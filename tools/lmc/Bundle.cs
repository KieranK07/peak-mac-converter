using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;

namespace Lmc;

// A UnityFS archive (format 6-8, "data.unity3d"): header, an LZ4(HC) block table + directory, then the data stream
// cut into blocks (LZ4/LZ4HC or stored). Nodes are files inside the stream.
// Only what Unity player bundles use is supported: block info right after the header, no LZMA, no encryption.
sealed class Bundle : IDisposable
{
    public record Node(long Offset, long Size, uint Flags, string Path);
    public record struct Block(uint USize, uint CSize, ushort Flags);

    public readonly string FilePath;
    public uint Format, Flags;
    public string Version = "", Revision = "";
    public byte[] Hash = new byte[16];
    public readonly List<Block> Blocks = new();
    public readonly List<Node> Nodes = new();
    byte[] rawInfo = Array.Empty<byte>(), info = Array.Empty<byte>();  // block info as stored and decompressed
    long[] uStart = Array.Empty<long>(), cStart = Array.Empty<long>();
    readonly FileStream fs;
    public long DataStart;

    const uint CombinedInfo = 0x40, InfoAtEnd = 0x80, PadAtStart = 0x200;

    public Bundle(string path)
    {
        FilePath = path;
        fs = File.OpenRead(path);
        var r = new BinaryReader(fs);
        if (CStr(r) != "UnityFS") throw new Exception($"{path}: not a UnityFS bundle");
        Format = BE32(r);
        Version = CStr(r); Revision = CStr(r);
        var size = BE64(r);
        uint csz = BE32(r), usz = BE32(r);
        Flags = BE32(r);
        if (size != fs.Length) throw new Exception($"{path}: header size {size}, file {fs.Length}");
        if ((Flags & CombinedInfo) == 0 || (Flags & InfoAtEnd) != 0 || Flags >> 10 != 0) throw new Exception($"{path}: unsupported bundle flags 0x{Flags:x}");
        if (Format >= 7) fs.Position = Align16(fs.Position);
        rawInfo = r.ReadBytes((int)csz);
        info = Decompress(rawInfo, (int)usz, Flags & 0x3f);
        DataStart = fs.Position;
        if ((Flags & PadAtStart) != 0) DataStart = Align16(DataStart);

        var br = new BinaryReader(new MemoryStream(info));
        Hash = br.ReadBytes(16);
        var nb = BE32(br);
        for (var i = 0; i < nb; i++) Blocks.Add(new Block(BE32(br), BE32(br), BinaryPrimitives.ReverseEndianness(br.ReadUInt16())));
        var nn = BE32(br);
        for (var i = 0; i < nn; i++) Nodes.Add(new Node(BE64(br), BE64(br), BE32(br), CStr(br)));
        if (br.BaseStream.Position != info.Length) throw new Exception($"{path}: {info.Length - br.BaseStream.Position} bytes after the directory");
        Index();
        if (DataStart + cStart[^1] != fs.Length) throw new Exception($"{path}: blocks end at {DataStart + cStart[^1]}, file is {fs.Length}");
    }

    void Index()
    {
        uStart = new long[Blocks.Count + 1]; cStart = new long[Blocks.Count + 1];
        for (var i = 0; i < Blocks.Count; i++) { uStart[i + 1] = uStart[i] + Blocks[i].USize; cStart[i + 1] = cStart[i] + Blocks[i].CSize; }
    }

    public long DataSize => uStart[^1];
    public Node Get(string path) => Nodes.Single(n => n.Path == path);

    public byte[] RawBlock(int i)
    {
        var b = new byte[Blocks[i].CSize];
        lock (fs) { fs.Position = DataStart + cStart[i]; fs.ReadExactly(b); }
        return b;
    }

    public byte[] ReadBlock(int i) => Decompress(RawBlock(i), (int)Blocks[i].USize, Blocks[i].Flags & 0x3fu);

    // Bytes [pos, pos + dst.Length) of the uncompressed data stream.
    public void ReadData(long pos, Span<byte> dst)
    {
        var i = Array.BinarySearch(uStart, pos);
        if (i < 0) i = ~i - 1;
        while (dst.Length > 0)
        {
            var b = ReadBlock(i);
            var lo = (int)(pos - uStart[i]);
            var n = Math.Min(b.Length - lo, dst.Length);
            b.AsSpan(lo, n).CopyTo(dst);
            dst = dst[n..]; pos += n; i++;
        }
    }

    public byte[] Read(Node n)
    {
        var b = new byte[n.Size];
        ReadData(n.Offset, b);
        return b;
    }

    // Writes this bundle with some nodes' bytes replaced (by path). Nodes keep their order and stay contiguous; the
    // stream is cut into blocks of the original block size. An output block whose bytes equal the source block at
    // the same index keeps its stored bytes (so a no-op rewrite is byte-identical); other blocks are LZ4HC
    // compressed, or stored when that doesn't shrink them, as Unity does. recompress: compress every block.
    // patch: same-size edits of nodes that are otherwise copied (bytes written at an offset into the node).
    public void Write(string outPath, IReadOnlyDictionary<string, byte[]> repl, bool recompress = false, IReadOnlyDictionary<string, (long At, byte[] Bytes)>? patch = null)
    {
        foreach (var k in repl.Keys.Concat(patch?.Keys ?? Enumerable.Empty<string>())) Get(k);
        if (patch != null && patch.Keys.Any(repl.ContainsKey)) throw new Exception("a node is both replaced and patched");
        var blockSize = (int)Blocks[0].USize;
        if (Blocks.Take(Blocks.Count - 1).Any(b => b.USize != blockSize)) throw new Exception("blocks of mixed sizes");
        var nodes = new List<Node>();
        long pos = 0;
        foreach (var n in Nodes)
        {
            var size = repl.TryGetValue(n.Path, out var nb) ? nb.Length : n.Size;
            nodes.Add(n with { Offset = pos, Size = size });
            pos += size;
        }
        var total = pos;

        // the output stream as pieces: (start, length, source offset or replacement)
        byte[] Out(long at, int len)
        {
            var buf = new byte[len];
            for (var k = 0; k < nodes.Count && len > 0; k++)
            {
                var o = nodes[k];
                if (at >= o.Offset + o.Size || o.Size == 0) continue;
                var lo = at - o.Offset; var n = (int)Math.Min(o.Size - lo, len);
                var dst = buf.AsSpan(buf.Length - len, n);
                if (repl.TryGetValue(o.Path, out var nb)) nb.AsSpan((int)lo, n).CopyTo(dst);
                else
                {
                    ReadData(Nodes[k].Offset + lo, dst);
                    if (patch != null && patch.TryGetValue(o.Path, out var pt))
                        for (var b = 0; b < pt.Bytes.Length; b++)
                            if (pt.At + b >= lo && pt.At + b < lo + n) dst[(int)(pt.At + b - lo)] = pt.Bytes[b];
                }
                at += n; len -= n;
            }
            return buf;
        }

        var tmp = outPath + ".tmp";
        var blocks = new List<Block>();
        using (var w = File.Create(tmp))
        {
            w.Position = 1 << 20;  // room for header + block info, moved into place at the end
            var nOut = (int)((total + blockSize - 1) / blockSize);
            const int batch = 256;
            for (var b0 = 0; b0 < nOut; b0 += batch)
            {
                var cnt = Math.Min(batch, nOut - b0);
                var res = new (byte[] data, Block blk)[cnt];
                Parallel.For(0, cnt, j =>
                {
                    var i = b0 + j;
                    var len = (int)Math.Min(blockSize, total - (long)i * blockSize);
                    var u = Out((long)i * blockSize, len);
                    if (!recompress && i < Blocks.Count && Blocks[i].USize == len && u.AsSpan().SequenceEqual(ReadBlock(i)))
                    {
                        res[j] = (RawBlock(i), Blocks[i]);
                        return;
                    }
                    var c = new byte[LZ4Codec.MaximumOutputSize(len)];
                    var n = LZ4Codec.Encode(u, 0, len, c, 0, c.Length, LZ4Level.L12_MAX);
                    res[j] = n > 0 && n < len ? (c[..n], new Block((uint)len, (uint)n, 3)) : (u, new Block((uint)len, (uint)len, 0));
                });
                foreach (var (data, blk) in res) { w.Write(data); blocks.Add(blk); }
            }

            var ms = new MemoryStream();
            var iw = new BinaryWriter(ms);
            iw.Write(Hash);
            WBE32(iw, (uint)blocks.Count);
            foreach (var b in blocks) { WBE32(iw, b.USize); WBE32(iw, b.CSize); iw.Write(BinaryPrimitives.ReverseEndianness(b.Flags)); }
            WBE32(iw, (uint)nodes.Count);
            foreach (var n in nodes) { WBE64(iw, n.Offset); WBE64(iw, n.Size); WBE32(iw, n.Flags); iw.Write(Encoding.UTF8.GetBytes(n.Path)); iw.Write((byte)0); }
            var newInfo = ms.ToArray();
            byte[] stored;
            if (newInfo.AsSpan().SequenceEqual(info)) stored = rawInfo;
            else if ((Flags & 0x3f) is 2 or 3)
            {
                var c = new byte[LZ4Codec.MaximumOutputSize(newInfo.Length)];
                stored = c[..LZ4Codec.Encode(newInfo, 0, newInfo.Length, c, 0, c.Length, LZ4Level.L12_MAX)];
            }
            else if ((Flags & 0x3f) == 0) stored = newInfo;
            else throw new Exception($"block info compression {Flags & 0x3f}");

            var hs = new MemoryStream();
            var hw = new BinaryWriter(hs);
            hw.Write("UnityFS\0"u8); WBE32(hw, Format);
            hw.Write(Encoding.ASCII.GetBytes(Version + "\0" + Revision + "\0"));
            var sizeAt = hs.Position;
            WBE64(hw, 0); WBE32(hw, (uint)stored.Length); WBE32(hw, (uint)newInfo.Length); WBE32(hw, Flags);
            if (Format >= 7) while (hs.Length % 16 != 0) hw.Write((byte)0);
            hw.Write(stored);
            if ((Flags & PadAtStart) != 0) while (hs.Length % 16 != 0) hw.Write((byte)0);
            var head = hs.ToArray();
            var dataLen = w.Length - (1 << 20);
            BinaryPrimitives.WriteInt64BigEndian(head.AsSpan((int)sizeAt), head.Length + dataLen);
            // shift the data down to follow the header
            var buf = new byte[1 << 24];
            for (long off = 0; off < dataLen; off += buf.Length)
            {
                var n = (int)Math.Min(buf.Length, dataLen - off);
                w.Position = (1 << 20) + off; w.ReadExactly(buf, 0, n);
                w.Position = head.Length + off; w.Write(buf, 0, n);
            }
            w.SetLength(head.Length + dataLen);
            w.Position = 0; w.Write(head);
        }
        File.Move(tmp, outPath, true);
    }

    static byte[] Decompress(byte[] src, int usize, uint compression)
    {
        if (compression == 0) return usize == src.Length ? src : throw new Exception("stored block of the wrong size");
        if (compression is not (2 or 3)) throw new Exception($"compression type {compression} (only LZ4/LZ4HC)");
        var dst = new byte[usize];
        if (LZ4Codec.Decode(src, 0, src.Length, dst, 0, usize) != usize) throw new Exception("bad LZ4 block");
        return dst;
    }

    static long Align16(long x) => (x + 15) & ~15L;
    static string CStr(BinaryReader r) { var b = new List<byte>(); for (byte c; (c = r.ReadByte()) != 0;) b.Add(c); return Encoding.UTF8.GetString(b.ToArray()); }
    static uint BE32(BinaryReader r) => BinaryPrimitives.ReadUInt32BigEndian(r.ReadBytes(4));
    static long BE64(BinaryReader r) => BinaryPrimitives.ReadInt64BigEndian(r.ReadBytes(8));
    static void WBE32(BinaryWriter w, uint v) => w.Write(BinaryPrimitives.ReverseEndianness(v));
    static void WBE64(BinaryWriter w, long v) => w.Write(BinaryPrimitives.ReverseEndianness(v));

    public void Dispose() => fs.Dispose();
}
