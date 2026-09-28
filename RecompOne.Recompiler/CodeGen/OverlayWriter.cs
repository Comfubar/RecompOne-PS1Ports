using System.Text;
using System.Text.RegularExpressions;
using RecompOne.Recompiler.Analysis;
using RecompOne.Recompiler.Config;
using RecompOne.Recompiler.Disasm;
using RecompOne.Recompiler.Elf;
using RecompOne.Recompiler.Map;
using RecompOne.Recompiler.Symbols;
using RecompOne.Recompiler.Psx;
using RecompOne.Runtime.Cdrom;

namespace RecompOne.Recompiler.CodeGen;

public static class OverlayWriter
{
    private record OverlayResult(
        string Name,
        List<MipsFunction> Functions,
        int LbaStart,
        uint Base,
        uint Size,
        MipsInstruction[] Instructions,
        DiscImage? Source = null);

    //where an image's bytes are on the disc, when they are read straight from a disc file (see ImageSource)
    public sealed record DiscImage(string File, string? Archive, int Entry, int Skip);

    public static void Write(RecompOneConfig config, DiscFs fs, string outDir)
    {
        var className = SafeIdentifier(config.Game.Name);

        Console.WriteLine("[Recompiler] reading SYSTEM.CNF");
        var sysCfg = SystemCfg.Parse(fs);
        Console.WriteLine(
            $"[Recompiler] SYSTEM.CNF: BOOT={sysCfg.BootExe}  TCB={sysCfg.Tcb}  EVENT={sysCfg.Event}  STACK=0x{sysCfg.Stack:X8}");

        var mainExe = Parser.ParseExe(fs, sysCfg.BootExe);
        Console.WriteLine($"[Recompiler] PS-EXE: {mainExe.Region}");
        Console.WriteLine(
            $"[Recompiler] PS-EXE: PC=0x{mainExe.InitialPC:X8}  GP=0x{mainExe.InitialGP:X8}  SP=0x{mainExe.InitialSP:X8}  load=0x{mainExe.Destination:X8} ");

        var overlayResults = new List<OverlayResult>
            { AnalyzeMain(config, mainExe) with { Source = new DiscImage(sysCfg.BootExe, null, -1, 0x800) } };

        foreach (var overlayConfig in config.Overlays)
        {
            var analysis = AnalyzeOverlay(config, overlayConfig, fs);
            if (analysis == null) continue;
            overlayResults.Add(new OverlayResult(overlayConfig.Name, analysis.Functions, analysis.Lba,
                analysis.Base, (uint)analysis.DiscBin.Length, analysis.Instructions, analysis.Source));
        }

        var images = overlayResults.Select(r => new ImageFunctions(r.Name, r.Functions, r.Instructions)).ToList();
        FunctionPipeline.ScanCrossImage(images);
        FunctionPipeline.ScanEscapesToFixpoint(images);

        var allFuncs = overlayResults.SelectMany(o => o.Functions).ToList();
        ResolveCollisions(allFuncs);
        ApplyPatches(allFuncs, config.Patches);
        SdkPatches.Apply(allFuncs, config.DisableHle);
        WriteAll(config, outDir, className, mainExe, sysCfg, overlayResults, allFuncs);
    }

    private static OverlayResult AnalyzeMain(RecompOneConfig config, PsxExe mainExe)
    {
        FunctionInfo? rawElf = null;
        if (config.Elf != null)
        {
            if (!File.Exists(config.Elf))
                throw new FileNotFoundException($"Main ELF not found: {config.Elf}");

            Console.WriteLine(
                $"[Recompiler] Processing main executable with ELF: {config.Elf} (WARNING: 'elf' is deprecated, prefer 'map'/'funcMap')");
            rawElf = ElfReader.Read(config.Elf);
        }

        FunctionInfo? rawMap = null;
        if (config.Map != null)
        {
            if (!File.Exists(config.Map))
                throw new FileNotFoundException($"Main map not found: {config.Map}");

            Console.WriteLine($"[Recompiler] Processing main executable with map: {config.Map}");
            rawMap = MapReader.Read(config.Map);
        }

        FunctionInfo? rawFuncMap = null;
        if (config.FuncMap != null)
        {
            if (!File.Exists(config.FuncMap))
                throw new FileNotFoundException($"Main function map not found: {config.FuncMap}");

            Console.WriteLine($"[Recompiler] Processing main executable with function map: {config.FuncMap}");
            var funcMapBase = rawElf?.TextBase ?? rawMap?.LoadAddress ?? mainExe.Destination;
            rawFuncMap = FunctionMapLoader.Load(config.FuncMap, funcMapBase, mainExe.Code);
        }

        FunctionInfo elfInfo;
        MipsInstruction[] instrs;
        List<MipsFunction> funcs;

        if (rawElf != null || rawMap != null || rawFuncMap != null)
        {
            elfInfo = FunctionMapLoader.Merge(rawElf, rawMap, rawFuncMap);
            if (elfInfo.TextData.Length == 0) elfInfo.TextData = mainExe.Code;
            Console.WriteLine(
                $"[Recompiler] main function info: TextBase=0x{elfInfo.TextBase:X8} Functions={elfInfo.Functions.Count}");

            instrs = MipsDisasm.Disassemble(mainExe.Code, elfInfo.TextBase);

            funcs = elfInfo.Functions.Count > 0
                ? FunctionDetector.DetectFromElf(instrs, elfInfo, "main")
                : FunctionDetector.DetectFromScan(instrs, elfInfo.LoadAddress, "main");
        }
        else
        {
            Console.WriteLine("[Recompiler] processing main executable");
            instrs = MipsDisasm.Disassemble(mainExe.Code, mainExe.Destination);
            funcs = FunctionDetector.DetectFromScan(instrs, mainExe.InitialPC, "main");
            elfInfo = new FunctionInfo
            {
                TextBase = mainExe.Destination,
                LoadAddress = mainExe.Destination,
                TextData = mainExe.Code
            };
        }

        if (funcs.All(f => f.Start != mainExe.InitialPC))
        {
            funcs.AddRange(FunctionDetector.DetectFromAddresses(instrs, [(mainExe.InitialPC, null)], funcs, "main"));
            Console.WriteLine($"[Recompiler] added entry point function at 0x{mainExe.InitialPC:X8}");
        }

        FunctionPipeline.Run(funcs, instrs, elfInfo, "main",
            new PipelineOptions(config.Functions, config.LinearSweep, config.PointerScan, config.Stubs,
                config.Ignored));

        return new OverlayResult("main", funcs, -1, mainExe.Destination, mainExe.TextSize, instrs);
    }

    public sealed record OverlayAnalysis(
        List<MipsFunction> Functions,
        MipsInstruction[] Instructions,
        FunctionInfo ElfInfo,
        byte[] DiscBin,
        int Lba,
        uint Base,
        DiscImage? Source = null);

    public static OverlayAnalysis? AnalyzeOverlay(RecompOneConfig config, OverlayConfig overlayConfig, DiscFs fs)
    {
        //resolved first so a PS-X EXE header can supply the base and the entry point
        var (discBin, overlayLba, exe, discImage) = ResolveOverlay(fs, overlayConfig);
        if (discBin == null)
        {
            Console.WriteLine(
                $"[Recompiler] WARNING: could not resolve disc data for overlay '{overlayConfig.Name}', skipping");
            return null;
        }

        if (exe != null)
        {
            overlayConfig.Base ??= $"0x{exe.Value.TAddr - (uint)overlayConfig.Rebase:X8}";
            if (overlayConfig.Functions.All(f => Convert.ToUInt32(f.Address, 16) != exe.Value.Pc))
                overlayConfig.Functions = [..overlayConfig.Functions, new Config.FunctionEntry { Address = $"0x{exe.Value.Pc:X8}" }];
        }

        var noSymbols = overlayConfig.Elf == null && overlayConfig.Map == null && overlayConfig.FuncMap == null;
        if (noSymbols && !((overlayConfig.LinearSweep ?? config.LinearSweep) && overlayConfig.Base != null))
        {
            Console.WriteLine(
                $"[Recompiler] WARNING: Overlay '{overlayConfig.Name}' has no source defined, this will be skiped");
            return null;
        }

        if (overlayConfig.Elf != null && !File.Exists(overlayConfig.Elf))
        {
            Console.WriteLine(
                $"[Recompiler] WARNING: ELF file not found for overlay '{overlayConfig.Name}' ({overlayConfig.Elf}), this will be skiped.");
            return null;
        }

        if (overlayConfig.Map != null && !File.Exists(overlayConfig.Map))
        {
            Console.WriteLine(
                $"[Recompiler] WARNING: map file not found for overlay '{overlayConfig.Name}' ({overlayConfig.Map}), this will be skiped.");
            return null;
        }

        if (overlayConfig.FuncMap != null)
        {
            if (!File.Exists(overlayConfig.FuncMap))
            {
                Console.WriteLine(
                    $"[Recompiler] WARNING: function map not found for overlay '{overlayConfig.Name}' ({overlayConfig.FuncMap}), this will be skiped.");
                return null;
            }

            if (overlayConfig.Elf == null && overlayConfig.Map == null && overlayConfig.Base == null)
            {
                Console.WriteLine(
                    $"[Recompiler] WARNING: overlay '{overlayConfig.Name}' uses 'funcMap' alone but has no 'base' address defined, this will be skiped.");
                return null;
            }
        }

        if (overlayConfig.Elf != null)
            Console.WriteLine(
                $"[Recompiler] processing the overlay {overlayConfig.Name} (WARNING: 'elf' is deprecated, prefer 'map'/'funcMap')");
        else
            Console.WriteLine($"[Recompiler] processing the overlay {overlayConfig.Name}");

        var rawElf = overlayConfig.Elf != null ? ElfReader.Read(overlayConfig.Elf) : null;
        var rawMap = overlayConfig.Map != null ? MapReader.Read(overlayConfig.Map) : null;

        FunctionInfo? rawFuncMap = null;
        if (overlayConfig.FuncMap != null)
        {
            var funcMapBase = rawElf?.TextBase ?? rawMap?.LoadAddress ?? Convert.ToUInt32(overlayConfig.Base, 16);
            rawFuncMap = FunctionMapLoader.Load(overlayConfig.FuncMap, funcMapBase, discBin);
        }

        var elfInfo = FunctionMapLoader.Merge(rawElf, rawMap, rawFuncMap);
        if (elfInfo.TextData.Length == 0) elfInfo.TextData = discBin;

        if (noSymbols)
        {
            elfInfo.TextBase = Convert.ToUInt32(overlayConfig.Base, 16);
            elfInfo.LoadAddress = elfInfo.TextBase;
        }

        if (overlayConfig.Rebase != 0)
            RebaseElf(elfInfo, overlayConfig.Rebase, discBin);

        var instrs = MipsDisasm.Disassemble(discBin, elfInfo.TextBase);

        //elf is weird and doest properly provide all functions (specially asm) so resort to checking it
        var funcs = elfInfo.Functions.Count > 0
            ? FunctionDetector.DetectFromElf(instrs, elfInfo, overlayConfig.Name)
            : FunctionDetector.DetectFromScan(instrs, elfInfo.LoadAddress, overlayConfig.Name);

        FunctionPipeline.Run(funcs, instrs, elfInfo, overlayConfig.Name,
            new PipelineOptions(
                overlayConfig.Functions,
                overlayConfig.LinearSweep ?? config.LinearSweep,
                overlayConfig.PointerScan ?? config.PointerScan,
                overlayConfig.Stubs.Concat(config.Stubs),
                overlayConfig.Ignored.Concat(config.Ignored)));


        var ovlBase = overlayConfig.Base != null
            ? Convert.ToUInt32(overlayConfig.Base, 16) + (uint)overlayConfig.Rebase
            : 0;
        return new OverlayAnalysis(funcs, instrs, elfInfo, discBin, overlayLba, ovlBase, discImage);
    }

    private static void WriteAll(RecompOneConfig config, string outDir, string className, PsxExe mainExe,
        SystemCfg sysCfg, List<OverlayResult> overlayResults, List<MipsFunction> allFuncs)
    {
        var overlayParts = SpreadClasses(className, overlayResults);
        var funcClass = new Dictionary<uint, string>();

        foreach (var result in overlayResults)
        foreach (var part in overlayParts[result.Name])
        foreach (var func in part.Functions)
            funcClass[func.Start] = part.Class;

        var uniqueAddrs = allFuncs.GroupBy(f => f.Start).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
        var knownFuncs = allFuncs.Where(f => uniqueAddrs.Contains(f.Start))
            .ToDictionary(f => f.Start, f => $"{Owner(funcClass, f.Start, className)}.{f.EmittedName}");

        var conflictCount = allFuncs.Count - knownFuncs.Count;
        Console.WriteLine($"[Recompiler] total functions: {allFuncs.Count}");

        string? mainCall = null;
        if (config.Main != null)
        {
            var mainAddr = Convert.ToUInt32(config.Main, 16);
            var mainFunc = allFuncs.FirstOrDefault(f => f.Start == mainAddr);
            if (mainFunc == null)
                throw new InvalidOperationException($"[recompiler] the main function not found at 0x{mainAddr:X8}");
            mainCall = $"{Owner(funcClass, mainFunc.Start, className)}.{mainFunc.EmittedName}";
            Console.WriteLine($"[Recompiler] main: {mainCall} @ 0x{mainAddr:X8}");
        }

        ProjectLayoutWriter? layout = null;
        ImageDir = null;
        if (config.SplitProjects)
        {
            layout = new ProjectLayoutWriter(outDir, config.SourcesOnly ? null : config.RuntimeProject ?? throw new InvalidOperationException(
                "'splitProjects' needs 'runtimeProject' (path to RecompOne.Runtime.csproj, relative to the config)"));
            ImageDir = layout.ImageDir;
        }

        foreach (var result in overlayResults)
        {
            Console.WriteLine($"[Recompiler] emiting {result.Name}.cs ({result.Functions.Count} functions)");
            EmitOverlayFile(result.Name, overlayParts[result.Name], CallTargetsFor(result, overlayResults, overlayParts),
                config.EmbedImages ? null : result.Source, config.EmbedImages, config.Debug,
                config.AddressComments, config.DisasmComments, result.LbaStart, result.Base, result.Size,
                result.Instructions, outDir,
                SymbolRelocator.Plan(result.Functions, config.Relocations, result.Name));
        }

        Console.WriteLine("[Recompiler] Emitting Entry.cs");
        var overlayNames = overlayResults.Select(o => o.Name).ToList();
        EntryWriter.Write(mainExe, sysCfg, sysCfg.BootExe, className, mainCall, overlayNames, layout?.EntryDir ?? outDir,
            layout?.CommonDir);

        if (layout != null)
        {
            var resident = overlayResults.FirstOrDefault(r => r.Name == ResidentImage);
            foreach (var result in overlayResults)
                layout.WriteImageProject(result.Name,
                    resident != null && !ReferenceEquals(resident, result) && !Overlaps(result, resident) ? ResidentImage : null);
            layout.WriteCommonProject();
            Console.WriteLine($"[Recompiler] split layout: {overlayResults.Count} image projects + Common under {outDir}");
        }

        Console.WriteLine("[Recompiler] finished "); //maybe add time it took
    }


    //which functions code in `self` may call directly by address: its own, and the main exe's when self is an
    //overlay that does not overlap it (main stays resident, so it is always there to call). Everything else goes
    //through the dispatcher, which knows what is loaded: the EXE.PAC programs all load at 0x800A0000 and must never
    //bind to each other, and main must not bind to an overlay that may not be loaded. This also keeps the image
    //dependencies one way (overlay -> main), so each image can be compiled as its own project.
    private const string ResidentImage = "main";

    private static Dictionary<uint, string> CallTargetsFor(OverlayResult self, List<OverlayResult> all,
        Dictionary<string, List<OverlayPart>> parts)
    {
        var targets = new Dictionary<uint, string>();
        var resident = all.FirstOrDefault(r => r.Name == ResidentImage);
        if (resident != null && !ReferenceEquals(resident, self) && !Overlaps(self, resident))
            foreach (var part in parts[resident.Name])
            foreach (var f in part.Functions)
                targets[f.Start] = $"{part.Class}.{f.EmittedName}";

        foreach (var part in parts[self.Name])
        foreach (var f in part.Functions)
            targets[f.Start] = $"{part.Class}.{f.EmittedName}";

        return targets;
    }

    private static bool Overlaps(OverlayResult a, OverlayResult b)
    {
        if (a.Size == 0 || b.Size == 0) return true; //unknown range, assume the worst
        uint sa = a.Base & 0x1FFFFFFFu, sb = b.Base & 0x1FFFFFFFu;
        return sa < sb + b.Size && sb < sa + a.Size;
    }

    //where an image's .cs goes (null: straight into the output folder)
    private static Func<string, string>? ImageDir;

    private static string Owner(Dictionary<uint, string> funcClass, uint address, string fallback)
    {
        return funcClass.TryGetValue(address, out var owner) ? owner : fallback;
    }

    private const int FunctionsPerClass = 30000; //can hold up to 65k but a lower value is better looking

    //if it is too big it mus be slplit, CoreCLR cant handle classes over 65k functions
    private static Dictionary<string, List<OverlayPart>> SpreadClasses(string className,
        List<OverlayResult> overlayResults)
    {
        var map = new Dictionary<string, List<OverlayPart>>();

        foreach (var result in overlayResults)
        {
            var baseName = $"{className}_{SafeIdentifier(result.Name)}";
            var ordered = result.Functions.OrderBy(f => f.Start).ToList();
            var parts = new List<OverlayPart>();

            for (var i = 0; i < ordered.Count; i += FunctionsPerClass)
                parts.Add(new OverlayPart(
                    i == 0 ? baseName : $"{baseName}_{i / FunctionsPerClass}",
                    ordered.GetRange(i, Math.Min(FunctionsPerClass, ordered.Count - i))));

            if (parts.Count == 0) parts.Add(new OverlayPart(baseName, ordered));
            if (parts.Count > 1)
                Console.WriteLine($"[Recompiler] {result.Name} has {ordered.Count} functions, split across {parts.Count} classes");

            map[result.Name] = parts;
        }

        return map;
    }

    private sealed record OverlayPart(string Class, List<MipsFunction> Functions);

    private static void EmitOverlayFile(string overlayName, List<OverlayPart> parts,
        Dictionary<uint, string> knownFuncs, DiscImage? source, bool embed, bool debug, bool addressComments,
        bool disasmComments, int lbaStart, uint ovlBase, uint ovlSize, MipsInstruction[] instrs, string outDir,
        Dictionary<uint, uint> relocations)
    {
        var funcs = parts.SelectMany(p => p.Functions).ToList();
        var partClass = new Dictionary<uint, string>();
        foreach (var part in parts)
        foreach (var func in part.Functions)
            partClass[func.Start] = part.Class;

        var sb = new StringBuilder();
        sb.AppendLine("using RecompOne.Runtime.Context;");
        sb.AppendLine("using RecompOne.Runtime.Dispatch;");
        sb.AppendLine("using RecompOne.Runtime.Memory;");
        sb.AppendLine();
        sb.AppendLine("namespace Recompiled;");
        sb.AppendLine();
        foreach (var part in parts)
        {
            sb.AppendLine($"public static partial class {part.Class}");
            sb.AppendLine("{");

            foreach (var func in part.Functions)
            {
                var labels = LabelManager.Collect(func);
                var backEdges = LabelManager.CollectBackEdges(func);
                var ctx = new FunctionContext
                {
                    FuncStart = func.Start,
                    FuncEnd = func.End,
                    KnownFunctions = knownFuncs,
                    Labels = labels,
                    BackEdges = backEdges,
                    Debug = debug,
                    AddressComments = addressComments,
                    DisasmComments = disasmComments,
                    JumpTablesByJr = func.JumpTables.ToDictionary(j => j.JrVram),
                    RaReturnJrs = FunctionDetector.ComputeRaReturnJrs(func),
                    LinkReturns = LabelManager.CollectLinkReturns(func),
                    AllInstructions = instrs,
                    Relocations = relocations
                };
                sb.Append(FunctionEmitter.Emit(func, ctx));
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }
        
        sb.AppendLine($"public sealed class {DispatchTableName(overlayName)} : IOverlay");
        sb.AppendLine("{");
        sb.AppendLine($"    public string Name => \"{overlayName}\";");
        sb.AppendLine($"    public int LbaStart => {lbaStart};");
        sb.AppendLine($"    public uint Base => 0x{ovlBase:X8}u;");
        sb.AppendLine($"    public uint Size => 0x{ovlSize:X}u;");

        //the bytes this code was recompiled from, the runtime compares ram against them before running it
        if (ovlBase != 0 && ovlSize != 0 && instrs.Length > 0 && instrs[0].Vram == ovlBase)
        {
            var image = new byte[Math.Min(ovlSize, (uint)instrs.Length * 4)];
            for (var i = 0; i < image.Length / 4; i++)
                BitConverter.TryWriteBytes(image.AsSpan(i * 4), instrs[i].Word);

            //start/end of every function, so only the bytes a function was built from get checked
            var ranges = new SortedDictionary<uint, uint>();
            foreach (var f in funcs.Where(f => !f.IsStub && f.End > f.Start))
            {
                var e = Math.Min(f.End, ovlBase + (uint)image.Length);
                if (f.Start < ovlBase || e <= f.Start) continue;
                ranges[f.Start] = ranges.TryGetValue(f.Start, out var prev) ? Math.Max(prev, e) : e;
            }

            if (!embed && source != null)
            {
                var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image));
                var archive = source.Archive == null ? "null" : $"\"{source.Archive}\"";
                sb.AppendLine($"    public ImageSource? Source => new(\"{source.File.Replace("\\", "\\\\")}\", {archive}, {source.Entry}, " +
                              $"0x{source.Skip:X}, 0x{image.Length:X}, \"{sha}\");");
            }
            else
            {
                if (!embed)
                    Console.WriteLine($"[Recompiler] WARNING: {overlayName}: its bytes do not come straight from a disc file, " +
                                      "embedding them although embedImages is false");
                sb.AppendLine("    private static byte[]? _image;");
                sb.AppendLine($"    public byte[]? Image => _image ??= System.Convert.FromBase64String(\"{Convert.ToBase64String(image)}\");");
            }
            sb.Append("    public uint[] FunctionRanges { get; } = [");
            sb.Append(string.Join(", ", ranges.Select(r => $"0x{r.Key:X8}u, 0x{r.Value:X8}u")));
            sb.AppendLine("];");
        }
        sb.AppendLine("    public IReadOnlyDictionary<uint, Action<CpuContext, IMemory>> Functions { get; } =");
        sb.AppendLine("        new Dictionary<uint, Action<CpuContext, IMemory>>");
        sb.AppendLine("        {");
        foreach (var func in funcs.Where(f => !f.IsStub).OrderBy(f => f.Start))
            sb.AppendLine($"            [0x{func.Start:X8}u] = " +
                          $"{Owner(partClass, func.Start, parts[0].Class)}.{func.EmittedName},");
        sb.AppendLine("        };");
        sb.AppendLine("}");

        File.WriteAllText(Path.Combine(ImageDir?.Invoke(overlayName) ?? outDir, $"{overlayName}.cs"), sb.ToString());
    }

    private static string DispatchTableName(string name)
    {
        return $"{char.ToUpperInvariant(name[0])}{name[1..]}DispatchTable";
    }

    private static void ResolveCollisions(List<MipsFunction> allFuncs)
    {
        var crossOverlayDups = allFuncs.GroupBy(f => f.Name)
            .Where(g => g.Select(f => f.OverlayName).Distinct().Count() > 1)
            .Select(g => g.Key).ToHashSet();

        foreach (var func in allFuncs)
            func.EmittedName = SafeFuncName(
                crossOverlayDups.Contains(func.Name) && !string.IsNullOrEmpty(func.OverlayName)
                    ? $"{func.Name}_{SafeIdentifier(func.OverlayName)}"
                    : func.Name);

        foreach (var group in allFuncs.GroupBy(f => (f.OverlayName, f.EmittedName)).Where(g => g.Count() > 1))
        foreach (var func in group)
            func.EmittedName = $"{func.EmittedName}_{func.Start:X8}";
    }

    private static string SafeFuncName(string s)
    {
        return Regex.Replace(s, @"[^A-Za-z0-9_]", "_");
    }

    private static readonly byte[] PsxExeMagic = "PS-X EXE"u8.ToArray();

    public readonly record struct ExeInfo(uint Pc, uint TAddr, uint TSize);

    private static (byte[]? data, int lba, ExeInfo? exe, DiscImage? image) ResolveOverlay(DiscFs fs, OverlayConfig cfg)
    {
        try
        {
            if (cfg.Lba >= 0)
            {
                var sz = cfg.Size ?? throw new InvalidOperationException($"'size' is required when using 'lba' for overlay '{cfg.Name}'");
                return (Unpack(fs.ReadSectors(cfg.Lba, sz), cfg), cfg.Lba, null, null);
            }

            byte[] full;
            var lba = -1;
            string source;
            if (cfg.LocalFile != null)
            {
                if (!File.Exists(cfg.LocalFile))
                {
                    Console.WriteLine($"[Recompiler] WARNING: local file not found: {cfg.LocalFile}");
                    return (null, -1, null, null);
                }

                full = File.ReadAllBytes(cfg.LocalFile);
                source = cfg.LocalFile;
            }
            else if (cfg.File != null)
            {
                if (!fs.Locate(cfg.File, out lba, out _))
                {
                    Console.WriteLine($"[Recompiler] WARNING: disc file not found: {cfg.File}");
                    return (null, -1, null, null);
                }

                full = fs.ReadFile(cfg.File);
                source = cfg.File;
            }
            else
            {
                Console.WriteLine($"[Recompiler] WARNING: overlay '{cfg.Name}' has no 'file', 'localFile' or 'lba' source defined");
                return (null, -1, null, null);
            }

            //an archive entry is not at a fixed sector of its own, so it cant be tracked by lba, the runtime
            //switches to it by checking the ram contents instead
            if (cfg.Archive != null)
            {
                full = Psx.Compression.Archives.Extract(cfg.Archive, full, cfg.Entry, cfg.Name);
                source = $"{source} [{cfg.Archive} entry {cfg.Entry}]";
                lba = -1;
            }

            var skip = cfg.Skip;
            int? exeTextSize = null;
            ExeInfo? exe = null;

            //a PS-X EXE carries a 0x800 header, the code starts after it, without skipping it every
            //instruction ends up analyzed 0x800 above the address it really runs at
            if (cfg.Offset == 0 && skip == 0 && full.Length >= 0x800 && full.AsSpan(0, 8).SequenceEqual(PsxExeMagic))
            {
                skip = 0x800;
                var pc = BitConverter.ToUInt32(full, 0x10);
                var tAddr = BitConverter.ToUInt32(full, 0x18);
                var tSize = BitConverter.ToUInt32(full, 0x1C);
                if (tSize > 0 && tSize <= full.Length - 0x800) exeTextSize = (int)tSize;
                exe = new ExeInfo(pc, tAddr, tSize);
                Console.WriteLine($"[Recompiler] '{cfg.Name}': {source} is a PS-X EXE, skipping its 0x800 header (pc=0x{pc:X8} t_addr=0x{tAddr:X8} t_size=0x{tSize:X})");
                if (cfg.Base != null && Convert.ToUInt32(cfg.Base, 16) + (uint)cfg.Rebase != tAddr)
                    Console.WriteLine($"[Recompiler] WARNING: '{cfg.Name}' base {cfg.Base} does not match the EXE load address 0x{tAddr:X8}");
            }

            var absLba = lba < 0 ? -1 : lba + (cfg.Offset + skip) / 2048;
            var start = cfg.Offset + skip;
            var length = cfg.Size ?? exeTextSize ?? full.Length - start;
            var slice = full.AsSpan(start, length).ToArray();
            var data = Unpack(slice, cfg);
            //the runtime can read it back from the disc only when nothing but the archive unpacking touched it
            var plain = cfg.LocalFile == null && cfg.Compression == null && ReferenceEquals(data, slice);
            return (data, absLba, exe, plain ? new DiscImage(cfg.File!, cfg.Archive, cfg.Entry, start) : null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Recompiler] WARNING: failed to resolve disc data for '{cfg.Name}': {ex.Message}");
            return (null, -1, null, null);
        }
    }


    private static void RebaseElf(FunctionInfo elf, int delta, byte[] discBin)
    {
        var d = (uint)delta;
        elf.TextBase += d;
        elf.LoadAddress += d;
        foreach (var f in elf.Functions) f.Address += d;
        foreach (var f in elf.NoTypeSymbols) f.Address += d;
        foreach (var s in elf.DataSections) s.Va += d;
        elf.TextData = discBin;
    }

    private static byte[] Unpack(byte[] data, OverlayConfig cfg)
    {
        return Psx.Compression.Compression.Apply(data, cfg.Name, cfg.Compression);
    }


    private static bool PatchNameMatches(MipsFunction func, string? patchFunction)
    {
        if (string.IsNullOrEmpty(patchFunction)) return false;
        if (string.Equals(func.Name, patchFunction, StringComparison.Ordinal)) return true;
        if (string.IsNullOrEmpty(func.OverlayName)) return false;
        return string.Equals(func.Name, $"{func.OverlayName.ToUpperInvariant()}_{patchFunction}",
            StringComparison.Ordinal);
    }

    private static void ApplyPatches(List<MipsFunction> funcs, PatchEntry[] patches)
    {
        if (patches.Length == 0) return;
        var applied = 0;
        foreach (var patch in patches)
        {
            uint? addr = string.IsNullOrEmpty(patch.Address) ? null : Convert.ToUInt32(patch.Address, 16);
            var matched = 0;
            foreach (var func in funcs)
            {
                if (!patch.MatchesOverlay(func.OverlayName)) continue;
                var hit = addr.HasValue ? func.Start == addr.Value : PatchNameMatches(func, patch.Function);
                if (!hit) continue;
                matched++;
                switch (patch.Mode.ToLowerInvariant())
                {
                    case "pre":
                        if (!func.PreHookTargets.Contains(patch.Target))
                            func.PreHookTargets.Add(patch.Target);
                        break;
                    case "post":
                        if (!func.PostHookTargets.Contains(patch.Target))
                            func.PostHookTargets.Add(patch.Target);
                        break;
                    default:
                        if (func.IsPatch && !string.Equals(func.PatchTarget, patch.Target, StringComparison.Ordinal))
                        {
                            Console.WriteLine($"[Recompiler] WARNING: '{func.Name}' @ {func.OverlayName} already replaced by '{func.PatchTarget}', ignoring '{patch.Target}'"); //logeg
                            continue;
                        }

                        func.IsPatch = true;
                        func.PatchTarget = patch.Target;
                        break;
                }

                applied++;
            }

            if (matched == 0)
                Console.WriteLine(
                    $"[Recompiler] WARNING: patch '{patch.Target}' matched nothing (overlay='{patch.OverlayLabel}' function='{patch.Function}' address='{patch.Address}')");
        }

        Console.WriteLine($"[Recompiler] applied {applied} patches");
    }


    private static string SafeIdentifier(string s)
    {
        return Regex.Replace(s, @"[^a-zA-Z0-9_]", "_").TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
    }
}