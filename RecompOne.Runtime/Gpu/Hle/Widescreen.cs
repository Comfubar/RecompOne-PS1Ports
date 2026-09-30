using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Hle;

//Widescreen output: the render targets get side margins (GpuHle.WideAspect) and the GTE narrows its horizontal
//projection by the same factor, so 3D fills the wider picture with a wider field of view while 2D (HUD, menus,
//videos) keeps its original proportions, centred. "auto" matches the monitor when it is wider than 4:3.
public static class Widescreen
{
    public const string Key = "WideAspect";
    public static readonly string[] Modes = ["auto", "off", "16:9", "16:10"];

    public static float MonitorAspect { get; set; }

    public static string Mode
    {
        get
        {
            var mode = ConfigManager.View.GetString(Key, "auto");
            return Array.IndexOf(Modes, mode) >= 0 ? mode : "auto";
        }
    }

    public static void Apply()
    {
        var aspect = Resolve(Mode);
        GpuHle.WideAspect = aspect;
        var source = GpuHle.SourceAspect > 0f ? GpuHle.SourceAspect : GpuHle.BaseAspect;
        Gte.WideScaleFx = aspect > 0f ? (int)Math.Round(65536.0 * source / aspect) : 0x10000;
        Console.WriteLine($"[Display] widescreen {Mode}: " +
                          (aspect > 0f ? $"{aspect:0.###} (monitor {MonitorAspect:0.###})" : "off (4:3)"));
    }

    private static float Resolve(string mode)
    {
        return mode switch
        {
            "off" => 0f,
            "16:9" => 16f / 9f,
            "16:10" => 16f / 10f,
            _ => MonitorAspect > GpuHle.BaseAspect + 0.01f ? MonitorAspect : 0f
        };
    }
}
