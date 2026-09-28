using RecompOne.Runtime.Assets;

using RecompOne.Runtime;

namespace RecompOne.Runtime.Hle;

//debug aid for running without looking at the window, both off unless the env var is set:
//  RECOMPONE_FRAME_STATS=N  logs the gpu work done over the last N host frames
//  RECOMPONE_FRAME_DUMP=N   every N host frames saves vram and the display area to dumps/frames/*.png
public static class FrameDiagnostics
{
    private static readonly int StatsEvery = ReadEnv("RECOMPONE_FRAME_STATS");
    private static readonly int DumpEvery = ReadEnv("RECOMPONE_FRAME_DUMP");

    //RECOMPONE_FRAME_LATEST=N: every N host frames dumps/frames/latest.png is overwritten with the display
    private static readonly int LatestEvery = ReadEnv("RECOMPONE_FRAME_LATEST");
    private static volatile bool _dumpRequested;

    //a one off dump of the next frame (live script "dump")
    public static void RequestDump() => _dumpRequested = true;

    private static long _frames;
    private static long _lastIrqs;
    private static readonly System.Diagnostics.Stopwatch _statClock = System.Diagnostics.Stopwatch.StartNew();
    private static double _lastStatSec;
    private static long _tri, _rect, _line, _fill, _copy, _upload;

    private static int ReadEnv(string name)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var n) && n > 0 ? n : 0;
    }

    public static void CountTri() => _tri++;
    public static void CountRect() => _rect++;
    public static void CountLine() => _line++;
    public static void CountFill() => _fill++;
    public static void CountCopy() => _copy++;
    public static void CountUpload() => _upload++;

    public static bool Enabled => StatsEvery > 0 || DumpEvery > 0 || LatestEvery > 0 ||
                                  Environment.GetEnvironmentVariable("RECOMPONE_SCRIPT_LIVE") is { Length: > 0 };

    public static void Announce()
    {
        if (Enabled) Console.WriteLine($"[FrameStats] enabled: stats every {StatsEvery}, dump every {DumpEvery} host frames");
    }

    public static void OnHostFrame(IGlVram vram, int dispX, int dispY, int w, int h, bool rgb24, bool displayOn,
        bool hle)
    {
        _frames++;

        if (StatsEvery > 0 && _frames % StatsEvery == 0)
        {
            var prims = _tri + _rect + _line;
            var now = _statClock.Elapsed.TotalSeconds;
            var irqs = Interrupts.VBlankIrqsServiced;
            var irqRate = (irqs - _lastIrqs) / Math.Max(0.001, now - _lastStatSec);
            _lastIrqs = irqs;
            _lastStatSec = now;
            Console.WriteLine($"[FrameStats] frame {_frames}: last {StatsEvery} frames tri={_tri} rect={_rect} line={_line} " +
                              $"fill={_fill} copy={_copy} upload={_upload} ({prims / (double)StatsEvery:0.0} prims/frame) " +
                              $"display={dispX},{dispY} {w}x{h}{(rgb24 ? " rgb24" : "")} on={displayOn} hle={hle} vblank={Interrupts.VBlankCount} vblankIrq/s={irqRate:0} gameFrame={Diagnostics.TestScript.Frame} " +
                              $"spuKeyOns={Diagnostics.AudioStats.KeyOns} xaSectors={Diagnostics.AudioStats.XaSectors} audioPeak={Diagnostics.AudioStats.TakePeak()} " +
                              $"workingSetMB={Environment.WorkingSet / 1048576} managedMB={GC.GetTotalMemory(false) / 1048576}");
            _tri = _rect = _line = _fill = _copy = _upload = 0;
        }

        if ((DumpEvery > 0 && _frames % DumpEvery == 0) || _dumpRequested)
        {
            _dumpRequested = false;
            Dump(vram, dispX, dispY, Math.Max(w, 1), Math.Max(h, 1), rgb24);
        }
        else if (LatestEvery > 0 && _frames % LatestEvery == 0)
        {
            Dump(vram, dispX, dispY, Math.Max(w, 1), Math.Max(h, 1), rgb24, latestOnly: true);
        }
    }

    private static void Dump(IGlVram vram, int dispX, int dispY, int w, int h, bool rgb24, bool latestOnly = false)
    {
        const int vw = VramShadow.Width, vh = VramShadow.Height;
        var px = new ushort[vw * vh];
        vram.ReadRect(0, 0, vw, vh, px);

        var dir = Path.Combine("dumps", "frames");
        Directory.CreateDirectory(dir);

        if (!latestOnly)
        {
            var full = new byte[vw * vh * 4];
            for (var i = 0; i < px.Length; i++) Rgb15(px[i], full, i * 4);
            PngWriter.WriteRgba(Path.Combine(dir, $"vram_{_frames:D6}.png"), full, vw, vh);
        }

        w = Math.Min(w, vw);
        h = Math.Min(h, vh);
        var disp = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = (y * w + x) * 4;
            if (!rgb24)
            {
                Rgb15(px[((dispY + y) & (vh - 1)) * vw + ((dispX + x) & (vw - 1))], disp, o);
                continue;
            }

            //24 bit display: 3 bytes per pixel packed over the 16 bit vram words
            var byteX = dispX * 2 + x * 3;
            for (var k = 0; k < 3; k++)
            {
                var b = byteX + k;
                var word = px[((dispY + y) & (vh - 1)) * vw + ((b >> 1) & (vw - 1))];
                disp[o + k] = (byte)((b & 1) == 0 ? word & 0xFF : word >> 8);
            }

            disp[o + 3] = 255;
        }

        if (latestOnly)
        {
            //write then rename, so a reader never sees a half written file
            var tmp = Path.Combine(dir, "latest.tmp.png");
            PngWriter.WriteRgba(tmp, disp, w, h);
            File.Move(tmp, Path.Combine(dir, "latest.png"), true);
            return;
        }

        PngWriter.WriteRgba(Path.Combine(dir, $"display_{_frames:D6}.png"), disp, w, h);
        Console.WriteLine($"[FrameDump] frame {_frames} (game frame {Diagnostics.TestScript.Frame}): saved vram + display {dispX},{dispY} {w}x{h} to {Path.GetFullPath(dir)}");
    }

    private static void Rgb15(ushort c, byte[] dst, int o)
    {
        dst[o] = (byte)((c & 0x1F) << 3);
        dst[o + 1] = (byte)(((c >> 5) & 0x1F) << 3);
        dst[o + 2] = (byte)(((c >> 10) & 0x1F) << 3);
        dst[o + 3] = 255;
    }
}
