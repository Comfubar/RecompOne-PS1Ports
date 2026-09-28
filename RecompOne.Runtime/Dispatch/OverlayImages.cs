using System.Security.Cryptography;
using RecompOne.Runtime.Cdrom;

namespace RecompOne.Runtime.Dispatch;

//where the bytes an image was recompiled from are on the disc, so a port does not have to carry them (config
//"embedImages": false). File is a path on the disc; Archive/Entry pick an entry of a DPAC archive; Skip bytes are
//dropped from the front (0x800 for a PS-X EXE header) and Size bytes kept. Sha256 is of the kept bytes.
public sealed record ImageSource(string File, string? Archive, int Entry, int Skip, int Size, string Sha256);

//reads and checks the reference bytes of every overlay from the game's own disc, once
public static class OverlayImages
{
    private static readonly Dictionary<string, byte[]> _cache = [];

    public static byte[]? Get(string overlay, ImageSource? source)
    {
        if (source == null) return null;
        lock (_cache)
        {
            if (_cache.TryGetValue(overlay, out var hit)) return hit;
        }

        var fs = Runtime.Cd?.Fs ?? throw new InvalidOperationException(
            $"overlay {overlay}: its reference bytes come from the disc, but no disc is open");

        var data = fs.ReadFile(source.File);
        if (source.Archive != null)
        {
            if (!string.Equals(source.Archive, "dpac", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"overlay {overlay}: archive format '{source.Archive}' is not supported at runtime");
            data = Dpac.Extract(data, source.Entry, overlay);
        }

        if (source.Skip + source.Size > data.Length)
            throw new InvalidDataException($"overlay {overlay}: {source.File} is shorter than the recompiled image");

        var image = data.AsSpan(source.Skip, source.Size).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(image));
        if (!string.Equals(hash, source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"overlay {overlay}: the bytes on this disc ({source.File}) are not the ones the code was recompiled from " +
                $"(sha256 {hash}, expected {source.Sha256}). Wrong game version or a bad dump.");

        lock (_cache)
        {
            _cache[overlay] = image;
        }

        return image;
    }
}
