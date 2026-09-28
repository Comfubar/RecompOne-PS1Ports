namespace RecompOne.Runtime.Hardware;

public static class Controller
{
    public const ushort Select = 1 << 0;
    public const ushort L3 = 1 << 1;
    public const ushort R3 = 1 << 2;
    public const ushort Start = 1 << 3;
    public const ushort Up = 1 << 4;
    public const ushort Right = 1 << 5;
    public const ushort Down = 1 << 6;
    public const ushort Left = 1 << 7;
    public const ushort L2 = 1 << 8;
    public const ushort R2 = 1 << 9;
    public const ushort L1 = 1 << 10;
    public const ushort R1 = 1 << 11;
    public const ushort Triangle = 1 << 12;
    public const ushort Circle = 1 << 13;
    public const ushort Cross = 1 << 14;
    public const ushort Square = 1 << 15;

    public static bool Analog;
    public static bool Analog2;

    //port 1 (first pad, or multitap slot 1A) and port 2 (2A), kept for the paths that only know two pads
    public static ushort State = 0xFFFF;
    public static byte RightX = 0x80;
    public static byte RightY = 0x80;
    public static byte LeftX = 0x80;
    public static byte LeftY = 0x80;

    public static ushort State2 = 0xFFFF;
    public static bool Connected2;
    public static byte RightX2 = 0x80;
    public static byte RightY2 = 0x80;
    public static byte LeftX2 = 0x80;
    public static byte LeftY2 = 0x80;

    public struct PadSlot
    {
        public bool Connected;
        public bool Analog;
        public ushort Buttons; //active low, 0xFFFF = nothing pressed
        public byte RightX, RightY, LeftX, LeftY;
    }

    //every place a pad can sit: 0-3 = port 1 slots A-D (B-D only exist with a multitap), 4-7 = port 2 slots A-D
    public const int SlotCount = 8;
    public static readonly PadSlot[] Slots = NewSlots();

    //the DualShock state of each slot (mode, config mode, motors), see DualShock
    public static readonly DualShock[] Pads = Enumerable.Range(0, SlotCount).Select(i => new DualShock(i)).ToArray();

    //a multitap is plugged into port 1 / port 2
    public static bool Multitap1;
    public static bool Multitap2;

    //libpad port numbers: 0x00-0x03 = 1A-1D, 0x10-0x13 = 2A-2D
    public static int SlotFor(uint padPort)
    {
        return ((padPort & 0x10u) != 0 ? 4 : 0) + (int)(padPort & 3u);
    }

    public static string SlotName(int slot)
    {
        return $"{(slot < 4 ? 1 : 2)}{(char)('A' + (slot & 3))}";
    }

    public static PadSlot EmptySlot => new() { Buttons = 0xFFFF, RightX = 0x80, RightY = 0x80, LeftX = 0x80, LeftY = 0x80 };

    private static PadSlot[] NewSlots()
    {
        var s = new PadSlot[SlotCount];
        for (var i = 0; i < SlotCount; i++) s[i] = EmptySlot;
        return s;
    }

    //what the game sees in a slot right now: the host devices plus scripted test input
    public static PadSlot Read(int slot)
    {
        var s = Slots[slot];
        Input.ScriptedInput.Apply(slot, ref s);
        return s;
    }
}
