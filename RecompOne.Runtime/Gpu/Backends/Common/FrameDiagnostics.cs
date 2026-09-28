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

    //a one off dump of the next frame (live script "dump"); nonBlackWithin > 0: of the next frame that is not black,
    //or of whatever is shown once that many seconds have passed
    public static void RequestDump(double nonBlackWithin = 0)
    {
        _dumpNonBlackUntil = nonBlackWithin > 0 ? _statClock.Elapsed.TotalSeconds + nonBlackWithin : 0;
        _dumpRequested = true;
    }

    private static double _dumpNonBlackUntil;

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

    public static bool Enabled => StatsEvery > 0 || DumpEvery > 0 || LatestEvery > 0 || Diagnostics.ScreenProbe.Wanted ||
                                  Environment.GetEnvironmentVariable("RECOMPONE_SCRIPT_LIVE") is { Length: > 0 };

    public static void Announce()
    {
        if (Enabled) Console.WriteLine($"[FrameStats] enabled: stats every {StatsEvery}, dump every {DumpEvery} host frames");
    }

    //presenter: the backend that just presented this frame (its ReadPresented is the shown image), null when nothing
    //was presented (display off or not the HLE path), then the display area is read from vram
    public static void OnHostFrame(IGlVram vram, int dispX, int dispY, int w, int h, bool rgb24, bool displayOn,
        bool hle, GlCore? presenter = null)
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
                              $"workingSetMB={Environment.WorkingSet / 1048576} managedMB={GC.GetTotalMemory(false) / 1048576} " +
                              $"gameFps={Diagnostics.FrameRate.GameFps:0.0} hostFps={Diagnostics.FrameRate.HostFps:0.0}");
            _tri = _rect = _line = _fill = _copy = _upload = 0;
        }

        var dump = (DumpEvery > 0 && _frames % DumpEvery == 0) || _dumpRequested;
        var latest = !dump && LatestEvery > 0 && _frames % LatestEvery == 0;
        var probe = Diagnostics.ScreenProbe.Wanted;
        if (!dump && !latest && !probe) return;

        //the shown image, read on the GL thread at present time
        byte[]? shown = null;
        int sw = 0, sh = 0;
        if (presenter != null) shown = presenter.ReadPresented(out sw, out sh);
        if (probe) Diagnostics.ScreenProbe.OnPresented(shown, sw, sh, _frames);

        if (dump && _dumpRequested && _dumpNonBlackUntil > _statClock.Elapsed.TotalSeconds &&
            (shown == null || Diagnostics.ScreenProbe.IsBlack(new Diagnostics.ScreenProbe.Frame(shown, sw, sh, 0, 0, 0))))
            return; //not yet: waiting for a frame that is not black

        if (dump)
        {
            _dumpRequested = false;
            Dump(vram, dispX, dispY, Math.Max(w, 1), Math.Max(h, 1), rgb24, shown, sw, sh);
        }
        else if (latest && shown != null)
        {
            //nothing is written while the display is off, so latest.png is always a frame that was really shown
            WriteLatest(shown, sw, sh);
        }
    }

    public static string FramesDir => Path.Combine(Diagnostics.ScreenProbe.DumpRoot, "frames");

    private static void WriteLatest(byte[] rgba, int w, int h)
    {
        var dir = FramesDir;
        Directory.CreateDirectory(dir);
        //write then rename, so a reader never sees a half written file
        var tmp = Path.Combine(dir, "latest.tmp.png");
        PngWriter.WriteRgba(tmp, rgba, w, h);
        try
        {
            File.Move(tmp, Path.Combine(dir, "latest.png"), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            //a viewer has latest.png open; this frame is skipped, the next write replaces it
            _latestSkipped++;
            if (_statClock.Elapsed.TotalSeconds - _latestSkipLogged < 60) return;
            _latestSkipLogged = _statClock.Elapsed.TotalSeconds;
            Console.WriteLine($"[FrameDump] latest.png is open in another program, {_latestSkipped} update(s) skipped ({e.Message})");
        }
    }

    private static long _latestSkipped;
    private static double _latestSkipLogged = -60;

    private static void Dump(IGlVram vram, int dispX, int dispY, int w, int h, bool rgb24, byte[]? shown, int sw, int sh)
    {
        const int vw = VramShadow.Width, vh = VramShadow.Height;
        var px = new ushort[vw * vh];
        vram.ReadRect(0, 0, vw, vh, px);

        var dir = FramesDir;
        Directory.CreateDirectory(dir);

        var full = new byte[vw * vh * 4];
        for (var i = 0; i < px.Length; i++) Rgb15(px[i], full, i * 4);
        PngWriter.WriteRgba(Path.Combine(dir, $"vram_{_frames:D6}.png"), full, vw, vh);

        var file = Path.Combine(dir, $"display_{_frames:D6}.png");
        if (shown != null)
        {
            PngWriter.WriteRgba(file, shown, sw, sh);
            LastDump = Path.GetFullPath(file);
            var black = Diagnostics.ScreenProbe.IsBlack(new Diagnostics.ScreenProbe.Frame(shown, sw, sh, 0, 0, 0));
            Console.WriteLine($"[FrameDump] frame {_frames} (game frame {Diagnostics.TestScript.Frame}): saved vram + shown frame {sw}x{sh}{(black ? " (BLACK)" : "")} to {LastDump}");
            return;
        }

        //nothing presented this frame (display off): the display area straight from vram
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

        PngWriter.WriteRgba(file, disp, w, h);
        LastDump = Path.GetFullPath(file);
        Console.WriteLine($"[FrameDump] frame {_frames} (game frame {Diagnostics.TestScript.Frame}): display off (BLACK), saved vram + display area {dispX},{dispY} {w}x{h} to {LastDump}");
    }

    //full path of the last display_*.png written
    public static volatile string LastDump = "";

    private static void Rgb15(ushort c, byte[] dst, int o)
    {
        dst[o] = (byte)((c & 0x1F) << 3);
        dst[o + 1] = (byte)(((c >> 5) & 0x1F) << 3);
        dst[o + 2] = (byte)(((c >> 10) & 0x1F) << 3);
        dst[o + 3] = 255;
    }
}
