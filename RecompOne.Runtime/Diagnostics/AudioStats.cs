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

    public static void Output(short[] samples)
    {
        var peak = _peak;
        foreach (var s in samples)
        {
            var a = s == short.MinValue ? short.MaxValue : Math.Abs(s);
            if (a > peak) peak = a;
        }

        _peak = peak;
    }

    //peak since the last call, 0 = silence
    public static int TakePeak()
    {
        var p = _peak;
        _peak = 0;
        return p;
    }
}
