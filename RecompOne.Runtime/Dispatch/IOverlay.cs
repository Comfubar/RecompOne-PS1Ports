using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Dispatch;

public interface IOverlay
{
    string Name { get; }
    int LbaStart => -1;
    uint Base => 0;
    uint Size => 0;
    IReadOnlyDictionary<uint, Action<CpuContext, IMemory>> Functions { get; }

    //where the bytes the code was recompiled from are on the disc (see OverlayImages), when they are not embedded
    ImageSource? Source => null;

    //the bytes the code was recompiled from (starting at Base): embedded by the recompiler, or read from the disc,
    //null when neither is available
    byte[]? Image => OverlayImages.Get(Name, Source);

    //start, end pairs of every recompiled function inside Image
    uint[] FunctionRanges => [];
}