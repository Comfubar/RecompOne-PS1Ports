using System.Globalization;

namespace RecompOne.Runtime.Diagnostics;

//scripted actions for headless test runs, off unless RECOMPONE_SCRIPT points at a file. One action per line,
//the time is either a game frame (VSync presents since the process started, games often present at 30 fps)
//or wall clock seconds since the process started with an s suffix ("90s"):
//  <frame> reset                 same as System > Hard reset in the menu
//  <frame> quit                  ends the process with exit code 0
//  # comment
//pad lines (press, stick, plug, unplug) are handled by Input.ScriptedInput, which reads the same file
//
//RECOMPONE_SCRIPT_LIVE=<file>: lines appended to that file while the game runs are executed as they arrive, for
//driving a headless run step by step. A live line may leave out the time (run now) or use "+<n>s" (n seconds from
//now); besides the actions above it accepts "dump" (save the next frame to dumps/frames, see FrameDiagnostics)
public static class TestScript
{
    private sealed record Action(long Frame, double Seconds, string Verb, string[] Args, int Line);

    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private static readonly List<Action> _actions;
    private static long _frame;

    public static long Frame => _frame;
    public static IReadOnlyList<string> PadLines { get; private set; } = [];

    //after every field initializer, Load sets PadLines
    static TestScript()
    {
        _actions = Load();
    }

    private static List<Action> Load()
    {
        var path = Environment.GetEnvironmentVariable("RECOMPONE_SCRIPT");
        if (string.IsNullOrEmpty(path)) return [];
        if (!File.Exists(path)) throw new FileNotFoundException($"RECOMPONE_SCRIPT file not found: {path}");

        var actions = new List<Action>();
        var pad = new List<string>();
        var n = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            n++;
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long frame = -1;
            double seconds = -1;
            var timeOk = parts.Length >= 2 && (parts[0].EndsWith('s')
                ? double.TryParse(parts[0][..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                : long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out frame));
            if (!timeOk)
                throw new FormatException($"{path}:{n}: expected '<frame|seconds s> <action> ...', got '{raw}'");

            var verb = parts[1].ToLowerInvariant();
            switch (verb)
            {
                case "reset":
                case "quit":
                    actions.Add(new Action(frame, seconds, verb, parts[2..], n));
                    break;
                case "press":
                case "stick":
                case "plug":
                case "unplug":
                    pad.Add(line);
                    break;
                default:
                    throw new FormatException($"{path}:{n}: unknown action '{parts[1]}'");
            }
        }


        PadLines = pad;
        Console.WriteLine($"[TestScript] {path}: {actions.Count} action(s), {pad.Count} pad line(s)");
        return actions;
    }

    private static readonly string? LivePath = Environment.GetEnvironmentVariable("RECOMPONE_SCRIPT_LIVE");
    private static long _liveOffset;
    private static string _livePartial = "";

    //picks up lines appended to the live script, every few frames
    private static void PollLive()
    {
        if (string.IsNullOrEmpty(LivePath) || _frame % 10 != 0 || !File.Exists(LivePath)) return;
        using var fs = new FileStream(LivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _liveOffset) _liveOffset = 0; //file was replaced
        if (fs.Length == _liveOffset) return;
        fs.Seek(_liveOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var text = _livePartial + reader.ReadToEnd();
        _liveOffset = fs.Length;

        var lines = text.Split('\n');
        _livePartial = lines[^1]; //an unfinished last line waits for its newline
        var now = _clock.Elapsed.TotalSeconds;
        foreach (var raw in lines[..^1])
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var at = now;
            if (parts[0].StartsWith('+') && parts[0].EndsWith('s'))
            {
                at = now + double.Parse(parts[0][1..^1], CultureInfo.InvariantCulture);
                parts.RemoveAt(0);
            }

            var verb = parts[0].ToLowerInvariant();
            var timed = $"{at.ToString("0.###", CultureInfo.InvariantCulture)}s {string.Join(' ', parts)}";
            Console.WriteLine($"[TestScript] live: {line}");
            switch (verb)
            {
                case "reset":
                case "quit":
                    _actions.Add(new Action(-1, at, verb, parts.Skip(1).ToArray(), 0));
                    break;
                case "dump":
                    Hle.FrameDiagnostics.RequestDump();
                    break;
                case "press":
                case "stick":
                case "plug":
                case "unplug":
                    Input.ScriptedInput.Add(timed);
                    break;
                default:
                    Console.WriteLine($"[TestScript] live: unknown action '{parts[0]}'");
                    break;
            }
        }
    }

    //called once per game frame from Runtime.PresentFrame, on the game thread
    public static void Tick()
    {
        _frame++;
        PollLive();
        Input.ScriptedInput.Tick();
        MemoryWatch.Tick();
        var now = _clock.Elapsed.TotalSeconds;
        for (var i = 0; i < _actions.Count; i++)
        {
            var a = _actions[i];
            if (a.Frame == long.MinValue) continue; //already done
            var due = a.Seconds >= 0 ? now >= a.Seconds : _frame >= a.Frame;
            if (!due) continue;
            _actions[i] = a with { Frame = long.MinValue };
            Console.WriteLine($"[TestScript] frame {_frame} ({now:0.0} s): {a.Verb}");
            switch (a.Verb)
            {
                case "reset":
                    Runtime.HardReset();
                    break;
                case "quit":
                    //window and audio teardown belong to the thread that owns the window
                    Host.GpuJobs.Run(Runtime.Shutdown);
                    Environment.Exit(0);
                    break;
            }
        }
    }
}
