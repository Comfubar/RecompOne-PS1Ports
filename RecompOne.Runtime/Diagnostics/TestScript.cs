using System.Globalization;
using System.Text;

namespace RecompOne.Runtime.Diagnostics;

//scripted actions for headless test runs, off unless RECOMPONE_SCRIPT points at a file.
//
//Timed lines start with a game frame (VSync presents since the process started, games often present at 30 fps) or
//wall clock seconds with an s suffix ("90s"):
//  <time> reset | quit | press ... | stick ... | plug ... | unplug ...
//
//Sequential lines (no time) run one after another from the start, each once the previous one is done, so a test
//can wait for what is on screen instead of guessing how long a screen takes:
//  step <text>                          names the following steps in the log ("[TestScript] STEP <text>")
//  sleep <seconds>
//  wait overlay <name>                  until that program's code is active (Dispatcher.ActiveNames)
//  wait ram <hex addr>[:u8|u16|u32] <==|!=|>=|<=> <value>   (value hex with 0x, or decimal)
//  wait stable <n>                      until the shown frame has not changed for n host frames
//  wait region <ref>                    until the shown frame matches reference <ref> (see ScreenProbe)
//  wait log <text>                      until a console line containing <text> is written after this step started
//  wait fmv (a movie is playing) | wait shown (not a black screen) | wait not <condition>
//  wait multitap on|off | wait pads <n> | wait player <1-4> <name part|none> | wait file <data file> (content changed)
//  until <condition> do <action> [every <s>]   repeats the action until the wait condition holds (menu steps)
//  wait gone <text>                     fails if a console line containing <text> appears within the time given
//  capture <ref> <x> <y> <w> <h>        saves that region of the shown frame as reference <ref>
//  press / stick / plug / unplug ...    as in ScriptedInput, without the time; press waits for the release
//  key <Key[+Key]> [ms]                 synthetic keyboard press through the window's keyboard handler
//  keydown <Key> / keyup <Key>
//  vpad attach <id> <family> [bt]       an SDL virtual pad (Input.VirtualPads.Families); bt gives it another GUID
//  vpad detach <id>
//  vpad press <id[,id]> <Button[+Button]> [ms]   SDL button names (a b x y back start dpup ... leftshoulder)
//  vpad axis <id[,id]> <axis> <value -32768..32767> [ms]   ms = hold then back to 0; without ms it stays
//  dump | fps | reset | quit [code] | ramdump <name>
//Any wait takes "within <seconds>" (default 60). A wait that times out fails the script: the reason and the shown
//frame (dumps/frames/fail_*.png) are logged and the process exits with code 3.
//
//RECOMPONE_SCRIPT_LIVE=<file>: lines appended to that file while the game runs are executed as they arrive (a line
//may start with "+<n>s" to run n seconds from now); in live mode a failed wait is logged and the next line runs.
public static class TestScript
{
    private sealed record Action(long Frame, double Seconds, string Verb, string[] Args, int Line);

    private sealed class Step(string text, string[] parts, int line, bool live)
    {
        public readonly string Text = text;
        public readonly string[] Parts = parts;
        public readonly int Line = line;
        public readonly bool Live = live;
        public double Started = -1;
        public double Timeout = 60;
        public object? State;
    }

    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private static readonly List<Action> _actions;
    private static readonly Queue<Step> _steps = new();
    private static Step? _current;
    private static long _frame;
    private static string _stepName = "";

    public static long Frame => _frame;
    public static IReadOnlyList<string> PadLines { get; private set; } = [];

    //after every field initializer, Load sets PadLines
    static TestScript()
    {
        _actions = Load();
    }

    private static readonly string[] SequentialVerbs =
    [
        "step", "sleep", "wait", "capture", "press", "stick", "plug", "unplug", "key", "keydown", "keyup", "vpad",
        "dump", "fps", "reset", "quit", "ramdump", "until"
    ];

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

            if (!char.IsDigit(parts[0][0]))
            {
                var verb = parts[0].ToLowerInvariant();
                if (!SequentialVerbs.Contains(verb)) throw new FormatException($"{path}:{n}: unknown action '{parts[0]}'");
                _steps.Enqueue(new Step(line, parts, n, false));
                continue;
            }

            long frame = -1;
            double seconds = -1;
            var timeOk = parts.Length >= 2 && (parts[0].EndsWith('s')
                ? double.TryParse(parts[0][..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                : long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out frame));
            if (!timeOk)
                throw new FormatException($"{path}:{n}: expected '<frame|seconds s> <action> ...', got '{raw}'");

            switch (parts[1].ToLowerInvariant())
            {
                case "reset":
                case "quit":
                    actions.Add(new Action(frame, seconds, parts[1].ToLowerInvariant(), parts[2..], n));
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
        if (_steps.Count > 0) LogTee.Install();
        Console.WriteLine($"[TestScript] {path}: {actions.Count} timed action(s), {pad.Count} timed pad line(s), " +
                          $"{_steps.Count} sequential step(s)");
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
        foreach (var raw in lines[..^1])
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            LogTee.Install();
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            Console.WriteLine($"[TestScript] live: {line}");
            if (parts[0].StartsWith('+') && parts[0].EndsWith('s'))
            {
                _steps.Enqueue(new Step($"sleep {parts[0][1..^1]}", ["sleep", parts[0][1..^1]], 0, true));
                parts.RemoveAt(0);
            }

            if (!SequentialVerbs.Contains(parts[0].ToLowerInvariant()))
            {
                Console.WriteLine($"[TestScript] live: unknown action '{parts[0]}'");
                continue;
            }

            _steps.Enqueue(new Step(string.Join(' ', parts), parts.ToArray(), 0, true));
        }
    }

    //called once per game frame from Runtime.PresentFrame, on the game thread
    public static void Tick()
    {
        _frame++;
        FrameRate.MarkGame();
        PollLive();
        Input.ScriptedInput.Tick();
        MemoryWatch.Tick();
        RunSteps();
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
                    Quit(0);
                    break;
            }
        }
    }

    private static void Quit(int code)
    {
        Runtime.ExitCode = code;
        Console.Out.Flush();
        //window and audio teardown belong to the thread that owns the window
        Host.GpuJobs.Run(Runtime.Shutdown);
        Environment.Exit(code);
    }

    private static volatile Exception? _hookError;

    //an input test hook (virtual pad, synthetic key) failed on the input thread
    public static void ReportHookError(Exception e) => _hookError = e;

    private static void RunSteps()
    {
        //a few steps can finish in one frame (step names, keys, attaches); waits end the loop
        for (var guard = 0; guard < 32; guard++)
        {
            if (_current == null)
            {
                if (!_steps.TryDequeue(out _current)) return;
                _current.Started = _clock.Elapsed.TotalSeconds;
                Console.WriteLine($"[TestScript] > {(_current.Line > 0 ? $"line {_current.Line}: " : "")}{_current.Text}");
            }

            bool done;
            try
            {
                if (_hookError is { } he)
                {
                    _hookError = null;
                    throw new InvalidOperationException(he.Message, he);
                }

                done = Run(_current);
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or TimeoutException
                                          or FileNotFoundException or ArgumentException)
            {
                Fail(_current, e.Message);
                return;
            }

            if (!done) return;
            _current = null;
        }
    }

    private static void Fail(Step s, string reason)
    {
        var shot = SaveFailFrame();
        Console.WriteLine($"[TestScript] FAIL {(s.Line > 0 ? $"line {s.Line} " : "")}'{s.Text}'" +
                          $"{(_stepName.Length > 0 ? $" in step '{_stepName}'" : "")}: {reason}" +
                          $"{(shot != null ? $" (shown frame: {shot})" : "")}");
        _current = null;
        if (s.Live) return;
        Quit(3);
    }

    private static string? SaveFailFrame()
    {
        var f = ScreenProbe.Latest;
        if (f.Pixels == null) return null;
        var dir = Path.Combine(ScreenProbe.DumpRoot, "frames");
        Directory.CreateDirectory(dir);
        var file = Path.GetFullPath(Path.Combine(dir, $"fail_{DateTime.Now:yyyyMMdd_HHmmss}.png"));
        Assets.PngWriter.WriteRgba(file, f.Pixels, f.W, f.H);
        return file;
    }

    private static double Elapsed(Step s) => _clock.Elapsed.TotalSeconds - s.Started;

    //removes "within <seconds>" from the arguments
    private static string[] Args(Step s)
    {
        var list = s.Parts.ToList();
        var i = list.FindIndex(p => p.Equals("within", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < list.Count)
        {
            s.Timeout = double.Parse(list[i + 1].TrimEnd('s'), CultureInfo.InvariantCulture);
            list.RemoveRange(i, 2);
        }

        return list.ToArray();
    }

    private static bool Run(Step s)
    {
        var p = Args(s);
        var verb = p[0].ToLowerInvariant();
        switch (verb)
        {
            case "step":
                _stepName = string.Join(' ', p[1..]);
                Console.WriteLine($"[TestScript] STEP {_stepName}");
                return true;
            case "sleep":
                return Elapsed(s) >= double.Parse(p[1].TrimEnd('s'), CultureInfo.InvariantCulture);
            case "dump":
            {
                //done once the frame is on disk, so what follows in the log comes after it
                //"dump" is evidence: it waits (up to 10 s) for a frame that is not black; "dump any" takes the next one
                if (s.State == null)
                {
                    s.State = Hle.FrameDiagnostics.LastDump;
                    ScreenProbe.Enable();
                    Hle.FrameDiagnostics.RequestDump(nonBlackWithin: p.Length > 1 && p[1] == "any" ? 0 : 10);
                    return false;
                }

                if (!ReferenceEquals(Hle.FrameDiagnostics.LastDump, s.State)) return true;
                if (Elapsed(s) > 20) throw new TimeoutException("no frame was dumped within 20 s");
                return false;
            }
            case "fps":
                Console.WriteLine($"[TestScript] fps game={FrameRate.GameFps:0.0} host={FrameRate.HostFps:0.0}");
                return true;
            case "reset":
                Runtime.HardReset();
                return true;
            case "quit":
                Quit(p.Length > 1 ? int.Parse(p[1], CultureInfo.InvariantCulture) : 0);
                return true;
            case "capture":
                return Capture(s, p);
            case "ramdump":
            {
                //main RAM to <dumps>/ram_<name>.bin, for finding a game's variables by comparing dumps
                if (Runtime.Mem is not { } m) throw new InvalidOperationException("no memory yet");
                var ram = new byte[Runtime.RamSize];
                for (var i = 0; i < ram.Length; i++) ram[i] = m.ReadU8(0x80000000u + (uint)i);
                Directory.CreateDirectory(ScreenProbe.DumpRoot);
                var file = Path.GetFullPath(Path.Combine(ScreenProbe.DumpRoot, $"ram_{(p.Length > 1 ? p[1] : _frame.ToString(CultureInfo.InvariantCulture))}.bin"));
                File.WriteAllBytes(file, ram);
                Console.WriteLine($"[TestScript] ram saved to {file}");
                return true;
            }
            case "wait":
                return Wait(s, p);
            case "until":
                return Until(s, p);
            case "press":
            case "stick":
            case "plug":
            case "unplug":
                return PadStep(s, p);
            case "key":
            case "keydown":
            case "keyup":
                return KeyStep(s, p, verb);
            case "vpad":
                return VpadStep(s, p);
            default:
                throw new FormatException($"unknown action '{p[0]}'");
        }
    }

    private static bool Capture(Step s, string[] p)
    {
        if (p.Length < 6) throw new FormatException("capture <ref> <x> <y> <w> <h>");
        ScreenProbe.Enable();
        var f = ScreenProbe.Latest;
        if (f.Pixels == null)
        {
            if (Elapsed(s) > 10) throw new TimeoutException("nothing was shown within 10 s");
            return false;
        }

        int V(int i) => int.Parse(p[i], CultureInfo.InvariantCulture);
        var file = ScreenProbe.SaveReference(p[1], f, V(2), V(3), V(4), V(5));
        Console.WriteLine($"[TestScript] captured reference '{p[1]}' {V(4)}x{V(5)} at {V(2)},{V(3)} of a {f.W}x{f.H} frame -> {file}");
        return true;
    }

    private sealed class WaitState
    {
        public ScreenProbe.Reference? Ref;
        public long LogMark;
        public double Best = 1.0;
        public string? FileHash;
    }

    private static bool Wait(Step s, string[] p)
    {
        if (p.Length < 2) throw new FormatException("wait needs a condition");
        var st = (WaitState)(s.State ??= new WaitState { LogMark = LogTee.Mark });
        if (p[1].Equals("gone", StringComparison.OrdinalIgnoreCase))
        {
            //passes when the time runs out without the text
            var text = string.Join(' ', p[2..]);
            if (LogTee.SeenSince(st.LogMark, text)) throw new InvalidOperationException($"'{text}' was logged");
            if (Elapsed(s) < s.Timeout) return false;
            Console.WriteLine($"[TestScript] OK '{s.Text}': no '{text}' in {s.Timeout:0.#} s");
            return true;
        }

        var (met, detail) = Condition(p[1..], st);
        if (met)
        {
            Console.WriteLine($"[TestScript] OK '{s.Text}' after {Elapsed(s):0.0} s ({detail})");
            return true;
        }

        if (Elapsed(s) >= s.Timeout) throw new TimeoutException($"timed out after {s.Timeout:0.#} s ({detail})");
        return false;
    }

    //c = condition words without "wait": overlay <name> | ram ... | stable <n> | region <ref> | log <text>
    private static (bool, string) Condition(string[] c, WaitState st)
    {
        if (c[0].Equals("not", StringComparison.OrdinalIgnoreCase))
        {
            var (met, detail) = Condition(c[1..], st);
            return (!met, $"not: {detail}");
        }

        switch (c[0].ToLowerInvariant())
        {
            case "shown":
            {
                //something (not a black screen) is shown
                ScreenProbe.Enable();
                //for 10 host frames in a row, so a fade or reset right after does not count
                var f = ScreenProbe.Latest;
                return (f.ShownFor >= 10, f.ShownFor >= 10 ? $"{f.W}x{f.H} frame shown" : $"shown for {f.ShownFor} frame(s)");
            }
            case "fmv":
            {
                //a 24 bit display is the MDEC movie player
                var gpu = Runtime.Gpu;
                var on = gpu is { DisplayEnabled: true, Display24Bit: true };
                return (on, on ? "24 bit display (movie)" : "15 bit display");
            }
            case "overlay":
            {
                var names = Dispatch.Dispatcher.ActiveNames;
                return (names.Contains(c[1], StringComparer.OrdinalIgnoreCase), $"active: {string.Join(",", names)}");
            }
            case "ram":
                return RamCondition(["wait", .. c]);
            case "stable":
            {
                ScreenProbe.Enable();
                var n = int.Parse(c[1], CultureInfo.InvariantCulture);
                var f = ScreenProbe.Latest;
                var black = f.Pixels == null || ScreenProbe.IsBlack(f);
                return (!black && f.StableFor >= n, $"stable for {f.StableFor} frame(s){(black ? ", black" : "")}");
            }
            case "region":
            {
                ScreenProbe.Enable();
                st.Ref ??= ScreenProbe.LoadReference(c[1]);
                var f = ScreenProbe.Latest;
                var diff = ScreenProbe.Difference(st.Ref, f);
                st.Best = Math.Min(st.Best, diff);
                if (diff > 0.05)
                    return (false, $"best difference {st.Best:P1} (frame {f.W}x{f.H}, reference frame {st.Ref.FrameW}x{st.Ref.FrameH})");
                var r = st.Ref;
                return (true, $"difference {diff:P1}, region hash {ScreenProbe.Hash(f.Pixels!, r.X, r.Y, r.W, r.H, f.W):x16}");
            }
            case "log":
            {
                var text = string.Join(' ', c[1..]);
                var met = LogTee.SeenSince(st.LogMark, text);
                return (met, met ? $"'{text}' logged" : $"no line containing '{text}'");
            }
            case "multitap":
            {
                //multitap on|off: the tap the input layer puts in port 1 right now
                var want = c[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                return (Hardware.Controller.Multitap1 == want, $"multitap {(Hardware.Controller.Multitap1 ? "on" : "off")}");
            }
            case "pads":
            {
                //pads <n>: usable host controllers (after ignored pads and mirrors)
                var n = Host.InputManager.Devices.Count;
                return (n == int.Parse(c[1], CultureInfo.InvariantCulture), $"{n} usable pad(s)");
            }
            case "player":
            {
                //player <1-4> <part of the pad name | none>
                var p = int.Parse(c[1], CultureInfo.InvariantCulture) - 1;
                var name = Host.InputManager.PlayerDeviceName(p);
                var want = string.Join(' ', c[2..]);
                var met = want.Equals("none", StringComparison.OrdinalIgnoreCase)
                    ? name.Length == 0
                    : name.Contains(want, StringComparison.OrdinalIgnoreCase);
                return (met, $"P{p + 1} has '{name}'");
            }
            case "file":
            {
                //file <path> changed: the file's content differs from when the step started (a save was written)
                var path = Config.ConfigManager.DataPath(c[1]);
                var now = File.Exists(path) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))[..16] : "missing";
                st.FileHash ??= now;
                return (now != st.FileHash, $"{c[1]} sha256 {st.FileHash} -> {now}");
            }
            default:
                throw new FormatException($"unknown condition '{c[0]}' (overlay, ram, stable, region, log, gone, multitap, pads, player, file)");
        }
    }

    private sealed class UntilState
    {
        public readonly WaitState Wait = new() { LogMark = LogTee.Mark };
        public Step? Action;
        public double ActionEnded = -1;
        public int Tries;
    }

    //until <condition> do <action> [every <seconds>]: repeats the action (a press, a key, ...) until the condition
    //holds; the condition is checked before the first action and "every" seconds (default 0.8) after each one
    private static bool Until(Step s, string[] p)
    {
        var at = Array.FindIndex(p, x => x.Equals("do", StringComparison.OrdinalIgnoreCase));
        if (at < 2 || at == p.Length - 1) throw new FormatException("until <condition> do <action> [every <seconds>]");
        var action = p[(at + 1)..].ToList();
        var every = 0.8;
        var e = action.FindIndex(x => x.Equals("every", StringComparison.OrdinalIgnoreCase));
        if (e >= 0)
        {
            every = double.Parse(action[e + 1].TrimEnd('s'), CultureInfo.InvariantCulture);
            action.RemoveRange(e, 2);
        }

        var st = (UntilState)(s.State ??= new UntilState());
        if (st.Action != null)
        {
            if (!Run(st.Action)) return false;
            st.Action = null;
            st.ActionEnded = _clock.Elapsed.TotalSeconds;
            return false;
        }

        var (met, detail) = Condition(p[1..at], st.Wait);
        if (met)
        {
            Console.WriteLine($"[TestScript] OK '{s.Text}' after {Elapsed(s):0.0} s, {st.Tries} action(s) ({detail})");
            return true;
        }

        if (Elapsed(s) >= s.Timeout)
            throw new TimeoutException($"timed out after {s.Timeout:0.#} s and {st.Tries} action(s) ({detail})");
        if (st.ActionEnded >= 0 && _clock.Elapsed.TotalSeconds - st.ActionEnded < every) return false;

        st.Tries++;
        st.Action = new Step(string.Join(' ', action), action.ToArray(), s.Line, s.Live) { Started = _clock.Elapsed.TotalSeconds };
        return false;
    }

    private static (bool, string) RamCondition(string[] p)
    {
        //wait ram <addr>[:size] <op> <value>
        if (p.Length < 5) throw new FormatException("wait ram <hex addr>[:u8|u16|u32] <==|!=|>=|<=> <value>");
        if (Runtime.Mem is not { } m) return (false, "no memory yet");
        var a = p[2].Split(':');
        var addr = uint.Parse(a[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber);
        var size = a.Length > 1 ? a[1].ToLowerInvariant() : "u32";
        uint v = size switch
        {
            "u8" => m.ReadU8(addr),
            "u16" => m.ReadU16(addr),
            "u32" => m.ReadU32(addr),
            _ => throw new FormatException($"unknown size '{a[1]}'")
        };
        var want = p[4].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(p[4][2..], NumberStyles.HexNumber)
            : uint.Parse(p[4], CultureInfo.InvariantCulture);
        var met = p[3] switch
        {
            "==" => v == want,
            "!=" => v != want,
            ">=" => v >= want,
            "<=" => v <= want,
            _ => throw new FormatException($"unknown comparison '{p[3]}'")
        };
        return (met, $"{addr:X8} = 0x{v:X}");
    }

    private static bool PadStep(Step s, string[] p)
    {
        s.State ??= Input.ScriptedInput.AddNow(string.Join(' ', p));
        if (p[0].ToLowerInvariant() is "plug" or "unplug") return true;
        //the next step starts a moment after the release, so two presses of one button stay two presses
        return Input.ScriptedInput.FinishedFor(s.State, 0.12);
    }

    private sealed class KeyState
    {
        public Silk.NET.Input.Key[] Keys = [];
        public double Ms;
        public bool Released;
        public double ReleasedAt;
    }

    private static bool KeyStep(Step s, string[] p, string verb)
    {
        if (p.Length < 2) throw new FormatException($"{verb} <Key>");
        var keys = p[1].Split('+').Select(k => Enum.TryParse<Silk.NET.Input.Key>(k, true, out var key)
            ? key
            : throw new FormatException($"unknown key '{k}'")).ToArray();
        if (verb != "key")
        {
            foreach (var k in keys) Host.InputManager.SyntheticKey(k, verb == "keydown");
            return true;
        }

        if (s.State is not KeyState ks)
        {
            ks = new KeyState { Keys = keys, Ms = p.Length > 2 ? double.Parse(p[2].Replace("ms", ""), CultureInfo.InvariantCulture) : 100 };
            s.State = ks;
            foreach (var k in keys) Host.InputManager.SyntheticKey(k, true);
            return false;
        }

        var t = Elapsed(s) * 1000;
        if (!ks.Released)
        {
            if (t < ks.Ms) return false;
            foreach (var k in ks.Keys) Host.InputManager.SyntheticKey(k, false);
            ks.Released = true;
            ks.ReleasedAt = t;
            return false;
        }

        return t - ks.ReleasedAt >= 120;
    }

    private sealed class VpadState
    {
        public double Ms = -1;
        public bool Released;
        public double ReleasedAt;
    }

    private static bool VpadStep(Step s, string[] p)
    {
        if (p.Length < 3) throw new FormatException("vpad attach|detach|press|axis <id> ...");
        var action = p[1].ToLowerInvariant();
        var ids = p[2].Split(',');
        switch (action)
        {
            case "attach":
            {
                if (p.Length < 4) throw new FormatException("vpad attach <id> <family> [bt]");
                var fam = Input.VirtualPads.FindFamily(p[3]) ??
                          throw new FormatException($"unknown pad family '{p[3]}' (" +
                                                    string.Join(", ", Input.VirtualPads.Families.Select(f => f.Key)) + ")");
                var suffix = p.Length > 4 && p[4].Equals("bt", StringComparison.OrdinalIgnoreCase) ? " (Bluetooth)" : "";
                Host.InputManager.RunOnInputThread(sdl =>
                    Input.VirtualPads.Attach(sdl ?? throw new InvalidOperationException("SDL is not available"), ids[0], fam, suffix));
                return true;
            }
            case "detach":
                Host.InputManager.RunOnInputThread(sdl =>
                    Input.VirtualPads.Detach(sdl ?? throw new InvalidOperationException("SDL is not available"), ids[0]));
                return true;
            case "press":
            {
                if (p.Length < 4) throw new FormatException("vpad press <id[,id]> <Button[+Button]> [ms]");
                var buttons = p[3].Split('+').Select(Input.VirtualPads.ButtonIndex).ToArray();
                if (s.State is not VpadState vs)
                {
                    s.State = new VpadState { Ms = p.Length > 4 ? double.Parse(p[4].Replace("ms", ""), CultureInfo.InvariantCulture) : 100 };
                    Host.InputManager.RunOnInputThread(sdl =>
                    {
                        foreach (var id in ids) Input.VirtualPads.SetButtons(sdl!, id, buttons, true);
                    });
                    return false;
                }

                var t = Elapsed(s) * 1000;
                if (!vs.Released)
                {
                    if (t < vs.Ms) return false;
                    Host.InputManager.RunOnInputThread(sdl =>
                    {
                        foreach (var id in ids) Input.VirtualPads.SetButtons(sdl!, id, buttons, false);
                    });
                    vs.Released = true;
                    vs.ReleasedAt = t;
                    return false;
                }

                return t - vs.ReleasedAt >= 120;
            }
            case "axis":
            {
                if (p.Length < 5) throw new FormatException("vpad axis <id[,id]> <axis> <value> [ms]");
                var axis = Input.VirtualPads.AxisIndex(p[3]);
                var value = short.Parse(p[4], CultureInfo.InvariantCulture);
                if (s.State is not VpadState vs)
                {
                    s.State = new VpadState { Ms = p.Length > 5 ? double.Parse(p[5].Replace("ms", ""), CultureInfo.InvariantCulture) : -1 };
                    Host.InputManager.RunOnInputThread(sdl =>
                    {
                        foreach (var id in ids) Input.VirtualPads.SetAxis(sdl!, id, axis, value);
                    });
                    return p.Length <= 5;
                }

                var t = Elapsed(s) * 1000;
                if (!vs.Released)
                {
                    if (t < vs.Ms) return false;
                    Host.InputManager.RunOnInputThread(sdl =>
                    {
                        foreach (var id in ids) Input.VirtualPads.SetAxis(sdl!, id, axis, 0);
                    });
                    vs.Released = true;
                    vs.ReleasedAt = t;
                    return false;
                }

                return t - vs.ReleasedAt >= 120;
            }
            default:
                throw new FormatException($"unknown vpad action '{p[1]}'");
        }
    }
}

//keeps the console lines written while a script runs, for "wait log" / "wait gone"
internal sealed class LogTee : TextWriter
{
    private static LogTee? _installed;
    private static readonly List<string> _lines = [];
    private static long _count;
    private readonly TextWriter _inner;
    private readonly StringBuilder _partial = new();

    private LogTee(TextWriter inner) => _inner = inner;

    public override Encoding Encoding => _inner.Encoding;

    public static void Install()
    {
        if (_installed != null) return;
        _installed = new LogTee(Console.Out);
        Console.SetOut(TextWriter.Synchronized(_installed));
    }

    //number of lines written so far
    public static long Mark
    {
        get
        {
            lock (_lines) return _count;
        }
    }

    public static bool SeenSince(long mark, string text)
    {
        lock (_lines)
        {
            var first = Math.Max(0, _lines.Count - (int)Math.Min(_count - mark, _lines.Count));
            for (var i = first; i < _lines.Count; i++)
                if (_lines[i].Contains(text, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }

    public override void Write(char value)
    {
        _inner.Write(value);
        if (value == '\n') Commit();
        else if (value != '\r') _partial.Append(value);
    }

    public override void Write(string? value)
    {
        _inner.Write(value);
        if (value == null) return;
        foreach (var c in value)
            if (c == '\n') Commit();
            else if (c != '\r') _partial.Append(c);
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        Write('\n');
    }

    public override void Flush() => _inner.Flush();

    private void Commit()
    {
        lock (_lines)
        {
            _lines.Add(_partial.ToString());
            if (_lines.Count > 4000) _lines.RemoveRange(0, 1000);
            _count++;
        }

        _partial.Clear();
    }
}
