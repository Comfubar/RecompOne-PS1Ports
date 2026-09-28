using System.Globalization;
using System.Text;

namespace RecompOne.Runtime.Diagnostics;

//RECOMPONE_WATCH=<hex address>:<length>[,<hex address>:<length>...]: once per game frame, logs every watched RAM
//range whose bytes changed since the last frame (for example a game's pad receive buffers)
public static class MemoryWatch
{
    private static readonly (uint Addr, byte[] Last)[] _ranges = Parse();

    private static (uint, byte[])[] Parse()
    {
        var spec = Environment.GetEnvironmentVariable("RECOMPONE_WATCH");
        if (string.IsNullOrWhiteSpace(spec)) return [];
        var list = new List<(uint, byte[])>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Split(':');
            var addr = uint.Parse(p[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber);
            var len = p.Length > 1 ? int.Parse(p[1], CultureInfo.InvariantCulture) : 4;
            var last = new byte[len];
            Array.Fill(last, (byte)0xAA); //so the first frame is always logged
            list.Add((addr, last));
        }

        Console.WriteLine($"[Watch] {list.Count} range(s): {spec}");
        return list.ToArray();
    }

    public static void Tick()
    {
        if (_ranges.Length == 0 || Runtime.Mem is not { } m) return;
        foreach (var (addr, last) in _ranges)
        {
            var changed = false;
            for (var i = 0; i < last.Length; i++)
            {
                var b = m.ReadU8(addr + (uint)i);
                if (b == last[i]) continue;
                last[i] = b;
                changed = true;
            }

            if (!changed) continue;
            var sb = new StringBuilder();
            foreach (var b in last) sb.Append($"{b:X2}");
            Console.WriteLine($"[Watch] frame {TestScript.Frame} {addr:X8}: {sb}");
        }
    }
}
