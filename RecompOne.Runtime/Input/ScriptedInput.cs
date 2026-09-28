using System.Globalization;
using RecompOne.Runtime.Diagnostics;
using RecompOne.Runtime.Hardware;

namespace RecompOne.Runtime.Input;

//pad input from the RECOMPONE_SCRIPT file (see TestScript), for headless tests. Lines:
//  <time> press <target> <Button[+Button...]> [length]   hold the buttons for <length>: "120ms" (wall clock, the
//                                                         default is 100ms, a normal press; slow screens need that long, fast auto repeating lists want 50ms) or a
//                                                         number of game frames
//  <time> stick <target> <lx> <ly> [length]                hold the left stick at lx,ly (0-255, 128 = centre)
//  <time> plug <target>                                   a virtual pad is connected there from now on
//  <time> unplug <target>                                 and removed again
//<time> = game frame or wall clock seconds ("12.5s"), <target> = slot (1A-1D, 2A-2D) or player (P1-P4, mapped
//to a slot the same way real controllers are). Buttons: Cross Circle Square Triangle L1 R1 L2 R2 L3 R3 Start
//Select Up Down Left Right.
public static class ScriptedInput
{
    private sealed class Entry
    {
        public string Verb = "";
        public long Frame = -1;
        public double Seconds = -1;
        public string Target = "";
        public ushort Mask;
        public byte Lx = 0x80, Ly = 0x80;
        public int Frames = -1;
        public double Ms = 100;
        public double StartedSec = -1;
        public long StartedAt = -1; //game frame the entry became active
        public int Slot = -1; //resolved when it starts
        public string Text = "";
    }

    private static readonly List<Entry> _entries = Parse(TestScript.PadLines);

    //a line from the live script (TestScript), already given an absolute time
    public static void Add(string line)
    {
        _entries.AddRange(Parse([line]));
    }
    private static readonly bool[] _plugged = new bool[Controller.SlotCount];
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private static long _evaluatedFrame = -1;

    public static bool Active => _entries.Count > 0;

    public static bool IsPlugged(int slot) => _plugged[slot];

    private static List<Entry> Parse(IReadOnlyList<string> lines)
    {
        var list = new List<Entry>();
        foreach (var line in lines)
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var e = new Entry { Verb = p[1].ToLowerInvariant(), Text = line };
            if (p[0].EndsWith('s')) e.Seconds = double.Parse(p[0][..^1], CultureInfo.InvariantCulture);
            else e.Frame = long.Parse(p[0], CultureInfo.InvariantCulture);
            if (p.Length < 3) throw new FormatException($"scripted input: missing target in '{line}'");
            e.Target = p[2].ToUpperInvariant();

            switch (e.Verb)
            {
                case "press":
                    if (p.Length < 4) throw new FormatException($"scripted input: missing buttons in '{line}'");
                    foreach (var b in p[3].Split('+')) e.Mask |= ButtonBit(b, line);
                    if (p.Length >= 5) ParseLength(e, p[4]);
                    break;
                case "stick":
                    if (p.Length < 5) throw new FormatException($"scripted input: stick needs lx ly in '{line}'");
                    e.Lx = byte.Parse(p[3], CultureInfo.InvariantCulture);
                    e.Ly = byte.Parse(p[4], CultureInfo.InvariantCulture);
                    if (p.Length >= 6) ParseLength(e, p[5]);
                    break;
                case "plug":
                case "unplug":
                    break;
                default:
                    throw new FormatException($"scripted input: unknown action '{p[1]}' in '{line}'");
            }

            list.Add(e);
        }

        return list;
    }

    //the game paces its logic by vblanks (wall clock), so a hold measured in presented frames can last much longer
    //than meant on a busy screen and trigger the menus' auto repeat
    private static void ParseLength(Entry e, string text)
    {
        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            e.Ms = double.Parse(text[..^2], CultureInfo.InvariantCulture);
            return;
        }

        e.Frames = int.Parse(text, CultureInfo.InvariantCulture);
        e.Ms = -1;
    }

    private static ushort ButtonBit(string name, string line)
    {
        return name.ToLowerInvariant() switch
        {
            "cross" or "x" => Controller.Cross,
            "circle" or "o" => Controller.Circle,
            "square" => Controller.Square,
            "triangle" => Controller.Triangle,
            "l1" => Controller.L1,
            "r1" => Controller.R1,
            "l2" => Controller.L2,
            "r2" => Controller.R2,
            "l3" => Controller.L3,
            "r3" => Controller.R3,
            "start" => Controller.Start,
            "select" => Controller.Select,
            "up" => Controller.Up,
            "down" => Controller.Down,
            "left" => Controller.Left,
            "right" => Controller.Right,
            _ => throw new FormatException($"scripted input: unknown button '{name}' in '{line}'")
        };
    }

    private static int ResolveSlot(string target)
    {
        if (target.Length == 2 && target[0] is '1' or '2' && target[1] is >= 'A' and <= 'D')
            return (target[0] == '1' ? 0 : 4) + (target[1] - 'A');
        if (target.Length == 2 && target[0] == 'P' && target[1] is >= '1' and <= '4')
            return Host.InputManager.SlotOfPlayer(target[1] - '1');
        throw new FormatException($"scripted input: unknown target '{target}' (use 1A-2D or P1-P4)");
    }

    //called every game frame, so a press starts on time even while nothing reads that pad
    public static void Tick()
    {
        if (_entries.Count > 0) Evaluate();
    }

    //starts entries that are due, once per game frame
    private static void Evaluate()
    {
        var frame = TestScript.Frame;
        if (frame == _evaluatedFrame) return;
        _evaluatedFrame = frame;
        var now = _clock.Elapsed.TotalSeconds;

        foreach (var e in _entries)
        {
            if (e.StartedAt >= 0) continue;
            var due = e.Seconds >= 0 ? now >= e.Seconds : frame >= e.Frame;
            if (!due) continue;

            e.StartedAt = frame;
            e.StartedSec = now;
            e.Slot = ResolveSlot(e.Target);
            if (e.Slot < 0)
            {
                Console.WriteLine($"[ScriptedInput] frame {frame}: '{e.Text}' skipped, {e.Target} has no slot right now");
                continue;
            }

            if (e.Verb == "plug") _plugged[e.Slot] = true;
            if (e.Verb == "unplug") _plugged[e.Slot] = false;
            Console.WriteLine($"[ScriptedInput] frame {frame} ({now:0.0} s): {e.Verb} {e.Target} -> slot {Controller.SlotName(e.Slot)}" +
                              (e.Verb == "press" ? $" buttons 0x{e.Mask:X4} for {(e.Ms >= 0 ? $"{e.Ms} ms" : $"{e.Frames} frames")}" :
                                  e.Verb == "stick" ? $" left stick {e.Lx},{e.Ly} for {(e.Ms >= 0 ? $"{e.Ms} ms" : $"{e.Frames} frames")}" : ""));
        }
    }

    public static void Apply(int slot, ref Controller.PadSlot s)
    {
        if (_entries.Count == 0) return;
        Evaluate();

        if (_plugged[slot]) s.Connected = true;
        var frame = TestScript.Frame;
        foreach (var e in _entries)
        {
            if (e.Verb is not ("press" or "stick") || e.Slot != slot || e.StartedAt < 0) continue;
            if (e.Ms >= 0 ? (_clock.Elapsed.TotalSeconds - e.StartedSec) * 1000 >= e.Ms : frame - e.StartedAt >= e.Frames) continue;
            s.Connected = true;
            if (e.Verb == "press")
            {
                s.Buttons &= (ushort)~e.Mask;
                continue;
            }

            s.LeftX = e.Lx;
            s.LeftY = e.Ly;
        }
    }
}
