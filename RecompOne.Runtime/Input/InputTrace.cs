using RecompOne.Runtime.Dispatch;

namespace RecompOne.Runtime.Input;

//RECOMPONE_INPUT_TRACE=1: logs every change in what the game is given for a pad (per delivery path and slot) and
//every change in what the pad functions answer, together with the overlays that are active, so "the game never
//read the pad" and "it read it and got nothing" can be told apart
public static class InputTrace
{
    public static readonly bool On = Environment.GetEnvironmentVariable("RECOMPONE_INPUT_TRACE") == "1";

    private static readonly Dictionary<string, string> _last = [];

    private static string Programs()
    {
        return string.Join("+", Dispatcher.ActiveNames);
    }

    //a delivery path wrote/returned pad data for a slot
    public static void Data(string path, int slot, bool connected, ushort buttons)
    {
        if (!On) return;
        Changed($"data|{path}|{slot}", $"{(connected ? "on" : "off")} {buttons:X4}",
            $"{path} slot {Hardware.Controller.SlotName(slot)}");
    }

    //analog sticks, logged in steps of 16 so a resting stick does not flood the log
    public static void Sticks(string path, int slot, byte lx, byte ly, byte rx, byte ry)
    {
        if (!On) return;
        Changed($"stick|{path}|{slot}", $"L {lx >> 4:X}{ly >> 4:X} R {rx >> 4:X}{ry >> 4:X} (high nibbles)",
            $"{path} slot {Hardware.Controller.SlotName(slot)} sticks");
    }

    //a pad function was called (HLE or hardware), with what it answered
    public static void Call(string function, string args, string result)
    {
        if (!On) return;
        Changed($"call|{function}|{args}", result, $"{function}({args})");
    }

    private static void Changed(string key, string value, string what)
    {
        lock (_last)
        {
            if (_last.TryGetValue(key, out var prev) && prev == value) return;
            _last[key] = value;
        }

        Console.WriteLine($"[InputTrace] frame {Diagnostics.TestScript.Frame} [{Programs()}] {what} = {value}");
    }
}
