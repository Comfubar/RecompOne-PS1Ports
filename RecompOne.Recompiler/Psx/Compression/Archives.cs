namespace RecompOne.Recompiler.Psx.Compression;

//container formats that hold several files, an overlay picks one of them by its entry index
public static class Archives
{
    public static byte[] Extract(string kind, byte[] archive, int entry, string overlay)
    {
        return kind.ToLowerInvariant() switch
        {
            "dpac" => RecompOne.Runtime.Cdrom.Dpac.Extract(archive, entry, overlay),
            _ => throw new NotSupportedException($"overlay '{overlay}': unknown archive format '{kind}'")
        };
    }
}
