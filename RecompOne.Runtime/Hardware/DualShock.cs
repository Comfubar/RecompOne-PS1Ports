namespace RecompOne.Runtime.Hardware;

//a DualShock (SCPH-1200) as the pad library talks to it over SIO0, one per slot. Besides the 42h read it knows the
//configuration commands games use to switch analog mode on and lock it, and to map the two motors: 43h enters and
//leaves config mode, 44h sets digital/analog + lock, 45h-47h/4Ch are the id queries, 4Dh maps the motors onto the
//bytes the console sends with every 42h. In config mode every reply has ID F3h. Reference: the PlayStation
//"Pad and Memory Card" protocol notes (psx-spx) and DuckStation's AnalogController.
//
//Positions count from the command byte: 0 = command (reply: ID), 1 = second byte (reply: 5Ah; the multitap uses it
//as its TAP byte), 2..7 = data. Used for a pad on a port and for each lane of a multitap multi-read.
public sealed class DualShock(int slot)
{
    public readonly int Slot = slot;

    //the mode the pad is in; it starts in the mode the player chose (the ANALOG button), the game can change it
    public bool AnalogMode { get; private set; }
    public bool ConfigMode { get; private set; }
    public bool Locked { get; private set; }

    //motor mapping from 4Dh: map[i] = 00h small motor, 01h large motor, FFh unused, for data byte i of a 42h
    private readonly byte[] _map = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    public byte SmallMotor { get; private set; }
    public byte LargeMotor { get; private set; }

    //when the game last sent a read, motors of a pad nobody polls any more are off
    public long LastPollTicks { get; private set; }

    private bool _present;
    private byte _cmd;
    private byte _arg0;
    //the config mode a 43h asked for; it takes effect when that command ends, which is its last byte or, when the
    //console stops clocking earlier, the start of the next command
    private bool? _pendingConfig;

    private static readonly byte[] Status45 = [0x01, 0x02, 0x00, 0x02, 0x01, 0x00];
    private static readonly byte[][] Info46 = [[0x00, 0x00, 0x01, 0x02, 0x00, 0x0A], [0x00, 0x00, 0x01, 0x01, 0x01, 0x14]];
    private static readonly byte[] Info47 = [0x00, 0x00, 0x02, 0x00, 0x01, 0x00];
    private static readonly byte[][] Info4C = [[0x00, 0x00, 0x00, 0x04, 0x00, 0x00], [0x00, 0x00, 0x00, 0x07, 0x00, 0x00]];

    //called with every sample of the slot: a pad that was just plugged in powers up in the chosen mode
    public void Track(in Controller.PadSlot pad)
    {
        if (pad.Connected == _present) return;
        _present = pad.Connected;
        AnalogMode = pad.Analog;
        ConfigMode = false;
        Locked = false;
        Array.Fill(_map, (byte)0xFF);
        _smallLevel = _largeLevel = 0;
        SetMotors(0, 0);
    }

    //the reply to byte <pos> of a command; more = the pad acknowledges and expects another byte
    public byte Reply(int pos, byte tx, in Controller.PadSlot pad, out bool more)
    {
        if (pos == 0) FlushBytes();
        var r = ReplyCore(pos, tx, pad, out more);
        if (BytesTrace)
        {
            _txBytes.Append($"{tx:X2} ");
            _rxBytes.Append($"{r:X2}{(more ? "" : ".")} ");
        }

        return r;
    }

    //RECOMPONE_PAD_BYTES=1: every command other than a plain read, as the bytes the console sent and the pad answered
    //("." = no ack after that byte), for checking the protocol against a game's pad library
    private static readonly bool BytesTrace = Environment.GetEnvironmentVariable("RECOMPONE_PAD_BYTES") == "1";
    private readonly System.Text.StringBuilder _txBytes = new(), _rxBytes = new();
    private static int _bytesLines;
    private string _lastMotorTx = "";

    private void FlushBytes()
    {
        if (!BytesTrace || _txBytes.Length == 0) return;
        var tx = _txBytes.ToString();
        //reads only when they carry motor data (anything but zeros after the first two bytes), and only when it changed
        var motorData = _cmd == 0x42 && tx.Length > 6 && tx[6..].Replace("00", "").Trim().Length > 0;
        if ((_cmd != 0x42 || ConfigMode || (motorData && tx != _lastMotorTx)) && _bytesLines++ < 2000)
            Console.WriteLine($"[PadBytes] frame {Diagnostics.TestScript.Frame} {Controller.SlotName(Slot)} tx {tx}| rx {_rxBytes}| config={ConfigMode} analog={AnalogMode}");
        if (_cmd == 0x42) _lastMotorTx = tx;
        _txBytes.Clear();
        _rxBytes.Clear();
    }

    private byte ReplyCore(int pos, byte tx, in Controller.PadSlot pad, out bool more)
    {
        more = false;
        if (pos == 0)
        {
            CommitConfig();
            _cmd = tx;
            FirstUse(tx);
            if (ConfigMode)
            {
                if (tx is < 0x40 or > 0x4F) return 0xFF;
                more = true;
                return 0xF3;
            }

            if (tx is not (0x42 or 0x43))
            {
                Input.InputTrace.Call("SIO0 pad command", $"slot {Controller.SlotName(Slot)} cmd 0x{tx:X2}",
                    "not a command outside config mode, no ack");
                return 0xFF;
            }

            if (tx == 0x42) LastPollTicks = Environment.TickCount64;
            more = true;
            return AnalogMode ? (byte)0x73 : (byte)0x41;
        }

        if (pos == 1)
        {
            more = true;
            return 0x5A;
        }

        var i = pos - 2;
        if (i is < 0 or > 5) return 0xFF;
        if (i == 0) _arg0 = tx;
        var length = ConfigMode || AnalogMode ? 6 : 2;
        more = i < length - 1;
        byte rx;

        switch (_cmd)
        {
            case 0x42:
                rx = ReadByte(i, pad);
                Motor(i, tx);
                if (!more) Input.InputTrace.Data("SIO0 DualShock read", Slot, true, pad.Buttons);
                break;
            case 0x43 when !ConfigMode:
                rx = ReadByte(i, pad);
                if (i == 0) _pendingConfig = tx == 0x01;
                if (!more) CommitConfig();
                break;
            case 0x43:
                rx = 0x00;
                if (i == 0) _pendingConfig = tx == 0x01;
                if (!more) CommitConfig();
                break;
            case 0x44:
                rx = 0x00;
                if (i == 0 && tx <= 0x01) SetMode(tx == 0x01);
                if (i == 1) Locked = tx == 0x03;
                break;
            case 0x45:
                rx = i == 2 ? (byte)(AnalogMode ? 0x01 : 0x00) : Status45[i];
                break;
            case 0x46:
                rx = i == 0 ? (byte)0x00 : Info46[_arg0 == 0x01 ? 1 : 0][i];
                break;
            case 0x47:
                rx = Info47[i];
                break;
            case 0x4C:
                rx = i == 0 ? (byte)0x00 : Info4C[_arg0 == 0x01 ? 1 : 0][i];
                break;
            case 0x4D:
                rx = _map[i];
                _map[i] = tx;
                if (!more)
                    Input.InputTrace.Call("SIO0 DualShock", $"slot {Controller.SlotName(Slot)} motor map",
                        string.Join(" ", _map.Select(b => b.ToString("X2"))));
                break;
            default:
                rx = 0x00;
                break;
        }

        return rx;
    }

    private static byte ReadByte(int i, in Controller.PadSlot pad)
    {
        return i switch
        {
            0 => (byte)pad.Buttons,
            1 => (byte)(pad.Buttons >> 8),
            2 => pad.RightX,
            3 => pad.RightY,
            4 => pad.LeftX,
            _ => pad.LeftY
        };
    }

    private byte _pendingSmall, _pendingLarge;

    //data byte i of a 42h drives the motor it is mapped to; the values take effect with the last byte
    private void Motor(int i, byte tx)
    {
        if (i == 0)
        {
            _pendingSmall = 0;
            _pendingLarge = 0;
        }

        if (_map[i] == 0x00) _pendingSmall = (byte)((tx & 0x01) != 0 ? 0xFF : 0x00);
        else if (_map[i] == 0x01) _pendingLarge = tx;
        var last = ConfigMode || AnalogMode ? 5 : 1;
        if (i == last) SetMotors(_pendingSmall, _pendingLarge);
    }

    //games pulse the motors (this one sends 250 and 0 on alternate frames for a weaker rumble); the motor's own
    //inertia smooths that on a real pad, here the level follows the commands with a ~100 ms time constant. The levels
    //are what the host pad is given (InputManager.ApplyRumble).
    private float _smallLevel, _largeLevel;
    public byte SmallLevel => (byte)Math.Round(_smallLevel);
    public byte LargeLevel => (byte)Math.Round(_largeLevel);
    private bool _rumbling;

    private void SetMotors(byte small, byte large)
    {
        const float k = 0.3f; //per read, the game reads the pad about 30 times a second
        _smallLevel += (small - _smallLevel) * k;
        _largeLevel += (large - _largeLevel) * k;
        if (small == 0 && _smallLevel < 4) _smallLevel = 0;
        if (large == 0 && _largeLevel < 4) _largeLevel = 0;

        if (small != SmallMotor || large != LargeMotor)
            Input.InputTrace.Call("SIO0 DualShock", $"slot {Controller.SlotName(Slot)} motors", $"small {small} large {large}");
        SmallMotor = small;
        LargeMotor = large;

        var on = _smallLevel >= 4 || _largeLevel >= 4;
        if (on == _rumbling) return;
        _rumbling = on;
        if (Environment.TickCount64 - _lastMotorLog < 1000 && on) return;
        _lastMotorLog = Environment.TickCount64;
        Console.WriteLine($"[Pad] {Controller.SlotName(Slot)} vibration {(on ? $"on (command small={small} large={large})" : "off")}");
    }

    private long _lastMotorLog;

    //which commands the game's pad library uses, once per command and program (a handful of lines per session)
    private static readonly HashSet<string> _seen = [];

    private void FirstUse(byte cmd)
    {
        var programs = string.Join("+", Dispatch.Dispatcher.ActiveNames);
        lock (_seen)
            if (!_seen.Add($"{Slot}|{cmd}|{programs}")) return;
        Console.WriteLine($"[Pad] {Controller.SlotName(Slot)} first command 0x{cmd:X2} from {programs}" +
                          $"{(ConfigMode ? " (config mode)" : "")}, pad in {(AnalogMode ? "analog" : "digital")} mode");
    }

    private void CommitConfig()
    {
        if (_pendingConfig is not { } on) return;
        _pendingConfig = null;
        if (on == ConfigMode) return;
        ConfigMode = on;
        Input.InputTrace.Call("SIO0 DualShock", $"slot {Controller.SlotName(Slot)}", on ? "config mode" : "config mode off");
    }

    private void SetMode(bool analog)
    {
        if (analog == AnalogMode) return;
        AnalogMode = analog;
        Console.WriteLine($"[Pad] {Controller.SlotName(Slot)} set to {(analog ? "analog" : "digital")} mode by the game");
    }
}
