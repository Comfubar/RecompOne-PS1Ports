using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.SDL;

namespace RecompOne.Runtime.Input;

//synthetic host controllers for tests (TestScript "vpad ..."), off unless a script uses them. They are SDL virtual
//joysticks, so they go through the same path as a real pad: SDL hotplug events, InputManager's device scan, mappings,
//bindings, deadzone and mirror detection. Each family carries that family's USB vendor/product id, so SDL reports the
//same controller type for it as for the real pad. Runs on the thread that owns SDL (InputManager.Poll).
public static unsafe class VirtualPads
{
    public sealed record Family(string Key, ushort Vendor, ushort Product, string Name, bool Mapped);

    //vendor/product ids as the real pads report them over USB
    public static readonly Family[] Families =
    [
        new("xbox360", 0x045E, 0x028E, "Xbox 360 Controller", true),
        new("xboxone", 0x045E, 0x02EA, "Xbox One Controller", true),
        new("xboxseries", 0x045E, 0x0B12, "Xbox Series X Controller", true),
        new("ds4", 0x054C, 0x09CC, "PS4 Controller", true),
        new("dualsense", 0x054C, 0x0CE6, "DualSense Wireless Controller", true),
        new("switchpro", 0x057E, 0x2009, "Nintendo Switch Pro Controller", true),
        new("joycons", 0x057E, 0x200E, "Nintendo Switch Joy-Con (L/R)", true),
        new("8bitdo", 0x2DC8, 0x6101, "8BitDo SN30 Pro", true),
        new("generic", 0x0079, 0x0006, "Generic USB Joystick", true),
        //a pad SDL has no mapping for: only a raw joystick, needs the raw fallback or "map this controller"
        new("unmapped", 0x1209, 0x7E57, "Unmapped Test Pad", false)
    ];

    private sealed class Pad
    {
        public required string Id;
        public required Family Family;
        public Joystick* Joy;
        public nint Name;
        public GCHandle Self;
    }

    private static readonly Dictionary<string, Pad> _pads = new(StringComparer.OrdinalIgnoreCase);

    public static int Count => _pads.Count;

    public static Family? FindFamily(string key)
    {
        return Families.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    //suffix: appended to the name (for example " (Bluetooth)"), which gives the same model another GUID, the way the
    //same pad differs between USB and Bluetooth
    public static void Attach(Sdl sdl, string id, Family family, string suffix)
    {
        if (_pads.ContainsKey(id)) throw new InvalidOperationException($"virtual pad '{id}' is already attached");
        var pad = new Pad { Id = id, Family = family };
        pad.Name = Marshal.StringToCoTaskMemUTF8(family.Name + suffix);
        pad.Self = GCHandle.Alloc(pad);

        var desc = new VirtualJoystickDesc
        {
            Version = 1, //SDL_VIRTUAL_JOYSTICK_DESC_VERSION
            Type = (ushort)(family.Mapped ? JoystickType.Gamecontroller : JoystickType.Unknown),
            Naxes = (ushort)GameControllerAxis.Max,
            Nbuttons = (ushort)GameControllerButton.Max,
            Nhats = 0,
            VendorId = family.Vendor,
            ProductId = family.Product,
            //a mapped pad gets SDL's automatic virtual mapping: button n = controller button n, axis n = axis n
            ButtonMask = family.Mapped ? (1u << (int)GameControllerButton.Max) - 1 : 0,
            AxisMask = family.Mapped ? (1u << (int)GameControllerAxis.Max) - 1 : 0,
            Name = (byte*)pad.Name,
            Userdata = (void*)GCHandle.ToIntPtr(pad.Self),
            Rumble = new PfnVvUsUsI(&OnRumble)
        };

        var index = sdl.JoystickAttachVirtualEx(&desc);
        if (index < 0) throw new InvalidOperationException($"SDL could not attach virtual pad '{id}': {sdl.GetErrorS()}");
        pad.Joy = sdl.JoystickOpen(index);
        if (pad.Joy == null) throw new InvalidOperationException($"SDL could not open virtual pad '{id}': {sdl.GetErrorS()}");
        _pads[id] = pad;
        Console.WriteLine($"[VirtualPad] attached '{id}': {family.Name}{suffix} {family.Vendor:x4}:{family.Product:x4} " +
                          $"({(family.Mapped ? "has a mapping" : "no mapping")}), device {index}");
    }

    public static void Detach(Sdl sdl, string id)
    {
        var pad = Get(id);
        var instance = sdl.JoystickInstanceID(pad.Joy);
        var n = sdl.NumJoysticks();
        var detached = false;
        for (var i = 0; i < n && !detached; i++)
        {
            if (sdl.JoystickGetDeviceInstanceID(i) != instance) continue;
            detached = sdl.JoystickDetachVirtual(i) == 0;
        }

        sdl.JoystickClose(pad.Joy);
        if (!detached) throw new InvalidOperationException($"SDL could not detach virtual pad '{id}': {sdl.GetErrorS()}");
        _pads.Remove(id);
        pad.Self.Free();
        Marshal.FreeCoTaskMem(pad.Name);
        Console.WriteLine($"[VirtualPad] detached '{id}'");
    }

    public static void DetachAll(Sdl sdl)
    {
        foreach (var id in _pads.Keys.ToArray()) Detach(sdl, id);
    }

    public static void SetButtons(Sdl sdl, string id, IEnumerable<int> buttons, bool down)
    {
        var pad = Get(id);
        foreach (var b in buttons)
            if (sdl.JoystickSetVirtualButton(pad.Joy, b, (byte)(down ? 1 : 0)) != 0)
                throw new InvalidOperationException($"virtual pad '{id}': button {b}: {sdl.GetErrorS()}");
    }

    public static void SetAxis(Sdl sdl, string id, int axis, short value)
    {
        var pad = Get(id);
        if (sdl.JoystickSetVirtualAxis(pad.Joy, axis, value) != 0)
            throw new InvalidOperationException($"virtual pad '{id}': axis {axis}: {sdl.GetErrorS()}");
    }

    private static Pad Get(string id)
    {
        return _pads.TryGetValue(id, out var p) ? p : throw new InvalidOperationException($"no virtual pad '{id}'");
    }

    //SDL calls this for SDL_GameControllerRumble on the virtual pad: proves what a real pad would have been sent
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnRumble(void* userdata, ushort low, ushort high)
    {
        var pad = (Pad)GCHandle.FromIntPtr((nint)userdata).Target!;
        Console.WriteLine($"[VirtualPad] '{pad.Id}' rumble low={low} high={high}");
        return 0;
    }

    //SDL button names used by scripts, in SDL_GameControllerButton order
    public static int ButtonIndex(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "a" or "south" => 0,
            "b" or "east" => 1,
            "x" or "west" => 2,
            "y" or "north" => 3,
            "back" or "select" => 4,
            "guide" => 5,
            "start" => 6,
            "leftstick" or "l3" => 7,
            "rightstick" or "r3" => 8,
            "leftshoulder" or "lb" => 9,
            "rightshoulder" or "rb" => 10,
            "dpup" or "up" => 11,
            "dpdown" or "down" => 12,
            "dpleft" or "left" => 13,
            "dpright" or "right" => 14,
            _ => int.TryParse(name, out var n) ? n : throw new FormatException($"unknown pad button '{name}'")
        };
    }

    public static int AxisIndex(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "leftx" or "lx" => 0,
            "lefty" or "ly" => 1,
            "rightx" or "rx" => 2,
            "righty" or "ry" => 3,
            "lefttrigger" or "lt" => 4,
            "righttrigger" or "rt" => 5,
            _ => int.TryParse(name, out var n) ? n : throw new FormatException($"unknown pad axis '{name}'")
        };
    }
}
