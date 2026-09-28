using System.Reflection;
using System.Text;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;

namespace RecompOne.Runtime.Diagnostics;

//writes logs/crash_<timestamp>.txt when the game dies (unhandled exception on any thread, or the game thread's
//outer frame) or stops making progress (watchdog). It only records, the crash still ends the process.
//The log folder is logs/ next to the exe, or RECOMPONE_LOG_DIR when set.
public static class CrashReporter
{
    private const int RingSize = 200;
    private const int ConsoleLines = 50;
    private const int WatchdogSeconds = 30;

    //last dispatcher calls, written by the game thread without allocating: one store and one increment per call
    private static readonly uint[] _ring = new uint[RingSize];
    private static long _ringPos;
    private static uint _lastUnmapped;
    private static long _progress;

    private static int _installed;
    private static int _reported;
    private static Thread? _watchdog;

    public static string LogDir =>
        Environment.GetEnvironmentVariable("RECOMPONE_LOG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(e.ExceptionObject as Exception, $"unhandled exception (terminating={e.IsTerminating})");

        _watchdog = new Thread(Watch) { IsBackground = true, Name = "crash-watchdog" };
        _watchdog.Start();
    }

    public static void RecordCall(uint addr)
    {
        _ring[_ringPos++ % RingSize] = addr;
        _progress++;
    }

    public static void RecordUnmapped(uint addr)
    {
        _lastUnmapped = addr;
    }

    //anything that proves the game thread is still alive (frames, dispatches)
    public static void Tick()
    {
        _progress++;
    }

    private static void Watch()
    {
        var last = -1L;
        var stalledSince = DateTime.UtcNow;
        var reportedStall = false;
        while (true)
        {
            Thread.Sleep(1000);
            var now = Interlocked.Read(ref _progress);
            if (now != last)
            {
                last = now;
                stalledSince = DateTime.UtcNow;
                reportedStall = false;
                continue;
            }

            //nothing ran yet (disc prompt, startup) is not a hang
            if (now == 0 || reportedStall) continue;
            if ((DateTime.UtcNow - stalledSince).TotalSeconds < WatchdogSeconds) continue;

            reportedStall = true;
            WriteReport(null, $"watchdog: the game thread made no progress for {WatchdogSeconds} s", "hang");
        }
    }

    private static readonly HashSet<string> _errorsSeen = [];

    //an exception the caller can keep running after (the window's event pump, a failed render): printed in full
    //and written to logs/error_<timestamp>.txt with the same state as a crash report, once per site and type so
    //something failing every frame does not flood the disk
    public static void ReportError(Exception e, string site)
    {
        Console.Error.WriteLine($"[CrashReporter] error in {site}: {e}");
        var key = $"{site}|{e.GetType().FullName}|{e.TargetSite}";
        lock (_errorsSeen)
        {
            if (!_errorsSeen.Add(key)) return;
        }

        WriteReport(e, $"error in {site} (execution continued)", "error");
    }

    public static string? Report(Exception? e, string reason)
    {
        //the first fatal report is the interesting one, a second one is usually a consequence of it
        if (Interlocked.Exchange(ref _reported, 1) != 0) return null;
        return WriteReport(e, reason, "crash");
    }

    private static string? WriteReport(Exception? e, string reason, string kind)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"RecompOne {kind} report, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"reason: {reason}");
            sb.AppendLine($"frame (vblank): {Interrupts.VBlankCount}   host frames: {Host.FrameClock.Count}");
            sb.AppendLine($"last unmapped address: 0x{_lastUnmapped:X8}");
            sb.AppendLine($"active overlays: {string.Join(", ", Dispatcher.ActiveNames)}");
            sb.AppendLine();

            sb.AppendLine("== exception ==");
            sb.AppendLine(e?.ToString() ?? "(none)");
            sb.AppendLine();

            sb.AppendLine("== threads ==");
            sb.AppendLine($"reporting thread: {Thread.CurrentThread.Name ?? Environment.CurrentManagedThreadId.ToString()}");
            sb.AppendLine();

            sb.AppendLine("== cpu registers ==");
            AppendRegisters(sb, Runtime.Cpu);
            sb.AppendLine();

            sb.AppendLine($"== last {RingSize} dispatcher calls (oldest first) ==");
            var end = Interlocked.Read(ref _ringPos);
            var start = Math.Max(0, end - RingSize);
            for (var i = start; i < end; i++)
            {
                sb.Append($"0x{_ring[i % RingSize]:X8}");
                sb.Append((i - start) % 8 == 7 ? '\n' : ' ');
            }

            sb.AppendLine();
            sb.AppendLine();

            sb.AppendLine($"== last {ConsoleLines} console lines ==");
            var lines = new List<string>();
            ConsoleMirror.SnapshotInto(lines);
            foreach (var l in lines.Skip(Math.Max(0, lines.Count - ConsoleLines))) sb.AppendLine(l);

            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, $"{kind}_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt");
            File.WriteAllText(path, sb.ToString());
            Console.Error.WriteLine($"[CrashReporter] {kind} report written to {path}");
            return path;
        }
        catch (Exception inner)
        {
            //writing the report must not replace the original failure, say why it could not be written
            Console.Error.WriteLine($"[CrashReporter] could not write the {kind} report: {inner}");
            return null;
        }
    }

    private static void AppendRegisters(StringBuilder sb, CpuContext? c)
    {
        if (c == null)
        {
            sb.AppendLine("(no cpu context yet)");
            return;
        }

        var n = 0;
        foreach (var f in typeof(CpuContext).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.FieldType != typeof(uint)) continue;
            sb.Append($"{f.Name,-3}={(uint)f.GetValue(c)!:X8}");
            sb.Append(++n % 6 == 0 ? '\n' : ' ');
        }

        sb.AppendLine();
    }
}
