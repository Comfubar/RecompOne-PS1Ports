namespace RecompOne.Runtime.Config;

public class KeyBindings
{
    public string Cross { get; set; } = "Z";
    public string Circle { get; set; } = "X";
    public string Square { get; set; } = "A";
    public string Triangle { get; set; } = "S";
    public string L1 { get; set; } = "Q";
    public string R1 { get; set; } = "W";
    public string L2 { get; set; } = "E";
    public string R2 { get; set; } = "R";
    public string L3 { get; set; } = "F";
    public string R3 { get; set; } = "G";
    public string Start { get; set; } = "Enter";
    public string Select { get; set; } = "ShiftRight";
    public string Up { get; set; } = "Up";
    public string Down { get; set; } = "Down";
    public string Left { get; set; } = "Left";
    public string Right { get; set; } = "Right";

    //a second layout on the other side of the keyboard, for a player 2 on the same keyboard (not on by default: a
    //keyboard player counts as a connected pad, see GameConfig.Keys2)
    public static KeyBindings DefaultPlayer2()
    {
        return new KeyBindings
        {
            Cross = "K", Circle = "L", Square = "J", Triangle = "I",
            L1 = "U", R1 = "O", L2 = "Number7", R2 = "Number9",
            L3 = "Number8", R3 = "Number0", Start = "Backspace", Select = "Backslash",
            Up = "Keypad8", Down = "Keypad5", Left = "Keypad4", Right = "Keypad6"
        };
    }

    public static KeyBindings Empty()
    {
        return new KeyBindings
        {
            Cross = "", Circle = "", Square = "", Triangle = "",
            L1 = "", R1 = "", L2 = "", R2 = "",
            L3 = "", R3 = "", Start = "", Select = "",
            Up = "", Down = "", Left = "", Right = ""
        };
    }
}

public enum PadKind
{
    Digital,
    Analog
}

public enum MultitapMode
{
    //a multitap is in port 1 whenever more than two players have a controller
    Auto,
    On,
    Off
}

public class GamepadBindings
{
    public int[] Cross { get; set; } = [0];
    public int[] Circle { get; set; } = [1];
    public int[] Square { get; set; } = [2];
    public int[] Triangle { get; set; } = [3];
    public int[] L1 { get; set; } = [9];
    public int[] R1 { get; set; } = [10];
    public int[] L2 { get; set; } = [100];
    public int[] R2 { get; set; } = [101];
    public int[] L3 { get; set; } = [7];
    public int[] R3 { get; set; } = [8];
    public int[] Start { get; set; } = [6];
    public int[] Select { get; set; } = [4];
    public int[] Up { get; set; } = [11, 104];
    public int[] Down { get; set; } = [12, 105];
    public int[] Left { get; set; } = [13, 102];
    public int[] Right { get; set; } = [14, 103];

    public int LeftStickX { get; set; } = 0;
    public int LeftStickY { get; set; } = 1;
    public int RightStickX { get; set; } = 2;
    public int RightStickY { get; set; } = 3;

    public static GamepadBindings DefaultAnalog()
    {
        return new GamepadBindings
        {
            Up = [11], Down = [12], Left = [13], Right = [14]
        };
    }

    public static GamepadBindings Empty()
    {
        return new GamepadBindings
        {
            Cross = [], Circle = [], Square = [], Triangle = [],
            L1 = [], R1 = [], L2 = [], R2 = [],
            L3 = [], R3 = [], Start = [], Select = [],
            Up = [], Down = [], Left = [], Right = []
        };
    }
}

public class GameConfig
{
    public string CdPath { get; set; } = "";
    public string CardAPath { get; set; } = "carda.sav";
    public string CardBPath { get; set; } = "cardb.sav";
    public bool CardAEnabled { get; set; } = true;
    public bool CardBEnabled { get; set; } = true;
    public float MasterVolume { get; set; } = 0.5f; // We don't wanna bust your ear drums out on initial run <3
    public float SpuVolume { get; set; } = 1.0f;
    public float XaVolume { get; set; } = 1.0f;
    public bool Muted { get; set; } = false;
    public KeyBindings Keys { get; set; } = new();
    //player 2 on the keyboard: empty by default, because a player with keys counts as a connected pad in port 2 (which
    //would hide the game's own "controller removed" handling for a player 2 pad); KeyBindings.DefaultPlayer2 fills it
    public KeyBindings Keys2 { get; set; } = KeyBindings.Empty();
    //players 1-4. The buttons use SDL's positional names (south = A on Xbox = Cross on PlayStation), so the same
    //defaults fit Xbox, PlayStation and Nintendo pads
    public GamepadBindings Pad { get; set; } = new();
    public GamepadBindings Pad2 { get; set; } = new();
    public GamepadBindings Pad3 { get; set; } = new();
    public GamepadBindings Pad4 { get; set; } = new();
    public string PadDevice { get; set; } = "";
    public string PadDevice2 { get; set; } = "";
    public string PadDevice3 { get; set; } = "";
    public string PadDevice4 { get; set; } = "";
    public GamepadBindings PadAnalog { get; set; } = GamepadBindings.DefaultAnalog();
    public GamepadBindings PadAnalog2 { get; set; } = GamepadBindings.DefaultAnalog();
    public GamepadBindings PadAnalog3 { get; set; } = GamepadBindings.DefaultAnalog();
    public GamepadBindings PadAnalog4 { get; set; } = GamepadBindings.DefaultAnalog();
    public PadKind PadKind { get; set; } = PadKind.Digital;
    public PadKind PadKind2 { get; set; } = PadKind.Digital;
    public PadKind PadKind3 { get; set; } = PadKind.Digital;
    public PadKind PadKind4 { get; set; } = PadKind.Digital;

    public MultitapMode Multitap { get; set; } = MultitapMode.Auto;

    //the game's DualShock motor commands are passed on to the player's pad (the game's own VIBRATION option decides
    //whether it sends any); off = never rumble. Strength scales both motors.
    public bool Vibration { get; set; } = true;
    public float VibrationStrength { get; set; } = 1.0f;

    //PlayStation 4/5 pads over Bluetooth only rumble in SDL's extended report mode, which stays on until the pad is
    //powered off and confuses other programs reading it through DirectInput; off by default, USB is not affected
    public bool PlayStationBluetoothRumble { get; set; } = false;

    //0..1 of the stick range that counts as centred, for the analog values and for sticks bound to buttons
    public float StickDeadzone { get; set; } = 0.20f;

    //controllers to leave alone, by SDL GUID or part of the name (for example a pad remapper that shows the same
    //physical pad twice, once as itself and once as a virtual Xbox pad)
    public List<string> IgnoredPads { get; set; } = [];

    public const int MaxPlayers = 4;

    public PadKind KindFor(int player)
    {
        return player switch { 0 => PadKind, 1 => PadKind2, 2 => PadKind3, _ => PadKind4 };
    }

    public string DeviceFor(int player)
    {
        return player switch { 0 => PadDevice, 1 => PadDevice2, 2 => PadDevice3, _ => PadDevice4 };
    }

    public void SetKind(int player, PadKind kind)
    {
        switch (player)
        {
            case 0: PadKind = kind; break;
            case 1: PadKind2 = kind; break;
            case 2: PadKind3 = kind; break;
            default: PadKind4 = kind; break;
        }
    }

    public void SetDevice(int player, string id)
    {
        switch (player)
        {
            case 0: PadDevice = id; break;
            case 1: PadDevice2 = id; break;
            case 2: PadDevice3 = id; break;
            default: PadDevice4 = id; break;
        }
    }

    //back to the default layout for the player's current pad kind
    public void ResetPad(int player)
    {
        var analog = KindFor(player) == PadKind.Analog;
        var fresh = analog ? GamepadBindings.DefaultAnalog() : new GamepadBindings();
        switch (player, analog)
        {
            case (0, true): PadAnalog = fresh; break;
            case (0, false): Pad = fresh; break;
            case (1, true): PadAnalog2 = fresh; break;
            case (1, false): Pad2 = fresh; break;
            case (2, true): PadAnalog3 = fresh; break;
            case (2, false): Pad3 = fresh; break;
            case (_, true): PadAnalog4 = fresh; break;
            default: Pad4 = fresh; break;
        }
    }

    public GamepadBindings PadFor(int player)
    {
        var analog = KindFor(player) == PadKind.Analog;
        return player switch
        {
            0 => analog ? PadAnalog : Pad,
            1 => analog ? PadAnalog2 : Pad2,
            2 => analog ? PadAnalog3 : Pad3,
            _ => analog ? PadAnalog4 : Pad4
        };
    }

    public List<string> ActiveMods { get; set; } = [];
}