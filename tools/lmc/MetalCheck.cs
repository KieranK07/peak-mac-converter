using System.Security.Cryptography;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// Proof for a metalized bundle against its source:
// - same nodes; non-serialized nodes byte-identical; every serialized file says StandaloneOSX (2), was Windows (19);
// - same objects (path ID, class) in every file; all bytes identical except Shaders, ComputeShaders, BuildSettings and
//   GraphicsSettings; the two settings objects differ only in m_GraphicsAPIs / m_ShaderDefinesPerShaderCompiler;
// - every Shader/ComputeShader parses to its exact size with the 6000.3 type tree, re-serializes byte for byte, and
//   holds only Metal programs in the docs/metal-format-6.3.md encoding.
// mslDir: also write each distinct MSL text (sha256 prefix name, as xlatall) for tools/xlat/mtlcheck.
static class MetalCheck
{
    public static int Run(string srcBundle, string outBundle, string? mslDir)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var a = new Bundle(srcBundle);
        using var b = new Bundle(outBundle);
        var bad = new List<string>();
        void Bad(string s) { if (bad.Count < 50) Console.WriteLine("  FAIL " + s); bad.Add(s); }
        var msl = new Dictionary<string, byte[]>();
        int same = 0, shaders = 0, computes = 0, programs = 0, kernels = 0, files = 0;
        // one manager per side: AssetsManager hands back an already loaded file of the same name
        var am = new AssetsManager(); var amA = new AssetsManager();
        am.LoadClassPackage(DataDir.Tpk()); amA.LoadClassPackage(DataDir.Tpk());
        if (a.Nodes.Count != b.Nodes.Count) { Console.WriteLine("node count differs"); return 1; }
        for (var i = 0; i < a.Nodes.Count; i++)
        {
            var (x, y) = (a.Nodes[i], b.Nodes[i]);
            if (x.Path != y.Path || x.Flags != y.Flags) { Bad($"node {i}: {x.Path} -> {y.Path}"); continue; }
            var (bx, by) = (a.Read(x), b.Read(y));
            if ((x.Flags & 4) == 0)
            {
                if (!SHA256.HashData(bx).AsSpan().SequenceEqual(SHA256.HashData(by))) Bad($"{x.Path}: bytes differ");
                continue;
            }
            files++;
            if (SerializedWriter.PlatformField(bx).platform != SerializedWriter.StandaloneWindows64 || SerializedWriter.PlatformField(by).platform != SerializedWriter.StandaloneOSX)
                Bad($"{x.Path}: target platform not 19 -> 2");
            var fa = amA.LoadAssetsFile(new MemoryStream(bx), x.Path, false);
            amA.LoadClassDatabaseFromPackage(fa.file.Metadata.UnityVersion);
            var fb = am.LoadAssetsFile(new MemoryStream(by), y.Path, false);
            am.LoadClassDatabaseFromPackage(fb.file.Metadata.UnityVersion);
            var ia = fa.file.AssetInfos; var ib = fb.file.AssetInfos;
            if (ia.Count != ib.Count) { Bad($"{x.Path}: {ia.Count} -> {ib.Count} objects"); continue; }
            for (var k = 0; k < ia.Count; k++)
            {
                var (oa, ob) = (ia[k], ib[k]);
                if (oa.PathId != ob.PathId || oa.TypeId != ob.TypeId) { Bad($"{x.Path}: object {k} {oa.PathId}/{oa.TypeId} -> {ob.PathId}/{ob.TypeId}"); continue; }
                var cls = (AssetClassID)ob.TypeId;
                var tag = $"{x.Path}:{ob.PathId} {cls}";
                try
                {
                    switch (cls)
                    {
                        case AssetClassID.Shader:
                        {
                            var v = Exact(am, fb, ob, tag, Bad);
                            if (v != null) programs += CheckShader(v, tag, Bad, msl);
                            shaders++;
                            break;
                        }
                        case AssetClassID.ComputeShader:
                        {
                            var v = Exact(am, fb, ob, tag, Bad);
                            if (v != null) kernels += CheckCompute(v, tag, Bad, msl);
                            computes++;
                            break;
                        }
                        case AssetClassID.BuildSettings:
                        case AssetClassID.GraphicsSettings:
                        {
                            var field = cls == AssetClassID.BuildSettings ? "m_GraphicsAPIs" : "m_ShaderDefinesPerShaderCompiler";
                            var (va, vb) = (amA.GetBaseField(fa, oa), Exact(am, fb, ob, tag, Bad));
                            if (vb == null) break;
                            foreach (var c in va.Children.Where(c => c.FieldName != field))
                                if (!Bytes(c).AsSpan().SequenceEqual(Bytes(vb[c.FieldName]))) Bad($"{tag}: {c.FieldName} changed");
                            var got = cls == AssetClassID.BuildSettings
                                ? string.Join(",", vb[field]["Array"].Children.Select(e => e.AsInt))
                                : string.Join(";", vb[field]["Array"].Children.Select(e => e["shaderPlatform"].AsInt + ":" + string.Join("|", Metalize.Tiers.Select(t => string.Join(",", e[t]["Array"].Children.Select(w => w.AsUInt))))));
                            var want = cls == AssetClassID.BuildSettings ? $"{Metalize.MetalApi}"
                                : $"{MetalShader.PlatformMetal}:" + string.Join("|", Metalize.Tiers.Select(_ => string.Join(",", Metalize.MetalDefines)));
                            if (got != want) Bad($"{tag}: {field} = {got}, want {want}");
                            break;
                        }
                        default:
                            if (DataDir.ObjectBytes(fa, oa).AsSpan().SequenceEqual(DataDir.ObjectBytes(fb, ob))) same++;
                            else Bad($"{tag}: bytes differ");
                            break;
                    }
                }
                catch (Exception e) { Bad($"{tag}: {e.Message}"); }
            }
            am.UnloadAll(); amA.UnloadAll();
        }
        if (mslDir != null)
        {
            Directory.CreateDirectory(mslDir);
            foreach (var (sha, text) in msl)
            {
                var path = Path.Combine(mslDir, sha + ".metal");
                if (!File.Exists(path)) File.WriteAllBytes(path, text);
            }
        }
        Console.WriteLine($"check: {files} serialized files (all 19 -> 2), {same} other objects byte-identical; {shaders} Shaders ({programs} Metal programs), {computes} ComputeShaders ({kernels} Metal kernels), {msl.Count} distinct MSL, {Values} cbuffer values and {Bindings} bindings match their MSL; {(bad.Count == 0 ? "PASS" : $"{bad.Count} FAIL")} ({sw.Elapsed.TotalSeconds:F0}s)");
        return bad.Count == 0 ? 0 : 1;
    }

    static byte[] Bytes(AssetTypeValueField f)
    {
        var ms = new MemoryStream();
        f.Write(new AssetsFileWriter(ms));
        return ms.ToArray();
    }

    // The object through the class database: must consume exactly its bytes and write them back unchanged.
    static AssetTypeValueField? Exact(AssetsManager am, AssetsFileInstance f, AssetFileInfo o, string tag, Action<string> bad)
    {
        var t = am.GetTemplateBaseField(f, o);
        var start = o.GetAbsoluteByteOffset(f.file);
        var v = t.MakeValue(f.file.Reader, start);
        var used = f.file.Reader.Position - start;
        if (used != o.ByteSize) { bad($"{tag}: parsed {used} of {o.ByteSize} bytes"); return null; }
        if (!v.WriteToByteArray().AsSpan().SequenceEqual(DataDir.ObjectBytes(f, o))) { bad($"{tag}: re-serialized bytes differ"); return null; }
        return v;
    }

    static IEnumerable<AssetTypeValueField> A(AssetTypeValueField f) => f["Array"].Children;
    static int Values, Bindings;
    static string Sha(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(SHA256.HashData(b))[..16];

    static int CheckShader(AssetTypeValueField bf, string tag, Action<string> bad, Dictionary<string, byte[]> msl)
    {
        foreach (var f in new[] { "platforms", "offsets", "compressedLengths", "decompressedLengths", "stageCounts" })
            if (A(bf[f]).Count() != 1) bad($"{tag}: {f} has {A(bf[f]).Count()} entries");
        if (A(bf["platforms"]).First().AsInt != MetalShader.PlatformMetal) bad($"{tag}: platform {A(bf["platforms"]).First().AsInt}");
        var blob = new ShaderBlob(bf, 0);
        var n = 0;
        var checkedSubs = new HashSet<long>();
        foreach (var p in A(bf["m_ParsedForm"]["m_SubShaders"]).SelectMany(ss => A(ss["m_Passes"])))
            foreach (var prog in MetalShader.Progs)
            {
                var subs = A(p[prog]["m_PlayerSubPrograms"]).ToList();
                var pars = A(p[prog]["m_ParameterBlobIndices"]).ToList();
                if (subs.Count != pars.Count) bad($"{tag} {prog}: {subs.Count} program lists, {pars.Count} parameter lists");
                foreach (var c in p[prog]["m_CommonParameters"].Children)
                    if (A(c).Any()) bad($"{tag} {prog}: common {c.FieldName} not empty");
                for (var o = 0; o < subs.Count; o++)
                {
                    var sl = A(subs[o]).ToList(); var pl = A(pars[o]).ToList();
                    if (sl.Count != pl.Count) { bad($"{tag} {prog}[{o}]: {sl.Count} programs, {pl.Count} parameter indices"); continue; }
                    for (var i = 0; i < sl.Count; i++)
                    {
                        n++;
                        var type = (int)sl[i]["m_GpuProgramType"].AsSByte;
                        var bi = sl[i]["m_BlobIndex"].AsUInt; var pi = pl[i].AsUInt;
                        if (type is not (23 or 24)) { bad($"{tag} {prog}: program type {type}"); continue; }
                        if (bi >= blob.Entries.Count || pi >= blob.Entries.Count) { bad($"{tag} {prog}: entry {bi}/{pi} of {blob.Entries.Count}"); continue; }
                        var par = blob.Entry((int)pi);
                        var pp = Params.FromBlob(par);  // throws on a bad layout or trailing bytes
                        if (BitConverter.ToInt32(par, 0) != 202012090) bad($"{tag}: parameter entry version");
                        if (!checkedSubs.Add(bi * 65536 + pi)) continue;
                        var sp = SubProgram.Read(blob.Entry((int)bi));
                        var why = CheckProgram(sp, type, msl) ?? Consistent(Encoding.UTF8.GetString(sp.Code, 0x3c, sp.Code.Length - 0x3c), pp);
                        if (why != null) bad($"{tag} {prog} entry {bi}/{pi}: {why}");
                    }
                }
            }
        return n;
    }

    // The parameter entry describes the MSL: every cbuffer value sits at its offset in the MSL struct (Metal layout
    // rules), and every cbuffer, texture, sampler and buffer binding is the slot the MSL declares.
    static string? Consistent(string msl, Params p)
    {
        var structs = new Dictionary<string, (Dictionary<string, int> off, int size, int align)>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(msl, @"struct (\w+)\s*\{([^}]*)\};"))
        {
            var offs = new Dictionary<string, int>();
            int off = 0, maxA = 4;
            foreach (System.Text.RegularExpressions.Match f in System.Text.RegularExpressions.Regex.Matches(m.Groups[2].Value, @"^\s*(\w+) (\w+)(?:\[(\d+)\])?;", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                var t = f.Groups[1].Value;
                var n = f.Groups[3].Success ? int.Parse(f.Groups[3].Value) : 1;
                int size, al;
                if (structs.TryGetValue(t, out var st)) (size, al) = (st.size, st.align);
                else
                {
                    var comps = char.IsDigit(t[^1]) ? t[^1] - '0' : 1;
                    var bytes = t.StartsWith("half") || t.StartsWith("short") || t.StartsWith("ushort") ? 2 : 4;
                    (size, al) = comps switch { 1 => (bytes, bytes), 2 => (2 * bytes, 2 * bytes), _ => (4 * bytes, 4 * bytes) };
                }
                off = (off + al - 1) / al * al;
                maxA = Math.Max(maxA, al);
                offs[f.Groups[2].Value] = off;
                off += ((size + al - 1) / al * al) * (n - 1) + size;
            }
            structs[m.Groups[1].Value] = (offs, (off + maxA - 1) / maxA * maxA, maxA);
        }
        int? Slot(string name, string kind)
        {
            var m = System.Text.RegularExpressions.Regex.Match(msl, $@"\b{System.Text.RegularExpressions.Regex.Escape(name)} \[\[ {kind}\((\d+)\)");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
        foreach (var cb in p.Cbs.Where(c => c.Name != ""))
        {
            if (!structs.TryGetValue(cb.Name + "_Type", out var st)) return $"cbuffer {cb.Name} has no MSL struct";
            foreach (var v in cb.Params)
                if (!(st.off.TryGetValue(v.Name, out var o) || st.off.TryGetValue((v.Matrix ? $"hlslcc_mtx{v.Rows}x{v.Cols}" : "") + v.Name, out o) || st.off.Keys.Where(k => k.StartsWith("hlslcc_mtx") && k.EndsWith(v.Name)).Select(k => st.off[k]).Cast<int?>().FirstOrDefault() is int oo && (o = oo) >= 0))
                    return $"{cb.Name}.{v.Name} not in the MSL struct";
                else if (o != v.Index) return $"{cb.Name}.{v.Name} at {v.Index}, MSL offset {o}";
                else Interlocked.Increment(ref Values);
            foreach (var sv in cb.Structs)
                if (!st.off.TryGetValue(sv.Name, out var o) || o != sv.Index) return $"{cb.Name}.{sv.Name}[] at {sv.Index}, MSL {(st.off.ContainsKey(sv.Name) ? st.off[sv.Name] : -1)}";
        }
        Interlocked.Add(ref Bindings, p.CbBinds.Count + p.Textures.Count + p.Buffers.Count);
        foreach (var b in p.CbBinds) if (Slot(b.Name, "buffer") != b.Index) return $"cbuffer {b.Name} bound at {b.Index}, MSL buffer({Slot(b.Name, "buffer")})";
        foreach (var t in p.Textures)
        {
            if (Slot(t.Name, "texture") != t.Index) return $"texture {t.Name} at {t.Index}, MSL texture({Slot(t.Name, "texture")})";
            if (t.Sampler >= 0 && !msl.Contains($" [[ sampler ({t.Sampler}) ]]"))
                return $"texture {t.Name}: no sampler({t.Sampler}) in the MSL";
        }
        foreach (var b in p.Buffers) if (Slot(b.Name, "buffer") != b.Index) return $"buffer {b.Name} at {b.Index}, MSL buffer({Slot(b.Name, "buffer")})";
        return null;
    }

    // One Metal subprogram entry, docs/metal-format-6.3.md sections 1-2.
    static string? CheckProgram(SubProgram sp, int type, Dictionary<string, byte[]> msl)
    {
        if (sp.Version != 202012090 || sp.Type != type || sp.Stats.Any(s => s != 0)) return "entry version/type/stats";
        var c = sp.Code;
        uint W(int i) => BitConverter.ToUInt32(c, 4 * i);
        if (c.Length < 0x3d || W(0) != 0xf00dcafe || W(1) != 0x30 || W(2) != MetalProgram.Format || W(3) != 0x3c) return "header magic/offsets/format";
        if (Enumerable.Range(4, 5).Any(i => W(i) != 0) || W(11) != 0) return "tessellation words or 0x2c not 0";
        if (W(9) is not (0x25 or 0x27) || (W(9) == 0x27 && type != 23)) return $"flags 0x{W(9):x}";
        if (!c.AsSpan(0x30, 12).SequenceEqual("xlatMtlMain\0"u8)) return "entry name";
        var src = c.AsSpan(0x3c);
        var text = Encoding.UTF8.GetString(src);
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, $@"(^|\n){(type == 23 ? "vertex" : "fragment")} \w+ xlatMtlMain\(")) return "no entry point";
        if (type == 23)
        {
            if (sp.Channels.Any(ch => ch.Target < 13)) return "channel target below 13";
            if (sp.SourceMap != sp.Channels.Aggregate(0, (m, ch) => m | 1 << ch.Source)) return $"sourceMap 0x{sp.SourceMap:x} != channel sources";
        }
        else if (sp.Channels.Count != 0) return "fragment program with channels";
        if (text.Contains("_UnityFBInput") && !text.Contains("function_constant(has_framebuffer_fetch)")) return "framebuffer input read only as a texture";
        msl.TryAdd(Sha(src), src.ToArray());
        return null;
    }

    static int CheckCompute(AssetTypeValueField bf, string tag, Action<string> bad, Dictionary<string, byte[]> msl)
    {
        var variants = A(bf["variants"]).ToList();
        if (variants.Count != 1 || variants[0]["targetRenderer"].AsInt != 16) { bad($"{tag}: variants {string.Join(",", variants.Select(v => v["targetRenderer"].AsInt))}"); return 0; }
        var v0 = variants[0];
        var ncb = A(v0["constantBuffers"]).Count();
        var n = 0;
        foreach (var k in A(v0["kernels"]))
        {
            var uvs = A(k["uniqueVariants"]).ToList();
            var kt = $"{tag} {k["name"].AsString}";
            foreach (var e in A(k["variantIndices"]))
                if (e["second"].AsUInt >= uvs.Count) bad($"{kt}: keywords '{e["first"].AsString}' -> variant {e["second"].AsUInt} of {uvs.Count}");
            foreach (var uv in uvs)
            {
                n++;
                var c = uv["code"]["Array"].AsByteArray;
                var tg = A(uv["threadGroupSize"]).Select(x => x.AsUInt).ToList();
                if (c.Length < 21 || BitConverter.ToUInt32(c, 0) != 0xdeadcafe || BitConverter.ToUInt32(c, 4) != 2 || BitConverter.ToUInt32(c, 8) != 0x14) { bad($"{kt}: compute header"); continue; }
                if (Enumerable.Range(0, 3).Any(i => BitConverter.ToUInt16(c, 12 + 2 * i) != tg[i]) || BitConverter.ToUInt16(c, 18) != 1) bad($"{kt}: thread group / flags");
                if (c[^1] != 0) bad($"{kt}: code does not end with NUL");
                var src = c.AsSpan(20, c.Length - 21);
                if (!Encoding.UTF8.GetString(src).Contains("kernel void computeMain(")) bad($"{kt}: no computeMain");
                foreach (var list in new[] { "cbs", "textures", "inBuffers", "outBuffers" })
                    foreach (var r in A(uv[list]))
                        if (r["resType"].AsInt != 0) bad($"{kt}: {list} {r["name"].AsString} resType {r["resType"].AsInt}");
                foreach (var ci in A(uv["cbVariantIndices"]))
                    if (ci.AsUInt >= ncb) bad($"{kt}: cb index {ci.AsUInt} of {ncb}");
                if (A(uv["builtinSamplers"]).Any()) bad($"{kt}: builtin samplers left");
                msl.TryAdd(Sha(src), src.ToArray());
            }
        }
        return n;
    }
}
