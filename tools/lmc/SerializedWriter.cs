using System.Buffers.Binary;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// Rewrites a Unity serialized file (format 22) with some objects replaced: header and metadata are copied
// byte for byte except the target platform, the file size and each object's (offset, size); untouched
// objects are copied byte for byte in their original order with the file's object alignment (8 or 16 bytes).
static class SerializedWriter
{
    public const uint StandaloneOSX = 2, StandaloneWindows64 = 19;

    // Rewrites a v22 serialized file: header + metadata copied byte for byte except target platform, file size and the
    // (offset, size) of replaced/shifted objects; object data copied byte for byte except the replaced objects.
    public static byte[] WriteSerialized(byte[] src, AssetsFileInstance inst, Dictionary<long, byte[]> repl, uint platform)
    {
        var f = inst.file;
        if (f.Header.Version != 22) throw new Exception($"{inst.name}: serialized format {f.Header.Version}, writer only knows 22");
        if (src[16] != 0) throw new Exception($"{inst.name}: big-endian file");
        var fileSize = BinaryPrimitives.ReadInt64BigEndian(src.AsSpan(24));
        var dataOff = BinaryPrimitives.ReadInt64BigEndian(src.AsSpan(32));
        if (fileSize != src.Length || dataOff != f.Header.DataOffset) throw new Exception($"{inst.name}: header mismatch");
        var verEnd = Array.IndexOf(src, (byte)0, 48);
        if (Encoding.ASCII.GetString(src, 48, verEnd - 48) != f.Metadata.UnityVersion) throw new Exception($"{inst.name}: version string not where expected");
        var platOff = verEnd + 1;
        if (BitConverter.ToUInt32(src, platOff) != f.Metadata.TargetPlatform) throw new Exception($"{inst.name}: platform field not where expected");

        var infos = f.Metadata.AssetInfos;
        var pat = new byte[20];
        BitConverter.TryWriteBytes(pat.AsSpan(0), infos[0].PathId);
        BitConverter.TryWriteBytes(pat.AsSpan(8), infos[0].ByteOffset);
        BitConverter.TryWriteBytes(pat.AsSpan(16), infos[0].ByteSize);
        var meta = src.AsSpan(0, (int)dataOff);
        var tbl = meta.IndexOf(pat);
        if (tbl < 0 || meta[(tbl + 1)..].IndexOf(pat) >= 0) throw new Exception($"{inst.name}: cannot locate object table uniquely");
        for (var k = 0; k < infos.Count; k++)
        {
            var e = tbl + 24 * k;
            if (BitConverter.ToInt64(src, e) != infos[k].PathId || BitConverter.ToInt64(src, e + 8) != infos[k].ByteOffset || BitConverter.ToUInt32(src, e + 16) != infos[k].ByteSize)
                throw new Exception($"{inst.name}: object table entry {k} does not match");
        }

        var order = Enumerable.Range(0, infos.Count).OrderBy(k => infos[k].ByteOffset).ToList();
        // Unity 2022.3 starts each object on an 8-byte boundary, 6000.3 on a 16-byte one: find which this file uses.
        // Bytes between an object's aligned end and the next object (6000.3's unity_builtin_extra has one 522 KB run
        // of stale data) are kept, verbatim, in front of that next object.
        long align = new long[] { 16, 8 }.FirstOrDefault(al => order.All(k => infos[k].ByteOffset % al == 0));
        if (align == 0) throw new Exception($"{inst.name}: objects not 8- or 16-byte aligned");
        var gap = new long[infos.Count];
        for (var i = 1; i < order.Count; i++)
        {
            var a = infos[order[i - 1]];
            gap[order[i]] = infos[order[i]].ByteOffset - Align(a.ByteOffset + a.ByteSize, align);
            if (gap[order[i]] < 0) throw new Exception($"{inst.name}: objects overlap");
        }
        var last = infos[order[^1]];
        var tail = src.Length - (dataOff + last.ByteOffset + last.ByteSize);

        var outMs = new MemoryStream(src.Length + repl.Values.Sum(v => v.Length));
        var head = src.AsSpan(0, (int)dataOff).ToArray();
        BitConverter.TryWriteBytes(head.AsSpan(platOff), platform);
        var pos = infos[order[0]].ByteOffset;
        var newStart = new long[infos.Count]; var newSize = new uint[infos.Count];
        foreach (var k in order)
        {
            pos += gap[k];
            newStart[k] = pos;
            newSize[k] = repl.TryGetValue(infos[k].PathId, out var nb) ? (uint)nb.Length : infos[k].ByteSize;
            pos = Align(pos + newSize[k], align);
        }
        for (var k = 0; k < infos.Count; k++)
        {
            BitConverter.TryWriteBytes(head.AsSpan(tbl + 24 * k + 8), newStart[k]);
            BitConverter.TryWriteBytes(head.AsSpan(tbl + 24 * k + 16), newSize[k]);
        }
        outMs.Write(head);
        foreach (var k in order)
        {
            while (outMs.Length < dataOff + newStart[k] - gap[k]) outMs.WriteByte(0);
            outMs.Write(src, (int)(dataOff + infos[k].ByteOffset - gap[k]), (int)gap[k]);
            if (repl.TryGetValue(infos[k].PathId, out var nb)) outMs.Write(nb);
            else outMs.Write(src, (int)(dataOff + infos[k].ByteOffset), (int)infos[k].ByteSize);
        }
        outMs.Write(src, src.Length - (int)tail, (int)tail);
        var res = outMs.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(res.AsSpan(24), res.Length);
        if (BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(4)) == src.Length) BinaryPrimitives.WriteUInt32BigEndian(res.AsSpan(4), (uint)res.Length);

        if (repl.Count == 0) // self-check: only the 4 platform bytes may differ
        {
            if (res.Length != src.Length) throw new Exception($"{inst.name}: unchanged rewrite changed size");
            for (var i = 0; i < res.Length; i++)
                if (res[i] != src[i] && (i < platOff || i >= platOff + 4)) throw new Exception($"{inst.name}: unchanged rewrite differs at {i}");
        }
        return res;
    }

    static long Align(long x, long a) => (x + a - 1) / a * a;

    // Where a format-22 serialized file keeps its target platform (u32 after the Unity version string), from its first
    // bytes; checks the header shape and returns the current value.
    public static (int at, uint platform) PlatformField(ReadOnlySpan<byte> head)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(head[8..]) != 22 || head[16] != 0) throw new Exception("not a little-endian format-22 serialized file");
        var end = head[48..].IndexOf((byte)0);
        if (end < 0) throw new Exception("no version string");
        var at = 48 + end + 1;
        return (at, BitConverter.ToUInt32(head[at..]));
    }
}
