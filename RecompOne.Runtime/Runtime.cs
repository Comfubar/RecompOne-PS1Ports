using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime;

public enum RunMode
{
    Retail,
    Devkit
}

public sealed class HardResetSignal : Exception;

public static class Runtime
{
    public static CpuContext? Cpu { get; private set; }
    public static IMemory? Mem { get; private set; }
    public static Gpu? Gpu;
    public static Spu? Spu;
    public static Mdec? Mdec;
    public static Hardware.Timers? Timers;
    public static Cdrom.CdController? Cd;

    public static RunMode Mode { get; private set; } = RunMode.Retail;

    public static void SetMode(RunMode mode)
    {
        Mode = mode;
        //devkit vs retail, devkits reads from sim and has more ram
    }

    public static uint RamSize { get; internal set; } = MemoryMap.RetailRamSize;
    public static uint RamWordMask => (RamSize - 1) & ~3u;
    public static string CdPath => Config.ConfigManager.Game.CdPath;

    public static Func<string, string?>? DiscValidator;

    public static string? ValidateDisc(string path)
    {
        try
        {
            return DiscValidator?.Invoke(path);
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    public static Config.ViewConfig View => Config.ConfigManager.View;

    private static readonly List<Action<Config.ViewConfig>> _defaults = [];

    public static void Defaults(Action<Config.ViewConfig> apply)
    {
        _defaults.Add(apply);
    }

    internal static void ApplyDefaults()
    {
        foreach (var apply in _defaults) apply(Config.ConfigManager.View);
    }

    public static void SaveView()
    {
        Config.ConfigManager.SaveView(Host.Window.PanelManager.Panels);
    }

    public static Hardware.MemoryCard CardA = new(Config.ConfigManager.DataPath("carda.sav")) { Enabled = true };
    public static Hardware.MemoryCard CardB = new(Config.ConfigManager.DataPath("cardb.sav")) { Enabled = true };

    private static void LoadMemoryCards()
    {
        var g = Config.ConfigManager.Game;
        CardA = new MemoryCard(Fallback(g.CardAPath, "carda.sav")) { Enabled = g.CardAEnabled };
        CardB = new MemoryCard(Fallback(g.CardBPath, "cardb.sav")) { Enabled = g.CardBEnabled };

        static string Fallback(string path, string def)
        {
            return Config.ConfigManager.DataPath(string.IsNullOrWhiteSpace(path) ? def : path);
        }
    }

    public static readonly RamLogger RamLog = new();
    public static readonly Dispatch.OverlayEventLog OverlayLog = new();

    private static bool _hostReady;

    public static void Initialize(string title)
    {
        if (!_hostReady)
        {
            _hostReady = true;
            Diagnostics.ConsoleMirror.Install();
            Diagnostics.CrashReporter.Install();
            Host.GpuJobs.Run(() => HostWindow.Initialize(title));
            Audio.Initialize();
        }

        LoadMemoryCards();
        Audio.SetMasterVolume(Config.ConfigManager.Game.Muted ? 0f : Config.ConfigManager.Game.MasterVolume);
        if (Event.HasAnyListeners<RuntimeReadyEvent>()) Event.Dispatch(new RuntimeReadyEvent());
    }

    public static void WaitForValidDisc()
    {
        HostWindow.WaitForValidDisc();
    }

    public static string Title
    {
        get => HostWindow.Title;
        set => HostWindow.Title = value;
    }

    public static void SetTitle(string title)
    {
        HostWindow.SetTitle(title);
    }

    public static void SetIcon(byte[] data)
    {
        HostWindow.SetIcon(data);
    }

    public static void SetIcon(byte[] rgba, int width, int height)
    {
        HostWindow.SetIcon(rgba, width, height);
    }

    public static void ClearIcon()
    {
        HostWindow.ClearIcon();
    }

    public static void ShowNotice(string message)
    {
        Host.Window.NoticePopup.Show(message);
    }

    public static void SetStartupNotice(string message, string title = "common.notice",
        string ackKey = "StartupNoticeAck")
    {
        Host.Window.StartupNoticePopup.Set(message, title, ackKey);
    }

    public static void AddLanguages(string json)
    {
        Host.Window.Localization.Merge(json);
    }

    public static bool AddLanguages(System.Reflection.Assembly assembly, string resourceName)
    {
        return Host.Window.Localization.MergeEmbedded(assembly, resourceName);
    }

    public static void SetContext(CpuContext c, IMemory m)
    {
        Cpu = c;
        Mem = m;
    }

    private static volatile bool _hardResetPending;

    public static bool HardResetPending => _hardResetPending;

    public static void HardReset()
    {
        _hardResetPending = true;
    }

    private static void ResetForBoot()
    {
        Audio.Detach();

        Sdk.LibCd.Reset();
        Sdk.LibCdStream.Reset();
        Assets.Xa.XaRouter.Reset();
        Sdk.LibPad.Reset();
        Dispatch.Dispatcher.Reset();
        Bios.BiosB.Reset();
        OverlayLog.Clear();

        Cpu = null;
        Mem = null;
        Gpu = null;
        Spu = null;
        Cd = null;

        //this runs on the game thread, but the gl context belongs to the thread that owns the window (Run claims
        //it), flushing here replays recorded draws into gl from the wrong thread and crashed in the driver
        Host.GpuJobs.Run(() =>
        {
            if (Hle.GpuHle.Backend is not { Ready: true } backend) return;
            backend.FillRect(0, 0, Gpu.VramWidth, Gpu.VramHeight, 0);
            backend.Flush();
        });
    }

    private static volatile bool _gameDone;
    private static readonly System.Diagnostics.Stopwatch _presentWatch = System.Diagnostics.Stopwatch.StartNew();
    private static double _nextPresentMs;
    
    //maxStackSize: recompiled code turns guest calls into nested host calls, deep call chains can need more than
    //the default 1MB thread stack, 0 keeps the default
    //returns the process exit code: 0 when the game ended normally, 1 when it crashed (see CrashReporter)
    public static int Run(Action boot, int maxStackSize = 0)
    {
        Diagnostics.SessionLog.StartIfConfigured();
        Diagnostics.CrashReporter.Install();
        Pgxp.PgxpGpu.Init();
        Host.GpuJobs.Claim();
        
        var thread = new Thread(() => RunGame(boot), maxStackSize)
        {
            IsBackground = true,
            Name = "game"
        };
        
        thread.Start();
        
        while (!_gameDone)
            PresentLoop();

        return _crashed ? 1 : 0;
    }

    private static volatile bool _crashed;
    
    private static void RunGame(Action boot)
    {
        while (true)
            try
            {
                boot();
                break;
            }
            catch (HardResetSignal)
            {
                Console.WriteLine("[Runtime] hard reset, game restarting");
                ResetForBoot();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[Runtime] runtime has crashed: {e}");
                Diagnostics.CrashReporter.Report(e, "exception on the game thread");
                _crashed = true;
                break;
            }
        
        _gameDone = true;
    }
    
    private static double _idleMark;
    
    private static void IdleRedraw()
    {
        var now = FrameClock.Now;
        if (now - _idleMark < FrameClock.FrameMs) return;
        
        _idleMark = now;
        HostWindow.Compose(Gpu, false);
        Host.GpuJobs.Drain();
    }
    
    private static void PresentLoop()
    {
        Host.GpuJobs.Drain();
        
        if (!HostWindow.Ready)
        {
            Thread.Sleep(1);
            return;
        }
        
        HostWindow.PumpEvents();
        
        var interp = Interp.Interp.Backend;
        
        if (interp == null || !interp.Acquire())
        {
            IdleRedraw();
            Thread.Sleep(1);
            return;
        }
        
        _idleMark = 0.0;
        HostWindow.AdvanceFrame();
        
        var frames = interp.BeginPresent();
        var pace = interp.PaceMs;
        
        for (var i = 0; i < frames; i++)
        {
            if (!interp.Affordable(i)) break;
            
            interp.Compose(i);
            Pace(pace);
            HostWindow.Compose(Gpu);
            Host.GpuJobs.Drain();
        }
        
        if (frames == 0)
        {
            interp.Compose(0);
        }
        
        interp.EndPresent();
    }
    
    //the display frames only read as smooth if they land evenly in time, so the schedule is held against the clock instead of leaving it to the swap
    private static void Pace(double intervalMs)
    {
        if (Interrupts.Turbo) return;

        var now = _presentWatch.Elapsed.TotalMilliseconds;
        
        if (intervalMs <= 0.0 || _nextPresentMs <= 0.0 || now - _nextPresentMs > 250.0)
        {
            _nextPresentMs = now + intervalMs;
            return;
        }
        
        var wait = _nextPresentMs - now;
        
        if (wait > 1.5) Thread.Sleep((int)(wait - 1.0));
        while (_presentWatch.Elapsed.TotalMilliseconds < _nextPresentMs) Thread.SpinWait(32);
        
        _nextPresentMs += intervalMs;
    }
    
    public static void PresentFrame()
    {
        Diagnostics.TestScript.Tick();
        if (_hardResetPending)
        {
            _hardResetPending = false;
            throw new HardResetSignal();
        }

        Diagnostics.CrashReporter.Tick();
        Interp.Interp.Backend?.Publish();
        
        Audio.Attach(Spu);
        FrameClock.MarkFrame();
        Sdk.LibCd.Tick();
        if (Cpu != null && Mem != null) Sdk.LibMcrd.Tick(Cpu, Mem);
        if (Mem != null)
        {
            Bios.BiosB.RefreshPad(Mem);
            Sdk.LibPad.Refresh(Mem);
        } //is this correct?

        //no Interrupts.Raise(0) here: the vblank interrupt comes from the real time clock (Interrupts.TickVBlank, reached
        //through the Poll calls in the recompiled code and while VSync waits). Raising it for every presented frame as
        //well gave the game about 120 vblank interrupts a second, so everything it times from its vblank handler (pad
        //polling, menu auto repeat, sound sequencing) ran twice as fast
    }

    public static void DispatchIrq(int irq)
    {
        if (Cpu != null && Mem != null)
            Interrupts.Deliver(irq, Cpu, Mem);
    }

    //the process exit code once the window closes (a test script sets it, for example 3 for a failed step)
    public static int ExitCode { get; set; }

    public static void Shutdown()
    {
        Audio.Shutdown();
        HostWindow.Shutdown();
    }
}