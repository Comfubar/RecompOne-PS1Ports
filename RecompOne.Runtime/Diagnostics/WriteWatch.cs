using System.Diagnostics;
using System.Globalization;

namespace RecompOne.Runtime.Diagnostics;

//RECOMPONE_WATCH_WRITE=<hex address>:<length>[,...]: logs who writes into the watched RAM ranges. Every write is
//checked (all RAM writes take the slow path while this is on), and a line with the writing code's call stack is
//printed the first time each distinct writer touches a range, so a routine that rewrites it every frame shows up once.
public static class WriteWatch
{
    private const int StackDepth = 8;
    private const int MaxWriters = 200;

    private static readonly (uint Start, uint End)[] _ranges = Parse();
    private static readonly HashSet<string> _seen = [];

    public static bool Enabled => _ranges.Length > 0;

    private static (uint, uint)[] Parse()
    {
        var spec = Environment.GetEnvironmentVariable("RECOMPONE_WATCH_WRITE");
        if (string.IsNullOrWhiteSpace(spec)) return [];
        var list = new List<(uint, uint)>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Split(':');
            var addr = uint.Parse(p[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber);
            var len = p.Length > 1 ? uint.Parse(p[1], CultureInfo.InvariantCulture) : 4u;
            var phys = Memory.MemoryMap.ToPhysical(addr);
            list.Add((phys, phys + len));
        }

        Console.WriteLine($"[WriteWatch] {list.Count} range(s): {spec} (all RAM writes take the slow path)");
        return list.ToArray();
    }

    //physical RAM offset of a write that is about to happen
    public static void Check(uint phys, int bytes)
    {
        foreach (var (start, end) in _ranges)
        {
            if (phys + (uint)bytes <= start || phys >= end) continue;
            Report(phys, bytes, start);
            return;
        }
    }

    private static void Report(uint phys, int bytes, uint rangeStart)
    {
        var frames = new StackTrace(3, false).GetFrames();
        var names = frames.Select(f => f.GetMethod()?.Name).Where(n => n != null).Take(StackDepth).ToArray();
        var key = $"{rangeStart:X}:{string.Join('<', names)}";
        lock (_seen)
        {
            if (_seen.Count >= MaxWriters || !_seen.Add(key)) return;
        }

        var cpu = Runtime.Cpu;
        Console.WriteLine($"[WriteWatch] frame {TestScript.Frame} 0x{0x80000000u | phys:X8} ({bytes} byte(s)) by " +
                          $"{string.Join(" <- ", names)}{(cpu != null ? $" ra=0x{cpu.RA:X8}" : "")}");
    }
}
