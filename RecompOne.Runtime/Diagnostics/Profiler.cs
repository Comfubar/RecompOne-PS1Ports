using System.Diagnostics;
using System.Text;

namespace RecompOne.Runtime.Diagnostics;

//RECOMPONE_PROFILE=1: a sampling profiler for the recompiled code. Every so many interrupt polls (the generated code
//polls on every loop back edge) the game thread's call chain is recorded; the TestScript "profile" action prints the
//most frequent chains and starts over. Shows which game functions run on a screen, without touching generated code.
public static class Profiler
{
    public static readonly bool On = Environment.GetEnvironmentVariable("RECOMPONE_PROFILE") == "1";
    private static readonly Dictionary<string, int> _chains = [];
    private static int _samples;

    //called from the slow path of Interrupts.Poll, on the game thread
    public static void Sample()
    {
        var frames = new StackTrace(2, false).GetFrames();
        var sb = new StringBuilder();
        var n = 0;
        foreach (var f in frames)
        {
            var m = f.GetMethod();
            if (m == null || m.DeclaringType?.Namespace?.StartsWith("RecompOne.Runtime") == true) continue;
            if (n++ > 0) sb.Append(" <- ");
            sb.Append(m.Name);
            if (n == 8) break;
        }

        var key = sb.ToString();
        lock (_chains)
        {
            _chains[key] = _chains.GetValueOrDefault(key) + 1;
            _samples++;
        }
    }

    public static void Report(int top)
    {
        lock (_chains)
        {
            Console.WriteLine($"[Profile] {_samples} samples, {_chains.Count} distinct chains, top {top}:");
            foreach (var (chain, count) in _chains.OrderByDescending(kv => kv.Value).Take(top))
                Console.WriteLine($"[Profile] {count,6} {chain}");
            _chains.Clear();
            _samples = 0;
        }
    }
}
