using System.Buffers.Binary;
using System.Text;

namespace RecompOne.Runtime.Cdrom;

//"DPAC" archives (EXE.PAC, LD.PAC, ...) of the game with serial SLUS-01234, ported from its own code in SLUS_012.34:
//  0x8002A20C registers an archive: reads its first 7 sectors, the table of contents is the u32 at +4 bytes
//             long and starts at +0x800
//  0x8002A39C looks a path up in the table of contents ("/EXE/0006" -> directory "EXE ", file "0006")
//  0x8002A75C reads the entry: sector = archive lba + entry sector, length = size * 256
//  0x80013628 unpacks an EXE.PAC entry: 16 byte "BPE " header, u32 at +0xC is the unpacked size, packed data
//             at +0x10, then 0x800612A0 (byte pair decoding) writes it out, then it is Exec'd
//
//header: "DPAC", u32 toc length in bytes, u32 data length, u32 (7 on EXE.PAC, the game never reads it)
//toc node: char[4] name (space padded), u32 w: w & 0xFFF = number of 4 byte words that follow,
//          (w >> 12) & 7 = depth, w & 0x8000 = numbered directory, (w >> 16) = first sector (numbered only)
//  numbered directory: children are {u16 id, u16 size/256}, the file name is the id in hex ("0010" = 0x10),
//                      each child starts where the previous one ends (rounded up to a sector)
//  named directory:    children are {char[4] name, u16 sector, u16 size/256}
//entry sectors are relative to the end of the 8 header sectors (0x4000)
public static class Dpac
{
    public const int DataSector = 8;

    public sealed record Entry(int Index, string Path, int Sector, int Size);

    public static bool IsDpac(ReadOnlySpan<byte> data)
    {
        return data.Length >= 0x800 && data[..4].SequenceEqual("DPAC"u8);
    }

    public static List<Entry> ReadToc(byte[] archive)
    {
        if (!IsDpac(archive)) throw new InvalidDataException("not a DPAC archive");

        var tocLength = BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(4));
        var toc = archive.AsSpan(0x800, tocLength);
        var entries = new List<Entry>();
        ReadNodes(toc, 0, "", entries);
        return entries;
    }

    //the game's lookup (0x8002A4DC) walks the nodes of one level: a node whose name matches the path component
    //at its depth is entered (next node is its first child), any other node is skipped whole (8 + words * 4),
    //so a directory that is not the last one of the path holds nodes one level deeper, the last one holds
    //the file entries
    private static void ReadNodes(ReadOnlySpan<byte> toc, int depth, string prefix, List<Entry> entries)
    {
        var pos = 0;
        while (pos + 8 <= toc.Length)
        {
            var name = Encoding.ASCII.GetString(toc.Slice(pos, 4)).TrimEnd(' ', '\0');
            var w = BinaryPrimitives.ReadUInt32LittleEndian(toc[(pos + 4)..]);
            var words = (int)(w & 0xFFF);
            if (((w >> 12) & 7) != depth)
                throw new InvalidDataException($"DPAC node {prefix}/{name} has depth {(w >> 12) & 7}, expected {depth}");
            if (pos + 8 + words * 4 > toc.Length)
                throw new InvalidDataException($"DPAC node {prefix}/{name} runs past its parent");

            var path = $"{prefix}/{name}";
            var children = toc.Slice(pos + 8, words * 4);
            pos += 8 + words * 4;

            if ((w & 0x8000) == 0 && IsNodeList(children, depth + 1))
            {
                ReadNodes(children, depth + 1, path, entries);
                continue;
            }

            ReadFiles(children, w, path, entries);
        }
    }

    private static bool IsNodeList(ReadOnlySpan<byte> span, int depth)
    {
        if (span.Length < 8) return false;
        var pos = 0;
        while (pos < span.Length)
        {
            if (pos + 8 > span.Length) return false;
            var w = BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 4)..]);
            if (((w >> 12) & 7) != depth) return false;
            pos += 8 + (int)(w & 0xFFF) * 4;
        }

        return pos == span.Length;
    }

    private static void ReadFiles(ReadOnlySpan<byte> children, uint w, string prefix, List<Entry> entries)
    {
        {
            if ((w & 0x8000) != 0)
            {
                //0x8002A5DC-0x8002A630
                var sector = (int)(w >> 16);
                for (var i = 0; i + 4 <= children.Length; i += 4)
                {
                    var id = BinaryPrimitives.ReadUInt16LittleEndian(children[i..]);
                    var size = BinaryPrimitives.ReadUInt16LittleEndian(children[(i + 2)..]) << 8;
                    entries.Add(new Entry(entries.Count, $"{prefix}/{id:X4}", sector + DataSector, size));
                    sector += (size + 2047) >> 11;
                }
            }
            else
            {
                //0x8002A634-0x8002A684, 0x8002A49C
                for (var i = 0; i + 8 <= children.Length; i += 8)
                {
                    var file = Encoding.ASCII.GetString(children.Slice(i, 4)).TrimEnd(' ');
                    var sector = BinaryPrimitives.ReadUInt16LittleEndian(children[(i + 4)..]);
                    var size = BinaryPrimitives.ReadUInt16LittleEndian(children[(i + 6)..]) << 8;
                    entries.Add(new Entry(entries.Count, $"{prefix}/{file}", sector + DataSector, size));
                }
            }
        }
    }

    public static byte[] ReadEntry(byte[] archive, Entry e)
    {
        var start = e.Sector * 2048;
        if (start + e.Size > archive.Length)
            throw new InvalidDataException($"DPAC entry {e.Path} runs past the end of the archive");
        return archive.AsSpan(start, e.Size).ToArray();
    }

    //the entry exactly as the game unpacks it (0x80013674-0x80013680)
    public static byte[] Unpack(byte[] archive, Entry e)
    {
        var raw = ReadEntry(archive, e);
        if (!IsBpe(raw)) throw new InvalidDataException($"DPAC entry {e.Path} is not BPE packed");
        var size = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(12));
        return Bpe.Decode(raw, 16, size);
    }

    public static bool IsBpe(ReadOnlySpan<byte> data)
    {
        return data.Length >= 16 && data[..4].SequenceEqual("BPE "u8);
    }

    public static byte[] Extract(byte[] archive, int index, string overlay)
    {
        var toc = ReadToc(archive);
        if (index < 0 || index >= toc.Count)
            throw new InvalidDataException($"overlay '{overlay}': DPAC entry {index} does not exist ({toc.Count} entries)");

        var e = toc[index];
        var data = Unpack(archive, e);
        Console.WriteLine($"[Dpac] '{overlay}': entry {index} {e.Path} unpacked {e.Size} -> {data.Length} bytes");
        return data;
    }
}

//byte pair decoding, a port of 0x800612A0 (a0 = src, a1 = dst, a2 = dst end). The game keeps left[] at
//0x1F800000, right[] at 0x1F800100 and the expansion stack at 0x1F800200 in the scratchpad.
public static class Bpe
{
    public static byte[] Decode(byte[] src, int pos, int outSize)
    {
        var dst = new byte[outSize];
        var d = 0;
        var left = new byte[256];
        var right = new byte[256];
        var stack = new Stack<byte>();

        while (d < outSize) //0x800612C0
        {
            //0x800612CC: left[i] = i, right[] keeps whatever the previous block left in it
            for (var i = 0; i < 256; i++) left[i] = (byte)i;

            //0x800612F0: pair table
            var c = 0;
            while (true)
            {
                int count = src[pos++];
                if (count > 127)
                {
                    //0x80061308: skip count - 127 codes, then exactly one pair
                    c += count - 127;
                    if (c == 256) break;
                    ReadPair(src, ref pos, left, right, c++);
                    if (c == 256) break;
                    continue;
                }

                //0x8006135C: count + 1 pairs, the end of the table is only checked after all of them
                for (var n = 0; n <= count; n++) ReadPair(src, ref pos, left, right, c++);
                if (c == 256) break;
            }

            //0x800613A4: block length, then expand every byte through the pair table
            var size = src[pos] | (src[pos + 1] << 8);
            pos += 2;
            for (; size > 0; size--)
            {
                var t = src[pos++];
                while (true)
                {
                    if (left[t] == t)
                    {
                        if (d >= outSize)
                            throw new InvalidDataException("BPE data expands past the declared size");
                        dst[d++] = t;
                        if (stack.Count == 0) break;
                        t = stack.Pop();
                    }
                    else
                    {
                        stack.Push(right[t]);
                        t = left[t];
                    }
                }
            }
        }

        return dst;
    }

    //a code that expands to itself has no right byte stored
    private static void ReadPair(byte[] src, ref int pos, byte[] left, byte[] right, int c)
    {
        if (c > 255) throw new InvalidDataException("BPE pair table has more than 256 codes");
        var t = src[pos++];
        left[c] = t;
        if (c != t) right[c] = src[pos++];
    }
}
