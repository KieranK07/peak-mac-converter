using AssetsTools.NET;
using K4os.Compression.LZ4;

namespace Lmc;

// One platform's program blob: LZ4-compressed segments; segment 0 starts with the entry table
// (count, then offset/length/segment per entry).
sealed class ShaderBlob
{
    public readonly List<byte[]> Segments = new();
    public readonly List<(int Offset, int Length, int Segment)> Entries = new();

    public ShaderBlob(AssetTypeValueField shader, int platformIndex)
    {
        var offs = shader["offsets"]["Array"][platformIndex]["Array"].Children.Select(x => x.AsUInt).ToList();
        var clens = shader["compressedLengths"]["Array"][platformIndex]["Array"].Children.Select(x => x.AsUInt).ToList();
        var dlens = shader["decompressedLengths"]["Array"][platformIndex]["Array"].Children.Select(x => x.AsUInt).ToList();
        var blob = shader["compressedBlob"]["Array"].AsByteArray;
        for (var i = 0; i < offs.Count; i++)
        {
            var seg = new byte[dlens[i]];
            var n = LZ4Codec.Decode(blob, (int)offs[i], (int)clens[i], seg, 0, seg.Length);
            if (n != seg.Length) throw new Exception($"lz4 segment {i}: {n}/{seg.Length}");
            Segments.Add(seg);
        }
        var r = new BinaryReader(new MemoryStream(Segments[0]));
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++) Entries.Add((r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
    }

    public byte[] Entry(int i) => Segments[Entries[i].Segment].AsSpan(Entries[i].Offset, Entries[i].Length).ToArray();
}
