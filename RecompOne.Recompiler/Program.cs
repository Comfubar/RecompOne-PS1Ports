using RecompOne.Recompiler.AutoConfigure;
using RecompOne.Recompiler.CodeGen;
using RecompOne.Recompiler.Config;
using RecompOne.Recompiler.Elf;
using RecompOne.Recompiler.Map;
using RecompOne.Recompiler.Symbols;
using RecompOne.Runtime.Cdrom;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: recompone <config.json> [-cue <disc.cue>] [-out <dir>] [-sources-only]");
    Console.Error.WriteLine(
        "       recompone --generate-function-file -elf <path> -map <path> -out <output.json> [-rebase <hex>]");
    Console.Error.WriteLine("       recompone --probe-disc <disc> [-json <out.json>] [-all]");
    Console.Error.WriteLine(
        "       recompone --autoconfigure <disc> -out <dir> [-name <game>] [-signatures <psyq.json>] [-sweep-all]");
    return 1;
}

if (string.Equals(args[0], "--probe-disc", StringComparison.OrdinalIgnoreCase))
    return ProbeDisc(args);

if (string.Equals(args[0], "--autoconfigure", StringComparison.OrdinalIgnoreCase))
    return Autoconfigure(args);

if (string.Equals(args[0], "--generate-function-file", StringComparison.OrdinalIgnoreCase))
    return GenerateFunctionFile(args);

//recompone --dpac <archive.PAC> [-out <dir>] [-compare <index> <exe>]
if (string.Equals(args[0], "--dpac", StringComparison.OrdinalIgnoreCase))
    return DpacCommand(args);

//recompone --sweep-overlay <config.json> <overlay> [-signatures <psyq.json>]
//runs the autoconfigure analysis (sweep + sdk naming) on one overlay of the config and writes its funcMap
string? sweepOverlay = null, sweepSignatures = null;
if (string.Equals(args[0], "--sweep-overlay", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: recompone --sweep-overlay <config.json> <overlay> [-signatures <psyq.json>]");
        return 1;
    }

    sweepOverlay = args[2];
    if (args.Length >= 5 && args[3] == "-signatures") sweepSignatures = args[4];
    args = args[1..2];
}

var configPath = Path.GetFullPath(args[0]);
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"config not found: {configPath}");
    return 1;
}

var config = ConfigLoader.Load(configPath);
var configDir = Path.GetDirectoryName(configPath)!;

//-cue and -out override the config, so a tool can point it at the player's own disc and a work folder
for (var i = 1; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "-cue": config.Cue = Path.GetFullPath(args[++i]); break;
        case "-out": config.Game.Output = Path.GetFullPath(args[++i]); break;
        case "-sources-only": config.SourcesOnly = true; break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 1;
    }
}

string? ResolvePath(string? p)
{
    return p == null ? null : Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(configDir, p));
}

config.Elf = ResolvePath(config.Elf);
config.Map = ResolvePath(config.Map);
config.FuncMap = ResolvePath(config.FuncMap);
config.RuntimeProject = ResolvePath(config.RuntimeProject);
foreach (var overlay in config.Overlays)
{
    overlay.Elf = ResolvePath(overlay.Elf);
    overlay.Map = ResolvePath(overlay.Map);
    overlay.FuncMap = ResolvePath(overlay.FuncMap);
    overlay.LocalFile = ResolvePath(overlay.LocalFile);
}

var cuePath = Path.GetFullPath(Path.Combine(configDir, config.Cue));

if (!File.Exists(cuePath))
{
    Console.Error.WriteLine($"disc file not found: {cuePath}");
    return 1;
}

Console.WriteLine($"[RecompOne] Game: {config.Game.Name} ({config.Game.Id})");
Console.WriteLine($"[RecompOne] Disc file: {cuePath}");

var fs = DiscFs.Open(cuePath);

if (sweepOverlay != null)
{
    var target = config.Overlays.FirstOrDefault(o => string.Equals(o.Name, sweepOverlay, StringComparison.OrdinalIgnoreCase));
    if (target == null)
    {
        Console.Error.WriteLine($"overlay '{sweepOverlay}' is not in {configPath}");
        return 1;
    }

    var funcMapPath = target.FuncMap ?? Path.Combine(configDir, "funcmaps", $"{target.Name}.json");
    return AutoConfigurator.SweepOverlay(config, fs, target, funcMapPath, sweepSignatures) ? 0 : 1;
}

var outDir = Path.GetFullPath(Path.Combine(configDir, config.Game.Output));
Directory.CreateDirectory(outDir);

Console.WriteLine($"[RecompOne] Output Path: {outDir}");

try
{
    OverlayWriter.Write(config, fs, outDir);
    Console.WriteLine("[RecompOne] Recompilation finished.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[RecompOne] Error: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 1;
}

static int DpacCommand(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: recompone --dpac <archive.PAC> [-out <dir>] [-compare <index> <exe>]");
        return 1;
    }

    string? outDir = null, compareExe = null;
    var compareIndex = -1;
    for (var i = 2; i < args.Length; i++)
        switch (args[i].ToLowerInvariant())
        {
            case "-out": outDir = args[++i]; break;
            case "-compare":
                compareIndex = int.Parse(args[++i]);
                compareExe = args[++i];
                break;
            default:
                Console.Error.WriteLine($"unknown argument: {args[i]}");
                return 1;
        }

    var archive = File.ReadAllBytes(args[1]);
    var toc = RecompOne.Runtime.Cdrom.Dpac.ReadToc(archive);
    Console.WriteLine($"[Dpac] {args[1]}: {toc.Count} entries");
    if (outDir != null) Directory.CreateDirectory(outDir);

    foreach (var e in toc)
    {
        var raw = RecompOne.Runtime.Cdrom.Dpac.ReadEntry(archive, e);
        var packed = RecompOne.Runtime.Cdrom.Dpac.IsBpe(raw);
        var data = packed ? RecompOne.Runtime.Cdrom.Dpac.Unpack(archive, e) : raw;
        var kind = "data";
        if (data.Length >= 0x800 && data.AsSpan(0, 8).SequenceEqual("PS-X EXE"u8))
            kind = $"PS-X EXE pc=0x{BitConverter.ToUInt32(data, 0x10):X8} t_addr=0x{BitConverter.ToUInt32(data, 0x18):X8} " +
                   $"t_size=0x{BitConverter.ToUInt32(data, 0x1C):X}";
        Console.WriteLine($"  [{e.Index,2}] {e.Path,-12} sector {e.Sector,5} size 0x{e.Size:X6} " +
                          $"{(packed ? $"BPE -> 0x{data.Length:X}" : "stored")}  {kind}");
        if (outDir != null)
            File.WriteAllBytes(Path.Combine(outDir, $"{e.Index:D2}_{e.Path.Trim('/').Replace('/', '_')}.bin"), data);
    }

    if (compareExe == null) return 0;

    //byte for byte check of an unpacked entry's code section against an exe (for example a ram dump)
    var unpacked = RecompOne.Runtime.Cdrom.Dpac.Unpack(archive, toc[compareIndex]);
    var reference = File.ReadAllBytes(compareExe);
    var tSize = (int)BitConverter.ToUInt32(reference, 0x1C);
    var diffs = 0;
    var first = -1;
    for (var i = 0; i < tSize; i++)
    {
        var a = 0x800 + i < unpacked.Length ? unpacked[0x800 + i] : -1;
        if (a == reference[0x800 + i]) continue;
        if (diffs++ == 0) first = i;
    }

    Console.WriteLine(diffs == 0
        ? $"[Dpac] entry {compareIndex} matches {compareExe}: 0x{tSize:X} code bytes identical"
        : $"[Dpac] entry {compareIndex} differs from {compareExe}: {diffs} of 0x{tSize:X} bytes, first at +0x{first:X}");
    return diffs == 0 ? 0 : 2;
}

static int ProbeDisc(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: recompone --probe-disc <disc> [-json <out.json>] [-all]");
        return 1;
    }

    string? json = null;
    var all = false;
    for (var i = 2; i < args.Length; i++)
        switch (args[i].ToLowerInvariant())
        {
            case "-json": json = args[++i]; break;
            case "-all": all = true; break;
            default:
                Console.Error.WriteLine($"uknown argument: {args[i]}");
                return 1;
        }

    return ProbeCommand.Run(args[1], json, all);
}

static int Autoconfigure(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine(
            "usage: recompone --autoconfigure <disc> -out <dir> [-name <game>] [-signatures <psyq.json>] [-sweep-all]");
        return 1;
    }

    string? outDir = null, name = null, signatures = null;
    var sweepAll = false;
    for (var i = 2; i < args.Length; i++)
        switch (args[i].ToLowerInvariant())
        {
            case "-out": outDir = args[++i]; break;
            case "-name": name = args[++i]; break;
            case "-signatures": signatures = args[++i]; break;
            case "-sweep-all": sweepAll = true; break;
            default:
                Console.Error.WriteLine($"unknown argument: {args[i]}");
                return 1;
        }

    if (outDir == null)
    {
        Console.Error.WriteLine("missing -out <dir>");
        return 1;
    }

    return AutoConfigurator.Run(args[1], outDir, name, signatures, sweepAll);
}

static int GenerateFunctionFile(string[] args)
{
    string? elfPath = null, mapPath = null, outPath = null;
    string? discPath = null, discFile = null, baseAddr = null;
    var linearSweep = false;
    int offset = 0, skip = 0, lba = -1, size = -1;
    string? compression = null;
    var rebase = 0;

    for (var i = 1; i < args.Length; i++)
        switch (args[i].ToLowerInvariant())
        {
            case "-elf": elfPath = args[++i]; break;
            case "-map": mapPath = args[++i]; break;
            case "-out": outPath = args[++i]; break;
            case "-linear-sweep": linearSweep = true; break;
            case "-disc": discPath = args[++i]; break;
            case "-file": discFile = args[++i]; break;
            case "-base": baseAddr = args[++i]; break;
            case "-offset": offset = Convert.ToInt32(args[++i], 16); break;
            case "-skip": skip = Convert.ToInt32(args[++i], 16); break;
            case "-lba": lba = int.Parse(args[++i]); break;
            case "-size": size = Convert.ToInt32(args[++i], 16); break;
            case "-compression": compression = args[++i]; break;
            case "-rebase": rebase = Convert.ToInt32(args[++i], 16); break;
            default:
                Console.Error.WriteLine($"unknown argument: {args[i]}");
                return 1;
        }

    if (outPath == null)
    {
        Console.Error.WriteLine("missing -out <output.json>");
        return 1;
    }

    if (linearSweep)
    {
        if (discPath == null || baseAddr == null || (discFile == null && lba < 0))
        {
            Console.Error.WriteLine(
                "-linear-sweep needs -disc <cue>, -base <hex> and eiter -file <path in disc> or -lba <n> -size <hex>");
            return 1;
        }

        return GenerateFromLinearSweep(discPath, discFile, baseAddr, offset, skip, lba, size, compression, rebase,
            outPath);
    }

    if (elfPath == null && mapPath == null)
    {
        Console.Error.WriteLine("at least one of -elf or -map is required");
        return 1;
    }

    FunctionInfo? elfInfo = null;
    if (elfPath != null)
    {
        if (!File.Exists(elfPath))
        {
            Console.Error.WriteLine($"elf not found: {elfPath}");
            return 1;
        }

        Console.WriteLine($"[RecompOne] reading ELF: {elfPath}");
        elfInfo = ElfReader.Read(elfPath);
        Console.WriteLine(
            $"[RecompOne] ELF: {elfInfo.Functions.Count} function(s), {elfInfo.NoTypeSymbols.Count} label(s)");
    }

    FunctionInfo? mapInfo = null;
    if (mapPath != null)
    {
        if (!File.Exists(mapPath))
        {
            Console.Error.WriteLine($"map not found: {mapPath}");
            return 1;
        }

        Console.WriteLine($"[RecompOne] reading MAP: {mapPath}");
        mapInfo = MapReader.Read(mapPath);
        Console.WriteLine($"[RecompOne] MAP: {mapInfo.Functions.Count} function(s)");
    }

    var merged = FunctionMapLoader.Merge(elfInfo, mapInfo);

    if (rebase != 0)
    {
        var delta = (uint)rebase;
        foreach (var f in merged.Functions) f.Address += delta;
        foreach (var f in merged.NoTypeSymbols) f.Address += delta;
    }

    FunctionMapLoader.Save(outPath, merged);
    Console.WriteLine(
        $"[RecompOne] wrote {merged.Functions.Count} function(s), {merged.NoTypeSymbols.Count} label(s) -> {outPath}");
    return 0;
}

static int GenerateFromLinearSweep(string discPath, string? discFile, string baseAddr,
    int offset, int skip, int lba, int size, string? compression, int rebase, string outPath)
{
    discPath = Path.GetFullPath(discPath);
    if (!File.Exists(discPath))
    {
        Console.Error.WriteLine($"disc file not found: {discPath}");
        return 1;
    }

    var overlay = new OverlayConfig
    {
        Name = Path.GetFileNameWithoutExtension(outPath),
        Base = baseAddr,
        File = discFile,
        Offset = offset,
        Skip = skip,
        Lba = lba,
        Size = size >= 0 ? size : null,
        Compression = compression,
        Rebase = rebase,
        LinearSweep = true
    };

    Console.WriteLine($"[RecompOne] sweeping {discFile ?? $"lba {lba}"} from {discPath}");

    var fs = DiscFs.Open(discPath);
    var analysis = OverlayWriter.AnalyzeOverlay(new RecompOneConfig(), overlay, fs);
    if (analysis == null)
    {
        Console.Error.WriteLine("[RecompOne] sweap produced nothing");
        return 1;
    }

    var info = new FunctionInfo
    {
        TextBase = analysis.ElfInfo.TextBase,
        LoadAddress = analysis.ElfInfo.LoadAddress
    };
    foreach (var f in analysis.Functions.OrderBy(f => f.Start))
        info.Functions.Add(new RecompOne.Recompiler.Symbols.FunctionEntry
        {
            Name = f.Name,
            Address = f.Start,
            Size = f.End - f.Start
        });
    info.NoTypeSymbols.AddRange(analysis.ElfInfo.NoTypeSymbols);

    FunctionMapLoader.Save(outPath, info);
    Console.WriteLine(
        $"[RecompOne] wrote {info.Functions.Count} function(s), {info.NoTypeSymbols.Count} label(s) -> {outPath}");
    return 0;
}