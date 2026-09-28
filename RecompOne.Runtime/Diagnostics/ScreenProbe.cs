using System.Globalization;
using StbImageSharp;

namespace RecompOne.Runtime.Diagnostics;

//what the window shows, for screen-aware test waits (TestScript "wait stable" / "wait region" / "capture"). Only
//active while a script needs it: the host thread then reads every presented frame back (GlCore.ReadPresented) and
//hands it here. Reference regions are PNG files in RECOMPONE_TESTREFS (default "testrefs"), a local folder that is
//never committed since the images are the game's.
public static class ScreenProbe
{
    public sealed record Frame(byte[]? Pixels, int W, int H, long HostFrame, ulong Hash, int StableFor, int ShownFor = 0);

    private static volatile bool _wanted = Environment.GetEnvironmentVariable("RECOMPONE_SCRIPT") is { Length: > 0 } ||
                                           Environment.GetEnvironmentVariable("RECOMPONE_SCRIPT_LIVE") is { Length: > 0 };
    private static Frame _latest = new(null, 0, 0, 0, 0, 0);

    public static bool Wanted => _wanted;
    public static void Enable() => _wanted = true;
    public static Frame Latest => _latest;

    public static string DumpRoot => Environment.GetEnvironmentVariable("RECOMPONE_DUMP_DIR") is { Length: > 0 } d ? d : "dumps";
    public static string RefDir => Environment.GetEnvironmentVariable("RECOMPONE_TESTREFS") is { Length: > 0 } d ? d : "testrefs";

    //on the GL thread, once per host frame
    public static void OnPresented(byte[]? px, int w, int h, long hostFrame)
    {
        var hash = px == null ? 0UL : Hash(px);
        var prev = _latest;
        var stable = hash == prev.Hash && w == prev.W && h == prev.H ? prev.StableFor + 1 : 1;
        var frame = new Frame(px, w, h, hostFrame, hash, stable);
        _latest = frame with { ShownFor = IsBlack(frame) ? 0 : prev.ShownFor + 1 };
    }

    public static bool IsBlack(Frame f)
    {
        if (f.Pixels == null) return true;
        var px = f.Pixels;
        for (var i = 0; i < px.Length; i += 4)
            if (px[i] > 8 || px[i + 1] > 8 || px[i + 2] > 8)
                return false;
        return true;
    }

    public static ulong Hash(byte[] px, int x0 = 0, int y0 = 0, int rw = -1, int rh = -1, int stride = 0)
    {
        //FNV-1a over the RGB bytes
        var h = 14695981039346656037UL;
        if (rw < 0)
        {
            for (var i = 0; i < px.Length; i++)
                if ((i & 3) != 3) h = (h ^ px[i]) * 1099511628211UL;
            return h;
        }

        for (var y = y0; y < y0 + rh; y++)
        for (var x = x0; x < x0 + rw; x++)
        {
            var o = (y * stride + x) * 4;
            h = (h ^ px[o]) * 1099511628211UL;
            h = (h ^ px[o + 1]) * 1099511628211UL;
            h = (h ^ px[o + 2]) * 1099511628211UL;
        }

        return h;
    }

    public sealed record Reference(string Name, int FrameW, int FrameH, int X, int Y, int W, int H, byte[] Pixels);

    private static string RefPng(string name) => Path.Combine(RefDir, name + ".png");
    private static string RefRect(string name) => Path.Combine(RefDir, name + ".rect");

    public static Reference LoadReference(string name)
    {
        if (!File.Exists(RefPng(name)) || !File.Exists(RefRect(name)))
            throw new FileNotFoundException($"screen reference '{name}' not found in {Path.GetFullPath(RefDir)} " +
                                            "(record it with 'capture <name> <x> <y> <w> <h>')");
        var r = File.ReadAllText(RefRect(name)).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        using var fs = File.OpenRead(RefPng(name));
        var img = ImageResult.FromStream(fs, ColorComponents.RedGreenBlueAlpha);
        CheckContrast(name, img.Data);
        return new Reference(name, r[0], r[1], r[2], r[3], r[4], r[5], img.Data);
    }

    //a reference without anything bright in it (captured during a fade, or of an empty area) would match any dark
    //screen, so it is refused
    public static void CheckContrast(string name, byte[] rgba)
    {
        int peak = 0, bright = 0;
        for (var q = 0; q < rgba.Length; q += 4)
            peak = Math.Max(peak, Math.Max(rgba[q], Math.Max(rgba[q + 1], rgba[q + 2])));
        for (var q = 0; q < rgba.Length; q += 4)
            if (Math.Max(rgba[q], Math.Max(rgba[q + 1], rgba[q + 2])) >= Math.Max(64, peak * 6 / 10)) bright++;
        var share = bright / (double)(rgba.Length / 4);
        if (peak < 64 || share < 0.02)
            throw new InvalidOperationException($"screen reference '{name}' has almost nothing bright in it (peak {peak}, " +
                                                $"{share:P1} bright pixels): it would match any dark screen, capture it again");
    }

    //saves region x,y,w,h of the frame as reference <name>; returns the file written
    public static string SaveReference(string name, Frame f, int x, int y, int w, int h)
    {
        if (f.Pixels == null) throw new InvalidOperationException("nothing is shown right now, cannot capture a reference");
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > f.W || y + h > f.H)
            throw new ArgumentOutOfRangeException(nameof(x), $"region {x},{y} {w}x{h} is outside the {f.W}x{f.H} frame");
        Directory.CreateDirectory(RefDir);
        var region = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(f.Pixels, ((y + row) * f.W + x) * 4, region, row * w * 4, w * 4);
        CheckContrast(name, region);
        Assets.PngWriter.WriteRgba(RefPng(name), region, w, h);
        File.WriteAllText(RefRect(name), string.Create(CultureInfo.InvariantCulture, $"{f.W} {f.H} {x} {y} {w} {h}\n"));
        return Path.GetFullPath(RefPng(name));
    }

    //share of the region's pixels that do not match the reference (1.0 when the frame has another size or nothing is
    //shown). Menus animate their backgrounds behind static text, so the reference's bright pixels (the text, icons)
    //must match closely while its dark pixels only must not have turned bright; a reference with few bright pixels
    //is compared pixel by pixel.
    public static double Difference(Reference r, Frame f)
    {
        if (f.Pixels == null || f.W != r.FrameW || f.H != r.FrameH) return 1.0;
        //"bright" is relative to the reference's brightest pixel: grey menu text on a dark background counts too
        var peak = 0;
        for (var q = 0; q < r.Pixels.Length; q += 4)
            peak = Math.Max(peak, Math.Max(r.Pixels[q], Math.Max(r.Pixels[q + 1], r.Pixels[q + 2])));
        var brightMin = Math.Max(64, peak * 6 / 10);
        var darkMax = Math.Max(32, peak * 3 / 10);
        int bright = 0, badBright = 0, badDark = 0, badAll = 0;
        for (var row = 0; row < r.H; row++)
        for (var col = 0; col < r.W; col++)
        {
            var o = ((r.Y + row) * f.W + r.X + col) * 4;
            var q = (row * r.W + col) * 4;
            var refMax = Math.Max(r.Pixels[q], Math.Max(r.Pixels[q + 1], r.Pixels[q + 2]));
            var frameMax = Math.Max(f.Pixels[o], Math.Max(f.Pixels[o + 1], f.Pixels[o + 2]));
            var d = Math.Max(Math.Abs(f.Pixels[o] - r.Pixels[q]),
                Math.Max(Math.Abs(f.Pixels[o + 1] - r.Pixels[q + 1]), Math.Abs(f.Pixels[o + 2] - r.Pixels[q + 2])));
            if (d > 32) badAll++;
            if (refMax >= brightMin)
            {
                bright++;
                if (d > 48) badBright++;
            }
            else if (refMax < darkMax && frameMax >= brightMin)
            {
                badDark++;
            }
        }

        var total = r.W * r.H;
        if (bright < total / 50) return badAll / (double)total;
        return Math.Max(badBright / (double)bright, badDark / (double)(total - bright));
    }
}
