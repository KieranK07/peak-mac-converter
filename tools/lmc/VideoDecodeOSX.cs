using System.Buffers.Binary;
using System.Security.Cryptography;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// Hidden/VideoDecodeOSX, Unity's macOS-only built-in video shader. Unity's Mac build of the game puts it in
// unity_builtin_extra at pathID 16002 (the game's ScriptMapper already points there; the Windows build strips it).
// Its two passes are GLSL-only (rectangle textures), so a Metal build writes them with no programs at all.
// Source: the same shader in the Unity Mac editor's own unity_builtin_extra (editor format, from Unity's
// official editor pkg). We drop the editor-only fields, fill in what a Metal build writes, check the result is
// byte-identical to Unity's, and add it to our unity_builtin_extra.
static class VideoDecodeOSX
{
    const long PathId = 16002;
    const int ShaderClass = 48;
    // m_EditorDataHash of each pass for Metal: two 128-bit hashes Unity's Mac build writes (LC Hybrid.app). Not
    // computable outside Unity; the player doesn't read them (the converter's other shaders keep D3D11's).
    static readonly string[] PassHash = { "ace44da396b4b694b4c6da374980c544", "836ec66b70d329a4a6809cb71d0da06c" };
    // sha256 of the result: object 16002 of Unity's own Mac build of the game (4772 bytes).
    const string OutSha256 = "f96a737d165807b3bba1c1e90ace25b4ffec50df7d84ddb62be738d96a55c00c";

    public static int Run(string editorExtra, string dataDir)
    {
        var obj = FromEditor(editorExtra);
        var path = Path.Combine(dataDir, "Resources", "unity_builtin_extra");
        var src = File.ReadAllBytes(path);
        var res = Insert(src, PathId, ShaderClass, obj);
        Check(src, res, PathId, obj);
        File.WriteAllBytes(path, res);
        Console.WriteLine($"unity_builtin_extra: added Hidden/VideoDecodeOSX ({obj.Length} bytes)");
        return 0;
    }

    static byte[] FromEditor(string editorExtra)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(DataDir.Tpk());
        var inst = am.LoadAssetsFile(editorExtra, false);
        am.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);
        var info = inst.file.GetAssetInfo(PathId) ?? throw new Exception($"{editorExtra}: no object {PathId}");
        var bf = am.GetBaseField(inst, info);
        if (info.TypeId != ShaderClass || bf["m_ParsedForm"]["m_Name"].AsString != "Hidden/VideoDecodeOSX")
            throw new Exception($"{editorExtra}: object {PathId} is not Hidden/VideoDecodeOSX");

        var player = new AssetTypeTemplateField();
        player.FromClassDatabase(am.ClassDatabase, am.ClassDatabase.FindAssetClassByID(ShaderClass));
        KeepOnly(bf, player);

        var passes = bf["m_ParsedForm"]["m_SubShaders"]["Array"].Children.SelectMany(ss => ss["m_Passes"]["Array"].Children).ToList();
        if (passes.Count != PassHash.Length) throw new Exception($"expected {PassHash.Length} passes, found {passes.Count}");
        for (var i = 0; i < passes.Count; i++)
        {
            var p = passes[i];
            if (p["m_ProgramMask"].AsUInt != 6) throw new Exception("expected vertex + fragment passes"); // 1<<1 | 1<<2
            var hash = ValueBuilder.DefaultValueFieldFromArrayTemplate(p["m_EditorDataHash"]["Array"]);
            var hb = Convert.FromHexString(PassHash[i]);
            for (var k = 0; k < 16; k++) hash.Children[k].AsByte = hb[k];
            Set(p["m_EditorDataHash"], hash);
            p["m_Platforms"]["Array"].AsByteArray = new byte[] { MetalShader.PlatformMetal };
            foreach (var prog in new[] { "progVertex", "progFragment" })
                foreach (var list in new[] { "m_PlayerSubPrograms", "m_ParameterBlobIndices" })
                {
                    var arr = p[prog][list]["Array"];
                    Set(p[prog][list], Enumerable.Range(0, 4).Select(_ => ValueBuilder.DefaultValueFieldFromArrayTemplate(arr)).ToArray());
                }
        }
        UInts(bf["platforms"], MetalShader.PlatformMetal);
        UInts(bf["stageCounts"], 2);
        foreach (var (field, v) in new[] { ("offsets", 0u), ("compressedLengths", 5u), ("decompressedLengths", 4u) })
        {
            var inner = ValueBuilder.DefaultValueFieldFromArrayTemplate(bf[field]["Array"]);
            UInts(inner, v);
            Set(bf[field], inner);
        }
        bf["compressedBlob"]["Array"].AsByteArray = new byte[] { 0x40, 0, 0, 0, 0 }; // LZ4 of an empty program list (uint 0)
        bf["m_ShaderIsBaked"].AsBool = true;

        var bytes = bf.WriteToByteArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != OutSha256)
            throw new Exception("Hidden/VideoDecodeOSX: result differs from Unity's Mac build (editor pkg changed?)");
        am.UnloadAll();
        return bytes;
    }

    // Drop the fields the player format doesn't have (EditorExtension, m_PackageRequirements, m_CompileInfo, ...).
    static void KeepOnly(AssetTypeValueField v, AssetTypeTemplateField t)
    {
        if (t.IsArray) { foreach (var c in v.Children) KeepOnly(c, t.Children[1]); return; }
        if (t.Children.Count == 0 || v.Value != null) return; // leaf or string
        v.Children = t.Children.Select(tc =>
        {
            var c = v.Children.FirstOrDefault(x => x.FieldName == tc.Name) ?? throw new Exception($"editor shader lacks {tc.Name}");
            KeepOnly(c, tc);
            return c;
        }).ToList();
    }

    static void Set(AssetTypeValueField vector, params AssetTypeValueField[] items)
    {
        vector["Array"].Children = items.ToList();
        vector["Array"].AsArray = new AssetTypeArrayInfo(items.Length);
    }

    static void UInts(AssetTypeValueField vector, params uint[] values) =>
        Set(vector, values.Select(x => { var f = ValueBuilder.DefaultValueFieldFromArrayTemplate(vector["Array"]); f.AsUInt = x; return f; }).ToArray());

    // Adds one object to a player serialized file (format 22, no type trees), the way Unity lays it out: object
    // table and data both in pathID order, data 8-byte aligned, data section 16-byte aligned after the metadata.
    static byte[] Insert(byte[] src, long pathId, int classId, byte[] obj)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(8)) != 22 || src[16] != 0) throw new Exception("not a little-endian format 22 file");
        var metaSize = (int)BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(20));
        var dataOff = BinaryPrimitives.ReadInt64BigEndian(src.AsSpan(32));
        if (dataOff != Align(48 + metaSize, 16)) throw new Exception("unexpected data offset");
        var o = Array.IndexOf(src, (byte)0, 48) + 1 + 4;
        if (src[o++] != 0) throw new Exception("file has type trees");
        int typeIndex = -1, types = BitConverter.ToInt32(src, o);
        o += 4;
        for (var i = 0; i < types; i++)
        {
            var cid = BitConverter.ToInt32(src, o);
            if (cid == classId) typeIndex = i;
            o += 7 + (cid == 114 ? 16 : 0) + 16;
        }
        if (typeIndex < 0) throw new Exception($"no class {classId} in the type list");
        var countPos = o;
        var n = BitConverter.ToInt32(src, countPos);
        var table = (int)Align(countPos + 4, 4);
        var rows = Enumerable.Range(0, n).Select(k => (Pid: BitConverter.ToInt64(src, table + 24 * k), Start: BitConverter.ToInt64(src, table + 24 * k + 8),
            Size: BitConverter.ToUInt32(src, table + 24 * k + 16), Type: BitConverter.ToInt32(src, table + 24 * k + 20))).ToList();
        if (rows.Any(r => r.Pid == pathId)) throw new Exception($"object {pathId} already present");
        if (rows[0].Start != 0 || !rows.SequenceEqual(rows.OrderBy(r => r.Pid)) || !rows.SequenceEqual(rows.OrderBy(r => r.Start)))
            throw new Exception("objects not in pathID order");
        var tail = src.AsSpan((int)(dataOff + rows[^1].Start + rows[^1].Size));
        rows.Add((pathId, -1, (uint)obj.Length, typeIndex));
        rows = rows.OrderBy(r => r.Pid).ToList();

        var meta = new MemoryStream();
        meta.Write(src, 0, table);
        long pos = 0;
        foreach (var r in rows)
        {
            meta.Write(BitConverter.GetBytes(r.Pid)); meta.Write(BitConverter.GetBytes(pos));
            meta.Write(BitConverter.GetBytes(r.Size)); meta.Write(BitConverter.GetBytes(r.Type));
            pos = Align(pos + r.Size, 8);
        }
        meta.Write(src, table + 24 * n, 48 + metaSize - (table + 24 * n));
        var newMeta = (int)meta.Length - 48;
        var res = new MemoryStream();
        res.Write(meta.GetBuffer(), 0, (int)meta.Length);
        res.SetLength(Align(res.Length, 16));
        var newDataOff = res.Length;
        foreach (var r in rows)
        {
            res.SetLength(Align(res.Length - newDataOff, 8) + newDataOff);
            res.Position = res.Length;
            res.Write(r.Pid == pathId ? obj : src.AsSpan((int)(dataOff + r.Start), (int)r.Size));
        }
        res.Write(tail);
        var b = res.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)newMeta);
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(24), b.Length);
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(32), newDataOff);
        BitConverter.TryWriteBytes(b.AsSpan(countPos), n + 1);
        return b;
    }

    // Self-check: AssetsTools reads the new file with every old object byte-identical and the new one in place.
    static void Check(byte[] src, byte[] res, long pathId, byte[] obj)
    {
        static Dictionary<long, byte[]> Objects(byte[] b)
        {
            var f = new AssetsFile();
            f.Read(new AssetsFileReader(new MemoryStream(b)));
            return f.AssetInfos.ToDictionary(i => i.PathId, i => b.AsSpan((int)i.GetAbsoluteByteOffset(f), (int)i.ByteSize).ToArray());
        }
        var a = Objects(src); var c = Objects(res);
        if (c.Count != a.Count + 1 || !c[pathId].AsSpan().SequenceEqual(obj) || a.Any(kv => !c[kv.Key].AsSpan().SequenceEqual(kv.Value)))
            throw new Exception("unity_builtin_extra: insert self-check failed");
    }

    static long Align(long x, int a) => (x + a - 1) & ~(long)(a - 1);
}
