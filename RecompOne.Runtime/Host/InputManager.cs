using System.Numerics;
using Silk.NET.Input;
using Silk.NET.SDL;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hardware;
using EventBus = RecompOne.Runtime.Events.Event;
using KeyboardEvent = RecompOne.Runtime.Events.KeyboardEvent;
using MouseEvent = RecompOne.Runtime.Events.MouseEvent;
using ControllerEvent = RecompOne.Runtime.Events.ControllerEvent;
using MouseAction = RecompOne.Runtime.Events.MouseAction;
using EvMouseButton = RecompOne.Runtime.Events.MouseButton;

namespace RecompOne.Runtime.Host;

internal static unsafe class InputManager
{
    private static IKeyboard? _keyboard;
    private static IMouse? _mouse;
    private static Sdl? _sdl;
    //one SDL controller per player (0: no pad for that player)
    private static readonly nint[] _pads = new nint[GameConfig.MaxPlayers];
    private static GameController* Pad(int player) => (GameController*)_pads[player];
    private static readonly int[] _playerSlot = [0, 4, -1, -1];
    private static readonly string[] _padIds = ["", "", "", ""];
    private static readonly bool[] _padLost = new bool[GameConfig.MaxPlayers];

    //pads found to be a second view of another pad (see Input.MirrorDetector), left alone until the program restarts
    private static readonly HashSet<string> _sessionIgnored = [];
    private static readonly Input.MirrorDetector _mirrors = new(GameConfig.MaxPlayers);

    private const int AxisThreshold = 8000;
    private const int StickThreshold = 16000;
    private const int LeftTrigger = 100;
    private const int RightTrigger = 101;
    private const int LeftStickLeft = 102;
    private const int LeftStickRight = 103;
    private const int LeftStickUp = 104;
    private const int LeftStickDown = 105;
    private const int RightStickLeft = 106;
    private const int RightStickRight = 107;
    private const int RightStickUp = 108;
    private const int RightStickDown = 109;
    private static bool _topBarToggle;
    private static bool _topBarChordHeld;
    private static bool _fullscreenToggle;


    //L3 + R3 together on player 1's pad toggles the menu bar, for handhelds without a keyboard (F1). The game's pad
    //runs in digital mode, which has no stick clicks, so the chord never reaches the game as input it uses.
    private static void PollTopBarChord()
    {
        var pad = Pad(0);
        if (_sdl == null || pad == null)
        {
            _topBarChordHeld = false;
            return;
        }

        var held = _sdl.GameControllerGetButton(pad, GameControllerButton.Leftstick) != 0 &&
                   _sdl.GameControllerGetButton(pad, GameControllerButton.Rightstick) != 0;
        if (held && !_topBarChordHeld) _topBarToggle = true;
        _topBarChordHeld = held;
    }

    public static bool ConsumeTopBarToggle()
    {
        var v = _topBarToggle;
        _topBarToggle = false;
        return v;
    }

    public static bool ConsumeFullscreenToggle()
    {
        var v = _fullscreenToggle;
        _fullscreenToggle = false;
        return v;
    }

    public static void Initialize(IInputContext input)
    {
        if (input.Keyboards.Count > 0)
        {
            _keyboard = input.Keyboards[0];
            _keyboard.KeyDown += OnKeyDown;
            _keyboard.KeyUp += OnKeyUp;
        }

        if (input.Mice.Count > 0)
        {
            _mouse = input.Mice[0];
            _mouse.MouseMove += OnMouseMove;
            _mouse.MouseDown += OnMouseDown;
            _mouse.MouseUp += OnMouseUp;
            _mouse.Scroll += OnScroll;
        }


        try
        {
            _sdl = Sdl.GetApi();
            //Windows backends: XInput (Xbox and XInput pads), HIDAPI (PlayStation 4/5 and Switch pads, USB and
            //Bluetooth, no DualSenseX/DS4Windows needed), DirectInput (generic pads), Windows.Gaming.Input.
            //RawInput is off: with it SDL hands XInput pads to its RawInput driver instead
            _sdl.SetHint("SDL_JOYSTICK_RAWINPUT", "0");
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI", "1");
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI_PS4", "1");
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI_PS5", "1");
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI_SWITCH", "1");
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI_JOY_CONS", "1");
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI_COMBINE_JOY_CONS", "1");
            //extended reports (needed for rumble on Bluetooth PlayStation pads) are only switched on when a rumble
            //actually goes to such a pad and the player allowed it (PlayStationBluetoothRumble): once on, the pad
            //keeps them until it is powered off and other programs that read it through DirectInput misbehave
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI_PS4_RUMBLE", "0");
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI_PS5_RUMBLE", "0");
            //bind by position, not by printed label, so south is Cross on every pad (SDL swaps A/B on Nintendo pads
            //by default)
            _sdl.SetHint("SDL_GAMECONTROLLER_USE_BUTTON_LABELS", "0");
            if (_sdl.InitSubSystem(Sdl.InitGamecontroller) != 0)
                throw new InvalidOperationException(_sdl.GetErrorS());
            SdlVersion();
            LoadMappings();
            Rescan();
        }
        catch (Exception e)
        {
            //the game still runs with the keyboard; the reason is in the log
            Console.WriteLine($"[Input] controllers unavailable, SDL could not start: {e.GetType().Name}: {e.Message}");
            _sdl = null;
        }
    }

    private static void SdlVersion()
    {
        Silk.NET.SDL.Version v;
        _sdl!.GetVersion(&v);
        Console.WriteLine($"[Input] SDL {v.Major}.{v.Minor}.{v.Patch}: XInput, HIDAPI (PS4, PS5, Switch, Joy-Con), " +
                          "DirectInput and Windows.Gaming.Input; RawInput off");
    }

    //the community mapping database next to the executable (SDL_GameControllerDB, see its license file), then the
    //player's own mappings (written by "map this controller"), which win for the same controller
    public const string MappingFile = "gamecontrollerdb.txt";
    public const string UserMappingFile = "gamecontrollerdb.user.txt";

    private static void LoadMappings()
    {
        foreach (var path in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, MappingFile), ConfigManager.DataPath(UserMappingFile)
                 })
        {
            if (!File.Exists(path)) continue;
            var n = AddMappings(path);
            Console.WriteLine($"[Input] {n} controller mapping(s) from {Path.GetFileName(path)}");
        }
    }

    //the lines for this platform, as SDL_GameControllerAddMappingsFromFile would take them; returns how many were used
    public static int AddMappings(string path)
    {
        if (_sdl == null) return 0;
        var n = 0;
        var line = 0;
        foreach (var raw in File.ReadLines(path))
        {
            line++;
            var m = raw.Trim();
            if (m.Length == 0 || m.StartsWith('#')) continue;
            if (m.Contains("platform:", StringComparison.Ordinal) && !m.Contains("platform:Windows,", StringComparison.Ordinal))
                continue;
            if (_sdl.GameControllerAddMapping(m) < 0)
                Console.WriteLine($"[Input] {Path.GetFileName(path)}:{line}: mapping not accepted: {_sdl.GetErrorS()}");
            else n++;
        }

        return n;
    }

    public static bool IsConnected => _pads[0] != 0;

    public static bool IsPadConnected(int player)
    {
        return player >= 0 && player < _pads.Length && _pads[player] != 0;
    }

    public static bool IsKeyDown(Key k)
    {
        return (_keyboard?.IsKeyPressed(k) ?? false) || SyntheticDown(k);
    }

    //test hooks (TestScript "key"/"vpad"), off unless a script uses them: work for the thread that owns SDL and the
    //window, run at the start of the next Poll
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action<Sdl?>> _hostJobs = new();
    private static readonly HashSet<Key> _synthKeys = [];

    public static void RunOnInputThread(Action<Sdl?> job) => _hostJobs.Enqueue(job);

    private static bool SyntheticDown(Key k)
    {
        lock (_synthKeys) return _synthKeys.Count > 0 && _synthKeys.Contains(k);
    }

    //a key event fed into the same handlers the window's keyboard uses
    public static void SyntheticKey(Key k, bool down)
    {
        _hostJobs.Enqueue(_ =>
        {
            lock (_synthKeys)
            {
                if (down) _synthKeys.Add(k);
                else _synthKeys.Remove(k);
            }

            if (_keyboard == null) return;
            if (down) OnKeyDown(_keyboard, k, 0);
            else OnKeyUp(_keyboard, k, 0);
        });
    }

    private static void RunHostJobs()
    {
        while (_hostJobs.TryDequeue(out var job))
            try
            {
                job(_sdl);
            }
            catch (Exception e)
            {
                //a failed test hook fails the script step that queued it
                Diagnostics.TestScript.ReportHookError(e);
            }
    }

    public static void Poll()
    {
        RunHostJobs();
        PollGamepadEvents();
        CheckMirrors();
        PollTopBarChord();

        var cfg = ConfigManager.Game;
        var players = new Controller.PadSlot[GameConfig.MaxPlayers];
        for (var p = 0; p < players.Length; p++)
        {
            var slot = Controller.EmptySlot;
            slot.Analog = cfg.KindFor(p) == PadKind.Analog;
            var keys = p switch { 0 => cfg.Keys, 1 => cfg.Keys2, _ => null };
            if (_keyboard != null && keys != null) slot.Buttons = KeyState(_keyboard, keys);
            //a player whose keys are pressed takes the keyboard back after their pad was unplugged
            if (slot.Buttons != 0xFFFF) _padLost[p] = false;
            //player 1 always has the keyboard, player 2 has it when keys are bound; while a player's pad is unplugged
            //the player counts as disconnected, so the game shows its own "controller removed" pause
            slot.Connected = !_padLost[p] && (p == 0 || (keys != null && HasAnyKey(keys)));

            var pad = Pad(p);
            if (_sdl != null && pad != null)
            {
                var bind = cfg.PadFor(p);
                slot.Buttons = PadState(pad, bind, slot.Buttons);
                slot.LeftX = Axis(pad, bind.LeftStickX);
                slot.LeftY = Axis(pad, bind.LeftStickY);
                slot.RightX = Axis(pad, bind.RightStickX);
                slot.RightY = Axis(pad, bind.RightStickY);
                slot.Connected = true;
            }

            players[p] = slot;
        }

        //scripted test pads plugged straight into a tap slot count as players too
        var inUse = 0;
        for (var p = 0; p < players.Length; p++)
            if (players[p].Connected) inUse = p + 1;
        for (var i = 1; i < 4; i++)
            if (Input.ScriptedInput.IsPlugged(i)) inUse = Math.Max(inUse, i + 1);

        var tap = cfg.Multitap switch
        {
            MultitapMode.On => true,
            MultitapMode.Off => false,
            _ => inUse > 2
        };

        if (tap != Controller.Multitap1)
            Console.WriteLine($"[Input] multitap in port 1: {(tap ? "yes" : "no")} ({cfg.Multitap}, {inUse} player(s))");
        Controller.SetMultitap1(tap);
        Controller.Multitap2 = false;

        //with a multitap players 1-4 are slots 1A-1D, without one player 1 is port 1 and player 2 port 2
        for (var p = 0; p < players.Length; p++)
            _playerSlot[p] = tap ? p : p switch { 0 => 0, 1 => 4, _ => -1 };

        var slots = new Controller.PadSlot[Controller.SlotCount];
        for (var i = 0; i < slots.Length; i++) slots[i] = Controller.EmptySlot;
        for (var p = 0; p < players.Length; p++)
            if (_playerSlot[p] >= 0) slots[_playerSlot[p]] = players[p];
            else if (players[p].Connected) WarnNoSlot(p);
        Array.Copy(slots, Controller.Slots, slots.Length);

        //the paths that only know two pads see slots 1A and 2A
        Controller.State = slots[0].Buttons;
        Controller.Analog = slots[0].Analog;
        Controller.LeftX = slots[0].LeftX;
        Controller.LeftY = slots[0].LeftY;
        Controller.RightX = slots[0].RightX;
        Controller.RightY = slots[0].RightY;
        ApplyRumble();

        Controller.State2 = slots[4].Buttons;
        Controller.Connected2 = slots[4].Connected;
        Controller.Analog2 = slots[4].Analog;
        Controller.LeftX2 = slots[4].LeftX;
        Controller.LeftY2 = slots[4].LeftY;
        Controller.RightX2 = slots[4].RightX;
        Controller.RightY2 = slots[4].RightY;
    }

    private static void CheckMirrors()
    {
        if (_sdl == null) return;
        var masks = new uint[_pads.Length];
        var open = new bool[_pads.Length];
        for (var p = 0; p < _pads.Length; p++)
        {
            if (_pads[p] == 0) continue;
            open[p] = true;
            for (var b = 0; b < (int)GameControllerButton.Max; b++)
                if (_sdl.GameControllerGetButton(Pad(p), (GameControllerButton)b) != 0)
                    masks[p] |= 1u << b;
        }

        var mirror = _mirrors.Observe(masks, open);
        if (mirror < 0) return;

        var twin = Array.FindIndex(open, o => o);
        Console.WriteLine($"[Input] P{mirror + 1} '{PadName(mirror)}' mirrors P{twin + 1} '{PadName(twin)}': the same pad is " +
                          "visible twice (a pad remapper such as DualSenseX or DS4Windows). Ignoring it for this session; " +
                          "add it to IgnoredPads (Settings > Input > Ignore this controller) to make that permanent.");
        _sessionIgnored.Add(_padIds[mirror]);
        Rescan();
    }

    private static readonly bool[] _warnedNoSlot = new bool[GameConfig.MaxPlayers];

    private static void WarnNoSlot(int player)
    {
        if (_warnedNoSlot[player]) return;
        _warnedNoSlot[player] = true;
        Console.WriteLine($"[Input] player {player + 1} has a controller but there is no port for it without a multitap " +
                          "(Multitap is Off in the settings)");
    }

    //which slot a player's controller is in (-1: none)
    public static int SlotOfPlayer(int player)
    {
        return player >= 0 && player < _playerSlot.Length ? _playerSlot[player] : -1;
    }

    public static int? GetFirstPressedPadButton(int pad = 0)
    {
        var ctrl = pad >= 0 && pad < _pads.Length ? Pad(pad) : null;
        if (_sdl == null || ctrl == null) return null;
        for (var b = 0; b < (int)GameControllerButton.Max; b++)
            if (_sdl.GameControllerGetButton(ctrl, (GameControllerButton)b) != 0)
                return b;
        if (Pressed(ctrl, LeftTrigger)) return LeftTrigger;
        if (Pressed(ctrl, RightTrigger)) return RightTrigger;
        for (var b = LeftStickLeft; b <= RightStickDown; b++)
            if (Pressed(ctrl, b))
                return b;
        return null;
    }

    private static bool IsStickBinding(int b)
    {
        return b is >= LeftStickLeft and <= RightStickDown;
    }

    private static (GameControllerAxis Axis, bool Positive) AxisBinding(int b)
    {
        return b switch
        {
            LeftStickLeft => (GameControllerAxis.Leftx, false),
            LeftStickRight => (GameControllerAxis.Leftx, true),
            LeftStickUp => (GameControllerAxis.Lefty, false),
            LeftStickDown => (GameControllerAxis.Lefty, true),
            RightStickLeft => (GameControllerAxis.Rightx, false),
            RightStickRight => (GameControllerAxis.Rightx, true),
            RightStickUp => (GameControllerAxis.Righty, false),
            _ => (GameControllerAxis.Righty, true)
        };
    }

    public static void Shutdown()
    {
        CloseControllers();
        _sdl?.QuitSubSystem(Sdl.InitGamecontroller);
        _sdl?.Dispose();
        _sdl = null;
    }

    private static void PollGamepadEvents()
    {
        if (_sdl == null) return;
        Event ev;
        var changed = false;
        var anyCtrl = EventBus.HasAnyListeners<ControllerEvent>();
        while (_sdl.PollEvent(&ev) != 0)
        {
            if (ev.Type is (uint)EventType.Controllerdeviceadded or (uint)EventType.Controllerdeviceremoved
                or (uint)EventType.Joydeviceadded or (uint)EventType.Joydeviceremoved) changed = true;
            if (!anyCtrl) continue;
            if (ev.Type == (uint)EventType.Controllerbuttondown || ev.Type == (uint)EventType.Controllerbuttonup)
                EventBus.Dispatch(new ControllerEvent
                {
                    Device = ev.Cbutton.Which,
                    Button = ev.Cbutton.Button,
                    Pressed = ev.Type == (uint)EventType.Controllerbuttondown
                });
            else if (ev.Type == (uint)EventType.Controlleraxismotion)
                EventBus.Dispatch(new ControllerEvent
                {
                    Device = ev.Caxis.Which,
                    Axis = ev.Caxis.Axis,
                    Value = ev.Caxis.Value / 32768f
                });
        }

        if (changed) Rescan();
    }

    private static void CloseControllers()
    {
        for (var p = 0; p < _pads.Length; p++)
        {
            if (_pads[p] == 0) continue;
            _sdl?.GameControllerClose(Pad(p));
            _pads[p] = 0;
        }
    }

    public readonly record struct PadDevice(string Id, string Name);

    private static readonly List<PadDevice> _devices = [];

    public static IReadOnlyList<PadDevice> Devices
    {
        get
        {
            lock (_devices)
            {
                return _devices.ToArray();
            }
        }
    }

    public static void RefreshDevices()
    {
        Rescan();
    }

    private static string DeviceId(int joystickIndex)
    {
        if (_sdl == null) return "";
        var guid = _sdl.JoystickGetDeviceGUID(joystickIndex);
        var text = new byte[33];
        fixed (byte* p = text)
        {
            _sdl.JoystickGetGUIDString(guid, p, text.Length);
        }

        var len = Array.IndexOf(text, (byte)0);
        return System.Text.Encoding.ASCII.GetString(text, 0, len < 0 ? text.Length : len);
    }

    private static string DeviceName(int joystickIndex)
    {
        if (_sdl == null) return "";
        var name = _sdl.GameControllerNameForIndexS(joystickIndex);
        return string.IsNullOrWhiteSpace(name) ? $"Controller {joystickIndex}" : name;
    }

    private static readonly HashSet<string> _generic = [];

    //a pad neither SDL nor the mapping files know: it gets a generic layout (the usual DirectInput order) so it works
    //right away, and the player is told to map it in the launcher ("map this controller"). False when it cannot.
    private static bool AddGenericMapping(int index)
    {
        if (_sdl == null) return false;
        var guid = DeviceId(index);
        var name = _sdl.JoystickNameForIndexS(index) ?? "Controller";
        var joy = _sdl.JoystickOpen(index);
        if (joy == null) return false;
        int buttons = _sdl.JoystickNumButtons(joy), axes = _sdl.JoystickNumAxes(joy), hats = _sdl.JoystickNumHats(joy);
        _sdl.JoystickClose(joy);
        if (buttons < 4) return false;

        var parts = new List<string> { guid, name.Replace(",", " "), "platform:Windows" };
        string[] order = ["x", "a", "b", "y", "leftshoulder", "rightshoulder", "lefttrigger", "righttrigger", "back",
            "start", "leftstick", "rightstick"];
        for (var b = 0; b < Math.Min(buttons, order.Length); b++) parts.Add($"{order[b]}:b{b}");
        if (hats > 0) parts.AddRange(["dpup:h0.1", "dpright:h0.2", "dpdown:h0.4", "dpleft:h0.8"]);
        if (axes >= 2) parts.AddRange(["leftx:a0", "lefty:a1"]);
        if (axes >= 4) parts.AddRange(["rightx:a2", "righty:a3"]);
        var mapping = string.Join(",", parts) + ",";
        if (_sdl.GameControllerAddMapping(mapping) < 0) return false;
        if (_generic.Add(guid))
            Console.WriteLine($"[Input] '{name}' ({guid}) has no known mapping: using a generic layout ({buttons} buttons, " +
                              $"{axes} axes, {hats} hat(s)). If buttons are wrong, map it in the launcher " +
                              "(Controllers > Map this controller).");
        return _sdl.IsGameController(index) == SdlBool.True;
    }

    public static bool IsGenericMapping(string guid) => _generic.Contains(guid);

    public static string Connection(string guid) => Input.PadInfo.Connection(guid);

    //the family of a player's pad, for labels and rumble rules
    public static string Family(int player)
    {
        if (_sdl == null || _pads[player] == 0) return "";
        return FamilyName(_sdl.GameControllerGetType(Pad(player)));
    }

    public static string FamilyName(GameControllerType t) => Input.PadInfo.FamilyName(t);

    public static string PlayerConnection(int player)
    {
        if (_pads[player] == 0) return "";
        return Input.VirtualPads.IsSimulatedBluetooth(PadName(player)) ? "Bluetooth" : Connection(_padIds[player]);
    }

    private static readonly bool VirtualOnly = Environment.GetEnvironmentVariable("RECOMPONE_VIRTUAL_PADS_ONLY") == "1";

    private static void Rescan()
    {
        if (_sdl == null) return;
        CloseControllers();

        var found = new List<(int Index, string Id, string Name)>();
        var n = _sdl.NumJoysticks();
        for (var i = 0; i < n; i++)
        {
            if (_sdl.IsGameController(i) != SdlBool.True && !AddGenericMapping(i)) continue;
            var id = DeviceId(i);
            var name = DeviceName(i);
            if (_sessionIgnored.Contains(id)) continue;
            //tests: only the SDL virtual pads (their GUID has 'v' in byte 14), real pads on the test machine stay out
            if (VirtualOnly && !(id.Length == 32 && id.Substring(28, 2) == "76")) continue;
            if (ConfigManager.Game.IgnoredPads.Any(x => x.Length > 0 &&
                    (string.Equals(x, id, StringComparison.OrdinalIgnoreCase) ||
                     name.Contains(x, StringComparison.OrdinalIgnoreCase))))
                continue;
            found.Add((i, id, name));
        }

        lock (_devices)
        {
            _devices.Clear();
            foreach (var f in found) _devices.Add(new PadDevice(f.Id, f.Name));
        }

        //players with a chosen device first, then the others take the remaining pads in order
        var used = new HashSet<int>();
        for (var p = 0; p < _pads.Length; p++)
            if (!string.IsNullOrEmpty(ConfigManager.Game.DeviceFor(p)))
                _pads[p] = (nint)OpenFor(found, ConfigManager.Game.DeviceFor(p), used);
        for (var p = 0; p < _pads.Length; p++)
            if (string.IsNullOrEmpty(ConfigManager.Game.DeviceFor(p)))
                _pads[p] = (nint)OpenFor(found, "", used);

        for (var p = 0; p < _pads.Length; p++)
        {
            var id = _pads[p] == 0 ? "" : IdOf(p);
            //the pad this player had is gone (unplugged, not moved elsewhere by a rescan)
            if (id.Length == 0 && _padIds[p] is { Length: > 0 } old && found.All(f => f.Id != old))
            {
                _padLost[p] = true;
                Console.WriteLine($"[Input] P{p + 1}'s controller was unplugged");
            }

            if (id.Length > 0) _padLost[p] = false;
            _padIds[p] = id;
        }

        _mirrors.Reset();

        var summary = string.Join(", ", Enumerable.Range(0, _pads.Length).Select(p => _pads[p] == 0
            ? $"P{p + 1}=-"
            : $"P{p + 1}={PadName(p)} [{Family(p)}{(PlayerConnection(p) is { Length: > 0 } c ? ", " + c : "")}]"));
        Console.WriteLine($"[Input] controllers: {found.Count} usable, {summary}");
    }

    private static string IdOf(int player)
    {
        if (_sdl == null || _pads[player] == 0) return "";
        var joystick = _sdl.GameControllerGetJoystick(Pad(player));
        var guid = _sdl.JoystickGetGUID(joystick);
        var text = new byte[33];
        fixed (byte* t = text) _sdl.JoystickGetGUIDString(guid, t, text.Length);
        var len = Array.IndexOf(text, (byte)0);
        return System.Text.Encoding.ASCII.GetString(text, 0, len < 0 ? text.Length : len);
    }

    //the SDL name of the pad a player has, "" when none
    public static string PlayerDeviceName(int player)
    {
        var n = PadName(player);
        return n == "-" ? "" : n;
    }

    private static string PadName(int player)
    {
        if (_sdl == null || _pads[player] == 0) return "-";
        var name = _sdl.GameControllerNameS(Pad(player));
        return string.IsNullOrWhiteSpace(name) ? "pad" : name;
    }

    private static GameController* OpenFor(List<(int Index, string Id, string Name)> found, string wanted,
        HashSet<int> used)
    {
        if (_sdl == null) return null;

        var pick = -1;
        if (!string.IsNullOrEmpty(wanted))
        {
            foreach (var f in found)
                if (f.Id == wanted && used.Add(f.Index))
                {
                    pick = f.Index;
                    break;
                }

            if (pick < 0) return null;
        }
        else
        {
            foreach (var f in found)
                if (used.Add(f.Index))
                {
                    pick = f.Index;
                    break;
                }

            if (pick < 0) return null;
        }

        var ctrl = _sdl.GameControllerOpen(pick);
        if (ctrl == null) used.Remove(pick);
        return ctrl;
    }

    private static ushort KeyState(IKeyboard kb, KeyBindings cfg)
    {
        ushort s = 0xFFFF;

        void B(string keyName, ushort bit)
        {
            if (Enum.TryParse<Key>(keyName, out var k) && (kb.IsKeyPressed(k) || SyntheticDown(k)))
                s &= (ushort)~bit;
        }

        B(cfg.Cross, Controller.Cross);
        B(cfg.Circle, Controller.Circle);
        B(cfg.Square, Controller.Square);
        B(cfg.Triangle, Controller.Triangle);
        B(cfg.L1, Controller.L1);
        B(cfg.R1, Controller.R1);
        B(cfg.L2, Controller.L2);
        B(cfg.R2, Controller.R2);
        B(cfg.L3, Controller.L3);
        B(cfg.R3, Controller.R3);
        B(cfg.Start, Controller.Start);
        B(cfg.Select, Controller.Select);
        B(cfg.Up, Controller.Up);
        B(cfg.Down, Controller.Down);
        B(cfg.Left, Controller.Left);
        B(cfg.Right, Controller.Right);

        return s;
    }

    private static bool HasAnyKey(KeyBindings cfg)
    {
        return cfg.Cross.Length > 0 || cfg.Circle.Length > 0 || cfg.Square.Length > 0 || cfg.Triangle.Length > 0 ||
               cfg.L1.Length > 0 || cfg.R1.Length > 0 || cfg.L2.Length > 0 || cfg.R2.Length > 0 ||
               cfg.L3.Length > 0 || cfg.R3.Length > 0 || cfg.Start.Length > 0 || cfg.Select.Length > 0 ||
               cfg.Up.Length > 0 || cfg.Down.Length > 0 || cfg.Left.Length > 0 || cfg.Right.Length > 0;
    }

    private static byte Axis(GameController* ctrl, int index)
    {
        if (_sdl == null || index < 0) return 0x80;
        return AxisToByte(_sdl.GameControllerGetAxis(ctrl, (GameControllerAxis)index));
    }

    private static float Deadzone => Math.Clamp(ConfigManager.Game.StickDeadzone, 0f, 0.9f);

    private static ushort PadState(GameController* ctrl, GamepadBindings pad, ushort s)
    {
        s = Apply(ctrl, pad.Cross, Controller.Cross, s);
        s = Apply(ctrl, pad.Circle, Controller.Circle, s);
        s = Apply(ctrl, pad.Square, Controller.Square, s);
        s = Apply(ctrl, pad.Triangle, Controller.Triangle, s);
        s = Apply(ctrl, pad.L1, Controller.L1, s);
        s = Apply(ctrl, pad.R1, Controller.R1, s);
        s = Apply(ctrl, pad.L2, Controller.L2, s);
        s = Apply(ctrl, pad.R2, Controller.R2, s);
        s = Apply(ctrl, pad.L3, Controller.L3, s);
        s = Apply(ctrl, pad.R3, Controller.R3, s);
        s = Apply(ctrl, pad.Start, Controller.Start, s);
        s = Apply(ctrl, pad.Select, Controller.Select, s);
        s = Apply(ctrl, pad.Up, Controller.Up, s);
        s = Apply(ctrl, pad.Down, Controller.Down, s);
        s = Apply(ctrl, pad.Left, Controller.Left, s);
        s = Apply(ctrl, pad.Right, Controller.Right, s);
        return s;
    }

    private static ushort Apply(GameController* ctrl, int[] bindings, ushort bit, ushort s)
    {
        foreach (var binding in bindings)
            if (Pressed(ctrl, binding))
                return (ushort)(s & ~bit);
        return s;
    }

    private static bool Pressed(GameController* ctrl, int binding)
    {
        if (_sdl == null) return false;
        if (binding == LeftTrigger)
            return _sdl.GameControllerGetAxis(ctrl, GameControllerAxis.Triggerleft) > AxisThreshold;
        if (binding == RightTrigger)
            return _sdl.GameControllerGetAxis(ctrl, GameControllerAxis.Triggerright) > AxisThreshold;
        if (IsStickBinding(binding))
        {
            var (axis, positive) = AxisBinding(binding);
            var v = _sdl.GameControllerGetAxis(ctrl, axis);
            var threshold = Math.Max(StickThreshold, (int)(Deadzone * 32767));
            return positive ? v > threshold : v < -threshold;
        }

        return _sdl.GameControllerGetButton(ctrl, (GameControllerButton)binding) != 0;
    }

    private static byte AxisToByte(short axis)
    {
        //inside the deadzone is centre, outside it is rescaled so the full range is still reachable
        var raw = axis / 32768.0f;
        var dz = Deadzone;
        var mag = Math.Abs(raw);
        raw = mag <= dz ? 0f : Math.Sign(raw) * (mag - dz) / (1f - dz);
        var f = Math.Clamp(raw * 1.3f, -1.0f, 1.0f);
        return (byte)Math.Clamp((int)MathF.Round((f + 1.0f) * 127.5f), 0, 255);
    }

    //what each player's pad was last told to do, so SDL is only called on a change or to keep a running motor going
    private static readonly (ushort Low, ushort High, long At)[] _rumble = new (ushort, ushort, long)[GameConfig.MaxPlayers];

    //what a player's pad was last told (SDL low/high frequency motor, 0-65535)
    public static (ushort Low, ushort High) RumbleOf(int player) => (_rumble[player].Low, _rumble[player].High);

    //the motors of the DualShock in each player's slot go to that player's pad. A pad the game stopped polling is
    //treated as stopped, like a real DualShock, so a loading screen never leaves a motor running.
    private static void ApplyRumble()
    {
        if (_sdl == null) return;
        var cfg = ConfigManager.Game;
        var now = Environment.TickCount64;
        for (var p = 0; p < _pads.Length; p++)
        {
            if (_pads[p] == 0 || _playerSlot[p] < 0) continue;
            var ds = Controller.Pads[_playerSlot[p]];
            var live = now - ds.LastPollTicks < 250;
            if (live && (ds.SmallLevel != 0 || ds.LargeLevel != 0) && !RumbleAllowed(p)) continue;
            var strength = Math.Clamp(cfg.VibrationStrength, 0f, 1f);
            var low = cfg.Vibration && live ? (ushort)(ds.LargeLevel * 257 * strength) : (ushort)0;
            var high = cfg.Vibration && live ? (ushort)(ds.SmallLevel * 257 * strength) : (ushort)0;
            var last = _rumble[p];
            var running = low != 0 || high != 0;
            //small changes of a running motor wait for the next renewal
            var changed = Math.Abs(low - last.Low) > 2048 || Math.Abs(high - last.High) > 2048 ||
                          (low == 0) != (last.Low == 0) || (high == 0) != (last.High == 0);
            if (!changed && (!running || now - last.At < 200)) continue;
            //SDL stops the effect after the duration, it is renewed every 200 ms while a motor runs
            _sdl.GameControllerRumble(Pad(p), low, high, running ? 400u : 0u);
            _rumble[p] = (low, high, now);
        }
    }

    private static readonly bool[] _rumbleHeldBack = new bool[GameConfig.MaxPlayers];

    //rumble on a Bluetooth PlayStation pad switches it into extended reports until it is powered off (see the hints in
    //Initialize); that only happens when the player turned PlayStationBluetoothRumble on
    private static bool RumbleAllowed(int p)
    {
        var type = _sdl!.GameControllerGetType(Pad(p));
        if (type is not (GameControllerType.PS4 or GameControllerType.PS5)) return true;
        if (PlayerConnection(p) != "Bluetooth" || ConfigManager.Game.PlayStationBluetoothRumble) return true;
        if (!_rumbleHeldBack[p])
        {
            _rumbleHeldBack[p] = true;
            Console.WriteLine($"[Input] P{p + 1} '{PadName(p)}' is a Bluetooth PlayStation pad: vibration is held back " +
                              "(turn on 'Vibration over Bluetooth' / PlayStationBluetoothRumble to allow it)");
        }

        return false;
    }

    public static void SetRumble(byte large, byte small)
    {
        if (_sdl == null || _pads[0] == 0) return;
        var lo = (ushort)(large * 257);
        var hi = small != 0 ? (ushort)65535 : (ushort)0;
        var duration = large == 0 && small == 0 ? 0u : 500u;
        _sdl.GameControllerRumble(Pad(0), lo, hi, duration);
    }

    private static void OnKeyDown(IKeyboard kb, Key key, int _)
    {
        if (key == Key.F1) _topBarToggle = true;
        if (key == Key.F11) _fullscreenToggle = true;

        if (EventBus.HasAnyListeners<KeyboardEvent>())
            EventBus.Dispatch(new KeyboardEvent
            {
                Key = (int)key,
                Pressed = true
            });
    }

    private static void OnKeyUp(IKeyboard kb, Key key, int _)
    {
        if (EventBus.HasAnyListeners<KeyboardEvent>())
            EventBus.Dispatch(new KeyboardEvent
            {
                Key = (int)key,
                Pressed = false
            });
    }

    private static void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (EventBus.HasAnyListeners<MouseEvent>())
            EventBus.Dispatch(new MouseEvent
            {
                Action = MouseAction.Move,
                X = (int)position.X,
                Y = (int)position.Y
            });
    }

    private static void OnMouseDown(IMouse mouse, MouseButton mouseButton)
    {
        if (EventBus.HasAnyListeners<MouseEvent>())
            EventBus.Dispatch(new MouseEvent
            {
                Action = MouseAction.Button,
                Button = MapMouseButton(mouseButton),
                Pressed = true,
                X = (int)mouse.Position.X,
                Y = (int)mouse.Position.Y
            });
    }

    private static void OnMouseUp(IMouse mouse, MouseButton mouseButton)
    {
        if (EventBus.HasAnyListeners<MouseEvent>())
            EventBus.Dispatch(new MouseEvent
            {
                Action = MouseAction.Button,
                Button = MapMouseButton(mouseButton),
                Pressed = false,
                X = (int)mouse.Position.X,
                Y = (int)mouse.Position.Y
            });
    }

    private static void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (EventBus.HasAnyListeners<MouseEvent>())
            EventBus.Dispatch(new MouseEvent
            {
                Action = MouseAction.Wheel,
                Wheel = (int)wheel.Y,
                X = (int)mouse.Position.X,
                Y = (int)mouse.Position.Y
            });
    }

    private static EvMouseButton MapMouseButton(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => EvMouseButton.Left,
            MouseButton.Right => EvMouseButton.Right,
            MouseButton.Middle => EvMouseButton.Middle,
            _ => EvMouseButton.None
        };
    }
}