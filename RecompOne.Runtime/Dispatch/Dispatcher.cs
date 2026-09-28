using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using BiosKernel = RecompOne.Runtime.Bios.Bios;

namespace RecompOne.Runtime.Dispatch;

public static class Dispatcher
{
    private static readonly OverlayLoadedEvent _overlayEvent = new();
    private static readonly Dictionary<string, IOverlay> _registry = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> _lbaToName = [];
    private static readonly List<string> _active = [];
    private static readonly Dictionary<uint, Action<CpuContext, IMemory>> _funcMap = [];
    private static readonly Dictionary<uint, CodeGuard> _guards = [];
    private static IOverlay? _pending;

    //every ram write stamps its 1KB page with the current generation, a function whose pages carry a newer
    //generation than the one it was last verified at gets its bytes compared against the recompiled image
    //before it runs, so code that was overwritten in ram is never executed from the stale recompilation
    private const int PageShift = 10;
    private static readonly uint[] _pageGen = new uint[(MemoryMap.DevkitRamSize >> PageShift) + 1];
    private static uint _gen = 1;

    private sealed class CodeGuard(IOverlay owner, uint start, uint end)
    {
        public readonly IOverlay Owner = owner;
        public readonly uint Start = start;
        public readonly uint End = end;
        public uint VerifiedGen;
    }

    public static void Register(string name, IOverlay overlay)
    {
        _registry[name] = overlay;
        if (overlay.LbaStart >= 0) _lbaToName[overlay.LbaStart] = name;
    }

    public static string[] ActiveNames
    {
        get
        {
            lock (_active)
            {
                return _active.ToArray();
            }
        }
    }

    public static IReadOnlyDictionary<string, IOverlay> Overlays => _registry;

    public static void LoadByLba(int lba)
    {
        if (!_lbaToName.TryGetValue(lba, out var name)) return;
        var overlay = _registry[name];
        if (overlay.Base == 0)
        {
            Load(name);
            return;
        }

        _pending = overlay;
    }

    public static void NotifyWrite(uint phys)
    {
        _pageGen[(phys & (MemoryMap.DevkitRamSize - 1)) >> PageShift] = _gen;

        var p = _pending;
        if (p == null) return;

        PromoteWrite(p, phys);
    }

    private static void PromoteWrite(IOverlay p, uint phys)
    {
        var start = p.Base & 0x1FFFFFFFu;
        if (phys < start || phys >= start + 0x800u) return;
        _pending = null;
        Load(p.Name);
    }

    public static void ClearPending()
    {
        _pending = null;
    }

    public static void Reset()
    {
        _pending = null;
        lock (_active)
        {
            _active.Clear();
        }

        _funcMap.Clear();
        _guards.Clear();
    }

    private static void AddGuards(IOverlay overlay)
    {
        var ranges = overlay.FunctionRanges;
        if (overlay.Image == null) return;
        for (var i = 0; i + 1 < ranges.Length; i += 2)
            _guards[ranges[i]] = new CodeGuard(overlay, ranges[i], ranges[i + 1]);
    }

    private static uint Phys(uint addr)
    {
        return addr & 0x1FFFFFFFu & (MemoryMap.DevkitRamSize - 1);
    }

    //first offset where ram differs from the image inside [start, end), or -1
    private static long FirstMismatch(IOverlay overlay, IMemory m, uint start, uint end)
    {
        var image = overlay.Image!;
        for (var a = start; a < end; a++)
        {
            var i = a - overlay.Base;
            if (i >= image.Length) break;
            if (m.ReadU8(a) != image[i]) return a;
        }

        return -1;
    }

    private static int CountMismatches(IOverlay overlay, IMemory m, out uint firstFunc, out uint firstAddr)
    {
        firstFunc = firstAddr = 0;
        var n = 0;
        var ranges = overlay.FunctionRanges;
        for (var i = 0; i + 1 < ranges.Length; i += 2)
        {
            var bad = FirstMismatch(overlay, m, ranges[i], ranges[i + 1]);
            if (bad < 0) continue;
            if (n++ == 0)
            {
                firstFunc = ranges[i];
                firstAddr = (uint)bad;
            }
        }

        return n;
    }

    private static void ReportMismatches(IOverlay overlay)
    {
        var m = Runtime.Mem;
        if (overlay.Image == null || m == null) return;
        var n = CountMismatches(overlay, m, out var func, out var at);
        if (n == 0)
            Console.WriteLine($"[Dispatcher] {overlay.Name}: ram matches the recompiled image ({overlay.FunctionRanges.Length / 2} functions)");
        else
            Console.WriteLine($"[Dispatcher] ERROR: {overlay.Name}: {n} function(s) differ from the recompiled image in ram " +
                              $"(first func_{func:X8} at 0x{at:X8}), they will refuse to run until ram matches again");
    }

    //before running a guarded function, make sure the bytes it was recompiled from are still what is in ram
    private static void CheckStale(uint addr, IMemory m)
    {
        if (!_guards.TryGetValue(addr, out var g)) return;

        var dirty = false;
        for (var p = Phys(g.Start) >> PageShift; p <= Phys(g.End - 1) >> PageShift; p++)
            if (_pageGen[p] > g.VerifiedGen)
            {
                dirty = true;
                break;
            }

        if (!dirty) return;

        var bad = FirstMismatch(g.Owner, m, g.Start, g.End);
        if (bad >= 0)
        {
            var msg = $"stale code: 0x{addr:X8} ({g.Owner.Name}, 0x{g.Start:X8}-0x{g.End:X8}) was overwritten in ram " +
                      $"(0x{bad:X8} is 0x{m.ReadU8((uint)bad):X2}, recompiled from 0x{g.Owner.Image![bad - g.Owner.Base]:X2}), " +
                      $"active overlays: {string.Join(", ", ActiveNames)}";
            Console.WriteLine($"[Dispatcher] ERROR: {msg}");
            throw new InvalidOperationException(msg);
        }

        g.VerifiedGen = _gen++;
    }

    //code that got into ram without a disc read the dispatcher could track (unpacked from an archive, copied,
    //exec'd): find the registered overlay whose recompiled bytes are what ram holds now and switch it on
    public static bool ActivateAt(uint addr, IMemory m, string reason)
    {
        IOverlay? best = null;
        var bestBad = int.MaxValue;
        var tied = new List<string>();

        foreach (var (name, overlay) in _registry)
        {
            if (overlay.Image == null) continue;
            if (!overlay.Functions.ContainsKey(addr) && !overlay.Functions.ContainsKey(Cached(addr))) continue;
            lock (_active)
            {
                if (_active.Contains(name)) continue;
            }

            var key = overlay.Functions.ContainsKey(addr) ? addr : Cached(addr);
            var ranges = overlay.FunctionRanges;
            var own = -1;
            for (var i = 0; i + 1 < ranges.Length; i += 2)
                if (ranges[i] == key)
                    own = i;

            if (own < 0 || FirstMismatch(overlay, m, ranges[own], ranges[own + 1]) >= 0)
            {
                Log.Dispatch($"{reason} 0x{addr:X8}: overlay {name} has this entry but ram does not hold its code");
                continue;
            }

            var bad = CountMismatches(overlay, m, out _, out _);
            if (bad < bestBad)
            {
                best = overlay;
                bestBad = bad;
                tied.Clear();
            }
            else if (bad == bestBad)
            {
                tied.Add(name);
            }
        }

        if (best == null) return false;
        if (tied.Count > 0)
        {
            Console.WriteLine($"[Dispatcher] ERROR: {reason} 0x{addr:X8}: ram matches {best.Name} and {string.Join(", ", tied)} equally, not guessing");
            return false;
        }

        Console.WriteLine($"[Dispatcher] {reason} 0x{addr:X8}: ram holds overlay {best.Name}, switching it on");

        //ram in best's range now holds best's code, so whatever was active there before has been overwritten
        //(the EXE.PAC programs all load at 0x800A0000 with different sizes)
        if (best.Base != 0 && best.Size != 0)
        {
            var s = best.Base & 0x1FFFFFFFu;
            var e = s + best.Size;
            List<string> gone;
            lock (_active)
            {
                gone = _active.Where(n =>
                {
                    var o = _registry[n];
                    if (o.Base == 0 || o.Size == 0) return false;
                    var os = o.Base & 0x1FFFFFFFu;
                    return os < e && os + o.Size > s;
                }).ToList();
            }

            foreach (var n in gone)
            {
                Unload(n);
                Console.WriteLine($"[Dispatcher] overlay {n} overwritten by {best.Name}");
            }
        }

        Load(best.Name);
        return true;
    }

    public static void Load(string name)
    {
        if (!_registry.TryGetValue(name, out var overlay))
            throw new KeyNotFoundException($"overlay not registered: {name}");

        bool already;
        lock (_active)
        {
            already = _active.Remove(name);
        }

        if (!already) HandleRegionOverwrites(overlay);

        lock (_active)
        {
            _active.Add(name);
        }

        foreach (var (addr, fn) in overlay.Functions)
            _funcMap[addr] = fn;
        AddGuards(overlay);

        if (already) return;
        Runtime.OverlayLog.Record(name, OverlayEventKind.Loaded);
        Console.WriteLine($"[Dispatcher] loaded overlay: {name}");
        ReportMismatches(overlay);

        if (Event.HasAnyListeners<OverlayLoadedEvent>())
        {
            var e = _overlayEvent;
            e.Context = Runtime.Cpu!;
            e.Memory = Runtime.Mem!;
            e.Name = name;
            Event.Dispatch(e);
        }
    }

    private static void HandleRegionOverwrites(IOverlay overlay)
    {
        var newStart = overlay.Base & 0x1FFFFFFFu;
        var newEnd = newStart + overlay.Size;
        var hasRegion = overlay.Base != 0 && overlay.Size != 0;

        List<string>? overwritten = null;
        List<(string Name, int Funcs)>? vramCollisions = null;

        lock (_active)
        {
            foreach (var activeName in _active)
            {
                var other = _registry[activeName];
                var otherHasRegion = other.Base != 0 && other.Size != 0;

                if (hasRegion && otherHasRegion)
                {
                    var s = other.Base & 0x1FFFFFFFu;
                    var e = s + other.Size;

                    if (s < newEnd && e > newStart)
                    {
                        if (s >= newStart && e <= newEnd)
                        {
                            overwritten ??= [];
                            overwritten.Add(activeName);
                        }

                        continue;
                    }
                }

                var shared = CountSharedFunctions(overlay, other);
                if (shared > 0)
                {
                    vramCollisions ??= [];
                    vramCollisions.Add((activeName, shared));
                }
            }

            if (overwritten != null)
                foreach (var d in overwritten)
                    _active.Remove(d);
        }

        if (overwritten != null)
        {
            Rebuild();
            foreach (var d in overwritten)
            {
                Runtime.OverlayLog.Record(d, OverlayEventKind.Overwritten, overlay.Name);
                Console.WriteLine($"[Dispatcher] overlay {d} overwritten by {overlay.Name}");
            }
        }

        if (vramCollisions != null)
            foreach (var (otherName, n) in vramCollisions)
            {
                Runtime.OverlayLog.Record(overlay.Name, OverlayEventKind.VramCollision, $"{otherName} ({n} funcs)");
                Console.WriteLine(
                    $"[Dispatcher] overlay {overlay.Name} vvram colision with {otherName}: {n} functions");
            }
    }

    private static int CountSharedFunctions(IOverlay a, IOverlay b)
    {
        var smaller = a.Functions.Count <= b.Functions.Count ? a : b;
        var larger = ReferenceEquals(smaller, a) ? b : a;

        var n = 0;
        foreach (var addr in smaller.Functions.Keys)
            if (larger.Functions.ContainsKey(addr))
                n++;
        return n;
    }

    public static void TryLoad(string name)
    {
        if (_registry.ContainsKey(name))
            Load(name);
    }

    public static void Unload(string name)
    {
        bool removed;
        lock (_active)
        {
            removed = _active.Remove(name);
        }

        if (!removed) return;
        Rebuild();
        Runtime.OverlayLog.Record(name, OverlayEventKind.Unloaded);
    }

    public static bool CanCall(uint addr)
    {
        return addr switch
        {
            0xA0u or 0xB0u or 0xC0u => true,
            _ => (addr & 0xFF000000u) == 0xBFC00000u || _funcMap.ContainsKey(addr) ||
                 _funcMap.ContainsKey(Cached(addr))
        };
    }

    private static uint Cached(uint addr)
    {
        var phys = addr & 0x1FFFFFFFu;
        return phys < 0x00800000u ? 0x80000000u | phys : addr;
    }

    public static bool Tolerant;

    private static readonly HashSet<uint> _reported = [];

    private static bool IsReturnSite(IMemory m, uint addr)
    {
        if (addr < 0x80000008u || (addr & 3u) != 0) return false;

        var w = m.ReadU32(addr - 8);
        var op = w >> 26;
        if (op == 3) return true;
        if (op == 0 && (w & 0x3Fu) == 9) return true;
        return op == 1 && ((w >> 16) & 0x1Fu) is 0x10 or 0x11;
    }

    public static void Call(CpuContext c, IMemory m, uint addr)
    {
        Diagnostics.CrashReporter.RecordCall(addr);
        if (BiosKernel.TryDispatch(c, m, addr)) return;

        if (_funcMap.TryGetValue(addr, out var fn))
        {
            CheckStale(addr, m);
            fn(c, m);
            return;
        }

        var cached = Cached(addr);
        if (cached != addr && _funcMap.TryGetValue(cached, out fn))
        {
            CheckStale(cached, m);
            fn(c, m);
            return;
        }

        if (ActivateAt(addr, m, "call"))
        {
            Call(c, m, addr);
            return;
        }

        if (IsReturnSite(m, addr)) return;

        Diagnostics.CrashReporter.RecordUnmapped(addr);
        if (!Tolerant)
        {
            DumpWrittenRegion(m, addr);
            throw new InvalidOperationException($"unmapped call: 0x{addr:X8}");
        }

        lock (_reported)
            if (_reported.Add(addr))
                Console.WriteLine($"[Dispatcher] skipped an unmapped call to 0x{addr:X8}");

        c.V0 = 0u;
    }

    private static void Rebuild()
    {
        _funcMap.Clear();
        _guards.Clear();
        lock (_active)
        {
            foreach (var name in _active)
            {
                foreach (var (addr, fn) in _registry[name].Functions)
                    _funcMap[addr] = fn;
                AddGuards(_registry[name]);
            }
        }
    }

    private const uint MaxDump = 0x80000;

    //an unmapped call into ram that was written at runtime is code the game put there itself (copied or
    //unpacked), save the written area around it so it can be recompiled
    private static void DumpWrittenRegion(IMemory m, uint addr)
    {
        var seg = addr & 0xE0000000u;
        if (seg != 0x80000000u && seg != 0x00000000u && seg != 0xA0000000u) return;
        var phys = addr & 0x1FFFFFFFu;
        if (phys >= MemoryMap.RetailRamSize || _pageGen[phys >> PageShift] == 0) return;

        var first = phys >> PageShift;
        var last = first;
        while (first > 0 && _pageGen[first - 1] != 0 && ((phys >> PageShift) - first + 1) << PageShift < MaxDump) first--;
        while (((last + 1) << PageShift) < MemoryMap.RetailRamSize && _pageGen[last + 1] != 0 &&
               (last - first + 1) << PageShift < MaxDump) last++;

        var start = 0x80000000u | (first << PageShift);
        var size = (last - first + 1) << PageShift;
        var data = new byte[size];
        for (uint i = 0; i < size; i++) data[i] = m.ReadU8(start + i);

        Directory.CreateDirectory("dumps");
        var path = Path.GetFullPath(Path.Combine("dumps", $"ram_{addr:X8}_{start:X8}-{start + size:X8}.bin"));
        File.WriteAllBytes(path, data);
        Console.WriteLine($"[Dispatcher] unmapped call 0x{addr:X8} lands in ram written at runtime, saved 0x{start:X8}-0x{start + size:X8} to {path}");
    }
}