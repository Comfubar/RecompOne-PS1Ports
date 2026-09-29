namespace RecompOne.Runtime.Diagnostics;

//counters reported with RECOMPONE_FRAME_STATS, so a headless run can tell whether the game makes any sound:
//SPU voices started (key on), XA audio sectors decoded, and the loudest sample handed to the audio device
public static class AudioStats
{
    public static long KeyOns;
    public static long XaSectors;
    private static int _peak;

    public static void CountKeyOns(uint bits)
    {
        KeyOns += System.Numerics.BitOperations.PopCount(bits);
    }

    //loudness over a window (TestScript "audio"): mean square of the stereo samples, and of left minus right (0 for mono)
    private static double _sumSq, _sideSq;
    private static long _frames;
    private static readonly object _lock = new();

    public static void Output(short[] samples)
    {
        var peak = _peak;
        double sq = 0, side = 0;
        for (var i = 0; i + 1 < samples.Length; i += 2)
        {
            int l = samples[i], r = samples[i + 1];
            var a = Math.Max(l == short.MinValue ? short.MaxValue : Math.Abs(l), r == short.MinValue ? short.MaxValue : Math.Abs(r));
            if (a > peak) peak = a;
            sq += (double)l * l + (double)r * r;
            side += (double)(l - r) * (l - r);
        }

        _peak = peak;
        lock (_lock)
        {
            _sumSq += sq;
            _sideSq += side;
            _frames += samples.Length / 2;
        }
    }

    //RMS of both channels and of left minus right since the last call (0..32767)
    public static (double Rms, double SideRms, long Frames) TakeLoudness()
    {
        lock (_lock)
        {
            var n = Math.Max(1, _frames);
            var r = (Math.Sqrt(_sumSq / (2.0 * n)), Math.Sqrt(_sideSq / n), _frames);
            _sumSq = _sideSq = 0;
            _frames = 0;
            return r;
        }
    }

    //peak since the last call, 0 = silence
    public static int TakePeak()
    {
        var p = _peak;
        _peak = 0;
        return p;
    }
}
