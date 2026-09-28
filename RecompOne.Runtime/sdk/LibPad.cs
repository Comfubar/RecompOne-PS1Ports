using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

public static class LibPad
{
    private const byte Connected = 0x00;
    private const byte Disconnected = 0xFF;
    private const byte DigitalId = 0x41;
    private const uint PadStateDiscon = 0;
    private const uint PadStateStable = 6;

    private static uint _buf1;
    private static uint _buf2;

    private static readonly PadReadEvent _readEvent = new();
    private static int _smallMotorIdx = 0;
    private static int _largeMotorIdx = 1;

    internal static void Reset()
    {
        _buf1 = 0;
        _buf2 = 0;
    }

    internal static void Detach()
    {
        _buf1 = 0;
        _buf2 = 0;
    }

    public static void PadInitDirect(CpuContext c, IMemory m)
    {
        var a0 = c.A0; var a1 = c.A1; var a2 = c.A2;
        try
        {
            _buf1 = c.A0;
            _buf2 = c.A1;
            Log.Sdk($"PadInitDirect buf1=0x{_buf1:X8} buf2=0x{_buf2:X8}");
            c.V0 = 0;
        }
        finally
        {
            if (Input.InputTrace.On) Input.InputTrace.Call("PadInitDirect", $"0x{a0:X},0x{a1:X},0x{a2:X}", $"v0=0x{c.V0:X}");
        }
    }

    public static void PadStartCom(CpuContext c, IMemory m)
    {
        var a0 = c.A0; var a1 = c.A1; var a2 = c.A2;
        try
        {
            Refresh(m);
            c.V0 = 0;
        }
        finally
        {
            if (Input.InputTrace.On) Input.InputTrace.Call("PadStartCom", $"0x{a0:X},0x{a1:X},0x{a2:X}", $"v0=0x{c.V0:X}");
        }
    }

    public static void PadStopCom(CpuContext c, IMemory m)
    {
        var a0 = c.A0; var a1 = c.A1; var a2 = c.A2;
        try
        {
            c.V0 = 0;
        }
        finally
        {
            if (Input.InputTrace.On) Input.InputTrace.Call("PadStopCom", $"0x{a0:X},0x{a1:X},0x{a2:X}", $"v0=0x{c.V0:X}");
        }
    }

    public static void PadEnableCom(CpuContext c, IMemory m)
    {
        var a0 = c.A0; var a1 = c.A1; var a2 = c.A2;
        try
        {
            c.V0 = 0;
        }
        finally
        {
            if (Input.InputTrace.On) Input.InputTrace.Call("PadEnableCom", $"0x{a0:X},0x{a1:X},0x{a2:X}", $"v0=0x{c.V0:X}");
        }
    }

    public static void PadChkVsync(CpuContext c, IMemory m)
    {
        var a0 = c.A0; var a1 = c.A1; var a2 = c.A2;
        try
        {
            c.V0 = 1;
        }
        finally
        {
            if (Input.InputTrace.On) Input.InputTrace.Call("PadChkVsync", $"0x{a0:X},0x{a1:X},0x{a2:X}", $"v0=0x{c.V0:X}");
        }
    }

    public static void PadChkMtap(CpuContext c, IMemory m)
    {
        var a0 = c.A0; var a1 = c.A1; var a2 = c.A2;
        try
        {
            //1 = a multitap answers on that port
            c.V0 = TapIn(c.A0) ? 1u : 0u;
        }
        finally
        {
            if (Input.InputTrace.On) Input.InputTrace.Call("PadChkMtap", $"0x{a0:X},0x{a1:X},0x{a2:X}", $"v0=0x{c.V0:X}");
        }
    }

    public static void PadGetState(CpuContext c, IMemory m)
    {
        var a0 = c.A0; var a1 = c.A1; var a2 = c.A2;
        try
        {
            c.V0 = SlotPresent(c.A0) ? PadStateStable : PadStateDiscon;
        }
        finally
        {
            if (Input.InputTrace.On) Input.InputTrace.Call("PadGetState", $"0x{a0:X},0x{a1:X},0x{a2:X}", $"v0=0x{c.V0:X}");
        }
    }

    public static void PadInfoMode(CpuContext c, IMemory m)
    {
        var a0 = c.A0; var a1 = c.A1; var a2 = c.A2;
        try
        {
            //InfoModeCurID (1): 4 = digital pad, 7 = analog pad; InfoModeCurExID (2): 7 when it is a DualShock
            var slot = Controller.Read(Controller.SlotFor(c.A0));
            c.V0 = !SlotPresent(c.A0) ? 0u : c.A1 switch
            {
                1 => slot.Analog ? 7u : 4u,
                2 => slot.Analog ? 7u : 0u,
                _ => 0u
            };
        }
        finally
        {
            if (Input.InputTrace.On) Input.InputTrace.Call("PadInfoMode", $"0x{a0:X},0x{a1:X},0x{a2:X}", $"v0=0x{c.V0:X}");
        }
    }

    public static void PadInfoComb(CpuContext c, IMemory m)
    {
        var a0 = c.A0; var a1 = c.A1; var a2 = c.A2;
        try
        {
            c.V0 = 0;
        }
        finally
        {
            if (Input.InputTrace.On) Input.InputTrace.Call("PadInfoComb", $"0x{a0:X},0x{a1:X},0x{a2:X}", $"v0=0x{c.V0:X}");
        }
    }

    public static void PadInfoAct(CpuContext c, IMemory m)
    {
        var a0 = c.A0; var a1 = c.A1; var a2 = c.A2;
        try
        {
            c.V0 = (int)c.A2 < 0 ? 2u : 1u;
        }
        finally
        {
            if (Input.InputTrace.On) Input.InputTrace.Call("PadInfoAct", $"0x{a0:X},0x{a1:X},0x{a2:X}", $"v0=0x{c.V0:X}");
        }
    }

    public static void PadSetMainMode(CpuContext c, IMemory m)
    {
        var a0 = c.A0; var a1 = c.A1; var a2 = c.A2;
        try
        {
            c.V0 = 0;
        }
        finally
        {
            if (Input.InputTrace.On) Input.InputTrace.Call("PadSetMainMode", $"0x{a0:X},0x{a1:X},0x{a2:X}", $"v0=0x{c.V0:X}");
        }
    }

    public static void PadSetActAlign(CpuContext c, IMemory m)
    {
        var a0 = c.A0; var a1 = c.A1; var a2 = c.A2;
        try
        {
            if (!IsPort1(c.A0))
            {
                c.V0 = 1;
                return;
            }

            var ptr = c.A1;
            var len = c.A2;
            if (ptr == 0 || len < 2)
            {
                c.V0 = 0;
                return;
            }

            for (var i = 0; i < (int)len && i < 6; i++)
            {
                var v = m.ReadU8(ptr + (uint)i);
                if (v == 0x00) _smallMotorIdx = i;
                else if (v == 0x01) _largeMotorIdx = i;
            }

            c.V0 = 1;
        }
        finally
        {
            if (Input.InputTrace.On) Input.InputTrace.Call("PadSetActAlign", $"0x{a0:X},0x{a1:X},0x{a2:X}", $"v0=0x{c.V0:X}");
        }
    }

    public static void PadSetAct(CpuContext c, IMemory m)
    {
        var a0 = c.A0; var a1 = c.A1; var a2 = c.A2;
        try
        {
            if (!IsPort1(c.A0))
            {
                c.V0 = 1;
                return;
            }

            var ptr = c.A1;
            var len = c.A2;
            if (ptr == 0 || len == 0)
            {
                c.V0 = 0;
                return;
            }

            var small = _smallMotorIdx < (int)len ? m.ReadU8(ptr + (uint)_smallMotorIdx) : (byte)0;
            var large = _largeMotorIdx < (int)len ? m.ReadU8(ptr + (uint)_largeMotorIdx) : (byte)0;
            InputManager.SetRumble(large, small);
            c.V0 = 1;
        }
        finally
        {
            if (Input.InputTrace.On) Input.InputTrace.Call("PadSetAct", $"0x{a0:X},0x{a1:X},0x{a2:X}", $"v0=0x{c.V0:X}");
        }
    }

    public static void Refresh(IMemory m)
    {
        //PadInitDirect buffers hold one pad per port, slot A
        if (_buf1 != 0) WritePort(m, _buf1, 0);
        if (_buf2 != 0) WritePort(m, _buf2, 4);
    }

    private static bool TapIn(uint padPort)
    {
        return IsPort1(padPort) ? Controller.Multitap1 : Controller.Multitap2;
    }

    //slots B-D of a port only exist behind a multitap
    private static bool SlotPresent(uint padPort)
    {
        if ((padPort & 3u) != 0 && !TapIn(padPort)) return false;
        return Controller.Read(Controller.SlotFor(padPort)).Connected;
    }

    private static void WritePort(IMemory m, uint buf, int slot)
    {
        var p = ReadSlot(m, slot);
        WritePad(m, buf, p.Buttons, p.Connected, p.RightX, p.RightY, p.LeftX, p.LeftY, p.Analog);
    }

    //the slot as the game will see it, after PadReadEvent listeners (mods) had their say
    private static Controller.PadSlot ReadSlot(IMemory m, int slot)
    {
        var p = Controller.Read(slot);
        if (p.Connected && Event.HasAnyListeners<PadReadEvent>())
        {
            var e = _readEvent;
            e.Context = Runtime.Cpu!;
            e.Memory = m;
            e.Port = slot < 4 ? 0 : 1;
            e.Slot = slot & 3;
            e.Buttons = p.Buttons;
            Event.Dispatch(e);
            p.Buttons = e.Buttons;
        }

        Input.InputTrace.Data("LibPad.Refresh", slot, p.Connected, p.Buttons);
        return p;
    }

    private static bool IsPort1(uint port)
    {
        return (port & 0x10u) == 0;
    }

    private const byte AnalogId = 0x73;

    private static void WritePad(IMemory m, uint buf, ushort buttons, bool present, byte rx, byte ry, byte lx, byte ly,
        bool analog = false)
    {
        m.WriteU8(buf + 0, present ? Connected : Disconnected);
        m.WriteU8(buf + 1, present ? analog ? AnalogId : DigitalId : Disconnected);
        m.WriteU8(buf + 2, (byte)(buttons & 0xFF));
        m.WriteU8(buf + 3, (byte)(buttons >> 8));
        m.WriteU8(buf + 4, present ? rx : (byte)0x80);
        m.WriteU8(buf + 5, present ? ry : (byte)0x80);
        m.WriteU8(buf + 6, present ? lx : (byte)0x80);
        m.WriteU8(buf + 7, present ? ly : (byte)0x80);
    }
}