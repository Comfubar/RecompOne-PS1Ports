namespace RecompOne.Runtime.Events;

/// <summary>the game reads a controller port</summary>
public sealed class PadReadEvent : GameEvent
{
    public int Port;

    //multitap slot on that port (0 = A ... 3 = D), 0 without a multitap
    public int Slot;

    //active low: a pressed button has its bit cleared
    public ushort Buttons;
}