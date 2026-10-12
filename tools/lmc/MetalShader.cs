using System.Security.Cryptography;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using K4os.Compression.LZ4;

namespace Lmc;

// Rewrites a D3D11 (+ Vulkan) Shader object into a Metal one: every D3D11 player subprogram is translated with HLSLcc
// and re-encoded in Unity's Metal blob format (6000.3: docs/metal-format-6.3.md); the other platforms' programs and
// blobs are dropped. Structure, keywords and requirements are untouched; only the platform, the blob and each
// subprogram's program type, blob index and parameter index change.
static class MetalShader
{
    public const int PlatformD3D11 = 4, PlatformMetal = 14;
    internal const uint HlslccFlags = 0x20080; // HLSLCC_FLAG_TRANSLATE_MATRICES | HLSLCC_FLAG_INOUT_SEMANTIC_NAMES
    internal static readonly string[] Progs = { "progVertex", "progFragment", "progGeometry", "progHull", "progDomain", "progRayTracing" };
    const int MaxSegment = 8 << 20; // Unity splits a platform blob into LZ4 segments of at most 8 MiB

    public static byte[] Convert(DataDir d, ShaderRef s, List<string> problems)
    {
        var bf = d.Base(s.File, s.PathId);
        var plan = new ShaderPlan(bf, s.Name);
        var xl = plan.Translate();
        var results = new MetalProgram[plan.Jobs.Count];
        for (var j = 0; j < xl.Length; j++)
        {
            var (ok, msl, refl) = xl[j];
            if (!ok) throw new Exception($"{plan.Jobs[j].Tag}: {refl.Split('\n').FirstOrDefault(l => l.StartsWith("error"))}");
            msl = Mark.Apply(s.Name, plan.Jobs[j], msl);
            Dump.Msl(plan.Jobs[j].Tag, msl);
            results[j] = MetalProgram.From(plan.Jobs[j].Subs[0], msl, refl, tess: plan.Jobs[j].Subs.Length > 1);
        }
        var entries = new List<byte[]>();
        var dedup = new Dictionary<string, int>();
        int Add(byte[] e)
        {
            var h = System.Convert.ToHexString(SHA1.HashData(e));
            if (!dedup.TryGetValue(h, out var i)) { i = entries.Count; entries.Add(e); dedup[h] = i; }
            return i;
        }
        var placed = plan.Jobs.Select((_, j) => (sub: Add(results[j].SubEntry), par: Add(results[j].ParamEntry))).ToList();
        foreach (var (sp, par, job) in plan.Sites)
        {
            sp["m_GpuProgramType"].AsSByte = (sbyte)results[job].Type;
            sp["m_BlobIndex"].AsUInt = (uint)placed[job].sub;
            par.AsUInt = (uint)placed[job].par;
        }
        DropOtherPlatforms(plan.Passes);
        // Metal parameter entries are complete per subprogram, so nothing is shared any more.
        foreach (var c in plan.Commons)
            foreach (var list in c.Children) SetArray(list, Array.Empty<AssetTypeValueField>());
        OnePlatform(bf, plan.PlatformIndex);
        if (plan.TessLists.Count > 0)
        {
            // Unity's Metal build: hull and domain lists have no tiers at all; stageCounts counts the stage types left
            foreach (var list in plan.TessLists) SetArray(list, Array.Empty<AssetTypeValueField>());
            bf["stageCounts"]["Array"][0].AsUInt = (uint)Progs.Count(x => plan.Passes.Any(p => HasPrograms(p[x])));
        }
        WriteBlob(bf, entries);
        return bf.WriteToByteArray();
    }

    // Removes every player subprogram that is not Metal (after translation: the Vulkan SPIR-V ones) with its
    // parameter index, keeping the lists' order.
    internal static void DropOtherPlatforms(IEnumerable<AssetTypeValueField> passes)
    {
        foreach (var p in passes)
            foreach (var prog in Progs)
            {
                var subs = p[prog]["m_PlayerSubPrograms"]["Array"].Children;
                var pars = p[prog]["m_ParameterBlobIndices"]["Array"].Children;
                for (var o = 0; o < subs.Count; o++)
                {
                    var keep = subs[o]["Array"].Children.Select((sp, i) => (sp, i)).Where(t => t.sp["m_GpuProgramType"].AsSByte is 23 or 24).Select(t => t.i).ToList();
                    var pl = pars[o]["Array"].Children;
                    SetArray(subs[o], keep.Select(i => subs[o]["Array"].Children[i]).ToList());
                    SetArray(pars[o], keep.Select(i => pl[i]).ToList());
                }
            }
    }

    // platforms / offsets / compressedLengths / decompressedLengths / stageCounts keep only the entry at `index`
    // (the D3D11 one), and the platform becomes Metal. WriteBlob then replaces the blob.
    internal static void OnePlatform(AssetTypeValueField bf, int index)
    {
        foreach (var f in new[] { "platforms", "offsets", "compressedLengths", "decompressedLengths", "stageCounts" })
        {
            var items = bf[f]["Array"].Children;
            if (items.Count <= index) throw new Exception($"{f}: {items.Count} entries, need index {index}");
            SetArray(bf[f], new[] { items[index] });
        }
        bf["platforms"]["Array"][0].AsInt = PlatformMetal;
    }

    internal static readonly string[] TessProgs = { "progVertex", "progHull", "progDomain" };
    internal static bool HasPrograms(AssetTypeValueField prog) => prog["m_PlayerSubPrograms"]["Array"].Children.Any(t => t["Array"].Children.Count > 0);
    internal static string Keywords(AssetTypeValueField sp) => string.Join(",", sp["m_KeywordIndices"]["Array"].Children.Select(k => k.AsUShort));

    // Segment 0 holds the entry table; entries follow in order, a new segment starting when 8 MiB would be crossed.
    internal static void WriteBlob(AssetTypeValueField bf, List<byte[]> entries)
    {
        var segments = new List<MemoryStream> { new() };
        var table = new List<(int off, int len, int seg)>();
        var tableSize = 4 + 12 * entries.Count;
        long pos = tableSize;
        foreach (var e in entries)
        {
            if (pos + e.Length > MaxSegment && pos > (segments.Count == 1 ? tableSize : 0)) { segments.Add(new MemoryStream()); pos = 0; }
            table.Add(((int)pos, e.Length, segments.Count - 1));
            pos += e.Length;
            segments[^1].Write(e);
        }
        var seg0 = new MemoryStream();
        var w = new BinaryWriter(seg0);
        w.Write(entries.Count);
        foreach (var (off, len, seg) in table) { w.Write(off); w.Write(len); w.Write(seg); }
        segments[0].Position = 0;
        segments[0].CopyTo(seg0);
        segments[0] = seg0;

        var compressed = new MemoryStream();
        var offs = new List<uint>(); var clens = new List<uint>(); var dlens = new List<uint>();
        foreach (var seg in segments.Select(m => m.ToArray()))
        {
            var buf = new byte[LZ4Codec.MaximumOutputSize(seg.Length)];
            var n = LZ4Codec.Encode(seg, 0, seg.Length, buf, 0, buf.Length, LZ4Level.L12_MAX);
            offs.Add((uint)compressed.Length); clens.Add((uint)n); dlens.Add((uint)seg.Length);
            compressed.Write(buf, 0, n);
        }
        SetUInts(bf["offsets"]["Array"][0], offs);
        SetUInts(bf["compressedLengths"]["Array"][0], clens);
        SetUInts(bf["decompressedLengths"]["Array"][0], dlens);
        bf["compressedBlob"]["Array"].AsByteArray = compressed.ToArray();
    }

    static void SetUInts(AssetTypeValueField vector, List<uint> values)
    {
        var arr = vector["Array"];
        SetArray(vector, values.Select(v =>
        {
            var f = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            f.AsUInt = v;
            return f;
        }).ToList());
    }

    internal static void SetArray(AssetTypeValueField vector, IList<AssetTypeValueField> items)
    {
        var arr = vector["Array"];
        arr.Children = items.ToList();
        arr.AsArray = new AssetTypeArrayInfo(items.Count);
    }
}

// One translated subprogram in Unity's Metal encoding.
sealed class MetalProgram
{
    public int Type; public byte[] SubEntry = Array.Empty<byte>(), ParamEntry = Array.Empty<byte>();
    const int BlobVersion = 202012090;
    public const uint Format = 7;  // graphics program header word 2 (Unity 6000.3)

    // tess: msl/refl are a whole tessellated program (vertex + hull + domain stages), d3d its vertex stage.
    public static MetalProgram From(SubProgram d3d, string msl, string refl, bool tess = false)
    {
        var lines = refl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();
        var type = d3d.Type switch
        {
            15 or 16 => 23, // DX11 vertex SM4/SM5 -> Metal vertex
            17 or 18 => 24, // DX11 pixel -> Metal fragment
            _ => throw new Exception($"no Metal equivalent for D3D program type {d3d.Type}"),
        };
        if (tess && type != 23) throw new Exception($"tessellated program from D3D program type {d3d.Type}");
        return new MetalProgram { Type = type, SubEntry = Sub(d3d, type, msl, lines, tess), ParamEntry = MetalParams.Build(lines) };
    }

    // 48-byte header, entry point name, then the source. Header fields as Unity writes them:
    // magic, name offset, format (6 in 2022.3, 7 in 6000.3: format 7 makes the 6.3 player build the MTLLibrary at load
    // instead of at first draw, docs/metal-format-6.3.md section 1), source offset, 5 tessellation values (else 0: MTLTessellationPartitionMode,
    // MTLWinding, max factor, patches per threadgroup, the patch kernel's buffer count), flags (37, +2 when the
    // vertex stage writes the render target array index), colour write mask (4 bits per output; ~0 except
    // unwritten components), 0.
    static byte[] Code(string msl, List<string[]> refl, int type)
    {
        const string entry = "xlatMtlMain";
        uint flags = 37, mask = 0xffffffff;
        var tess = new uint[5];
        foreach (var l in refl)
        {
            if (l[0] == "tess") for (var i = 0; i < 4; i++) tess[i] = uint.Parse(l[1 + i]);
            if (l[0] == "tesskernel") tess[4] = uint.Parse(l[1]);
            if (l[0] == "builtin" && int.Parse(l[1]) == 4 /* NAME_RENDER_TARGET_ARRAY_INDEX */ && type == 23) flags |= 2;
            if (l[0] == "fout")
            {
                int comps = int.Parse(l[1]), idx = int.Parse(l[2]);
                for (var c = comps; c < 4; c++) mask &= ~(1u << (4 * idx + c));
            }
        }
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var src = 48 + entry.Length + 1;
        foreach (var v in new uint[] { 0xf00dcafe, 48, Format, (uint)src }.Concat(tess).Concat(new[] { flags, mask, 0u })) w.Write(v);
        w.Write(Encoding.ASCII.GetBytes(entry)); w.Write((byte)0);
        w.Write(Encoding.UTF8.GetBytes(msl));
        return ms.ToArray();
    }

    // D3D vertex channel targets name a semantic; Metal targets are attribute slots (13 + attribute index).
    static readonly Dictionary<int, string> D3DTargetSemantic = new()
    {
        [0] = "POSITION0", [1] = "NORMAL0", [2] = "TANGENT0", [3] = "COLOR0",
        [5] = "TEXCOORD0", [6] = "TEXCOORD1", [7] = "TEXCOORD2", [8] = "TEXCOORD3",
        [9] = "TEXCOORD4", [10] = "TEXCOORD5", [11] = "TEXCOORD6", [12] = "TEXCOORD7",
        [29] = "BLENDWEIGHTS0", [30] = "BLENDINDICES0",  // after the 16 attribute targets (13-28); URP 2D SKINNED_SPRITE
    };
    const int MetalAttrib0 = 13;

    // Unity's vertex channel for a semantic: 0 position, 1 normal, 2 tangent, 3 colour, 4 + n texcoord n.
    static int? Channel(string semantic) => semantic switch
    {
        "POSITION0" => 0, "NORMAL0" => 1, "TANGENT0" => 2, "COLOR0" => 3,
        _ when semantic.StartsWith("TEXCOORD") && int.TryParse(semantic[8..], out var n) && n < 8 => 4 + n,
        _ => null,
    };

    static byte[] Sub(SubProgram d3d, int type, string msl, List<string[]> refl, bool tess)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void Align() { while (ms.Length % 4 != 0) w.Write((byte)0); }
        w.Write(BlobVersion); w.Write(type);
        for (var i = 0; i < 4; i++) w.Write(0); // Unity reports no stats for Metal
        w.Write(d3d.Keywords.Count);
        foreach (var k in d3d.Keywords) { var b = Encoding.UTF8.GetBytes(k); w.Write(b.Length); w.Write(b); Align(); }
        var code = Code(msl, refl, type);
        w.Write(code.Length); w.Write(code); Align();

        var bySemantic = d3d.Channels.ToDictionary(c => D3DTargetSemantic.TryGetValue(c.Target, out var sem) ? sem : throw new Exception($"D3D channel target {c.Target}"), c => c.Source);
        var channels = new List<(int, int)>();
        var sourceMap = d3d.SourceMap;
        if (!tess)
        {
            foreach (var l in refl.Where(l => l[0] == "input"))
                channels.Add((bySemantic.TryGetValue(l[1], out var src) ? src : throw new Exception($"vertex input {l[1]} has no D3D channel"), MetalAttrib0 + int.Parse(l[2])));
            // Unity 6.3: a Metal vertex program's map is exactly the sources of its channels (D3D's can have more bits)
            if (type == 23) sourceMap = channels.Aggregate(0, (m, c) => m | 1 << c.Item1);
        }
        else
            // Unity lists every stage_in attribute of the combined program whose semantic is a vertex channel, the hull's
            // control points and patch constants included, by attribute; INTERNALTESSPOS, CUSTOM_INSTANCE_ID etc. are not
            foreach (var l in refl.Where(l => l[0] == "input").DistinctBy(l => int.Parse(l[2])).OrderBy(l => int.Parse(l[2])))
                if ((bySemantic.TryGetValue(l[1], out var src) ? src : Channel(l[1])) is int ch)
                {
                    channels.Add((ch, MetalAttrib0 + int.Parse(l[2])));
                    sourceMap |= 1 << ch;
                }
        w.Write(sourceMap);
        w.Write(channels.Count);
        foreach (var (src, dst) in channels) { w.Write(src); w.Write(dst); }
        return ms.ToArray();
    }
}

// Dev diagnostic, LMC_MARK="<shader name>[#<pass name>]=[+]r,g,b;...": every fragment program of those shaders (passes)
// writes that constant colour to SV_Target0 instead of its result (with "+": adds it), to see on screen whether, and
// where, it draws.
// Metalize skips the object cache while it is set.
static class Mark
{
    static readonly (string shader, string? pass, string color)[] Marks = (Environment.GetEnvironmentVariable("LMC_MARK") ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(m =>
        {
            var (target, color) = (m[..m.LastIndexOf('=')], m[(m.LastIndexOf('=') + 1)..]);
            var hash = target.IndexOf('#');
            return hash < 0 ? (target, (string?)null, color) : (target[..hash], target[(hash + 1)..], color);
        }).ToArray();
    public static bool Any => Marks.Length > 0;
    public static int Applied;

    public static string Apply(string shader, ShaderPlan.Job job, string msl)
    {
        var m = Marks.FirstOrDefault(x => x.shader == shader && (x.pass == null || x.pass == job.Pass) && job.Stages.Contains("progFragment"));
        if (m.shader == null || !msl.Contains(" SV_Target0 [[ color(")) return msl;
        Interlocked.Increment(ref Applied);
        // "+r,g,b" adds the colour to the result instead (alpha untouched), so a pass's input stays visible
        var set = m.color.StartsWith('+') ? $"output.SV_Target0 = output.SV_Target0 + float4({m.color[1..]}, 0.0);" : $"output.SV_Target0 = float4({m.color}, 1.0);";
        return msl.Replace("    return output;", $"    {set}\n    return output;");
    }
}

// LMC_DUMP_MSL=<dir>: also write every translated program, so it can be compile-checked (tools/xlat/mtlcheck).
static class Dump
{
    static readonly string? Dir = Environment.GetEnvironmentVariable("LMC_DUMP_MSL");
    public static void Msl(string tag, string msl)
    {
        if (Dir == null) return;
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, string.Concat(tag.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_')) + ".metal"), msl);
    }
}
