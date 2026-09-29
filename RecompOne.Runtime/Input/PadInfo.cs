using Silk.NET.SDL;

namespace RecompOne.Runtime.Input;

//how a host controller is described to players (the game's input layer and a port's launcher share this)
public static class PadInfo
{
    //SDL's GUID starts with the bus type (little endian): 03h USB, 05h Bluetooth, FFh virtual
    public static string Connection(string guid)
    {
        if (guid.Length < 4) return "";
        return guid[..4].ToLowerInvariant() switch
        {
            "0300" => "USB",
            "0500" => "Bluetooth",
            "ff00" => "virtual",
            _ => ""
        };
    }

    public static string FamilyName(GameControllerType t)
    {
        return t switch
        {
            GameControllerType.Xbox360 => "Xbox 360",
            GameControllerType.Xboxone => "Xbox One/Series",
            GameControllerType.PS3 => "PlayStation 3",
            GameControllerType.PS4 => "PlayStation 4",
            GameControllerType.PS5 => "PlayStation 5",
            GameControllerType.NintendoSwitchPro => "Nintendo Switch Pro",
            GameControllerType.NintendoSwitchJoyconLeft or GameControllerType.NintendoSwitchJoyconRight
                or GameControllerType.NintendoSwitchJoyconPair => "Nintendo Joy-Con",
            GameControllerType.Virtual => "virtual",
            _ => "generic"
        };
    }

    //the labels printed on a family's face buttons, in PlayStation order (Cross, Circle, Square, Triangle)
    public static string[] FaceLabels(string family)
    {
        if (family.StartsWith("PlayStation", StringComparison.Ordinal)) return ["Cross", "Circle", "Square", "Triangle"];
        if (family.StartsWith("Nintendo", StringComparison.Ordinal)) return ["B", "A", "Y", "X"];
        return ["A", "B", "X", "Y"];
    }
}
