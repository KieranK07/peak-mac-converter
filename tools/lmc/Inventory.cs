using System.Security.Cryptography;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// Dev: every shader program of a build, as data.
//   programs.tsv  one row per player subprogram reference: shader, pass, stage, list, index, GPU program type,
//                 platform, blob/parameter entry, keywords, DOTS flag, entry sha256 (first 16 hex)
//   compute.tsv   one row per compute kernel variant: shader, renderer, kernel, variant, keywords, bytecode kind
//   blobs/        with "blobs": each distinct blob entry once, named by its sha (.sub / .par / .cs)
static class Inventory
{
    static readonly string[] Progs = { "progVertex", "progFragment", "progGeometry", "progHull", "progDomain", "progRayTracing" };
    public static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b))[..16];

    public static string PlatformOf(int gpuType) => gpuType switch
    {
        >= 15 and <= 22 => "d3d11",   // DX11 VS/PS SM4/SM5, GS, HS, DS
        23 or 24 => "metal",
        25 => "vulkan",
        _ => $"type{gpuType}",
    };

    public static int Run(string data, string outDir, bool blobs)
    {
        using var d = new DataDir(data);
        Directory.CreateDirectory(outDir);
        var blobDir = Path.Combine(outDir, "blobs");
        if (blobs) Directory.CreateDirectory(blobDir);
        var written = new HashSet<string>();
        void Save(string sha, string ext, byte[] b) { if (blobs && written.Add(sha + ext)) File.WriteAllBytes(Path.Combine(blobDir, sha + ext), b); }

        var tsv = new StringBuilder("shader\tobject\tpass\tstage\tlist\tindex\tgpuType\tplatform\tblob\tparams\tdots\tkeywords\tsub_sha\tpar_sha\tcode_bytes\n");
        var refs = new Dictionary<string, int>(); var uniq = new Dictionary<string, HashSet<string>>();
        foreach (var s in d.Shaders())
        {
            var bf = d.Base(s.File, s.PathId);
            var plats = bf["platforms"]["Array"].Children.Select(p => p.AsInt).ToList();
            var blob = plats.Select((_, i) => new ShaderBlob(bf, i)).ToList();
            var kw = bf["m_ParsedForm"]["m_KeywordNames"]["Array"].Children.Select(k => k.AsString).ToList();
            int ssI = 0;
            foreach (var ss in bf["m_ParsedForm"]["m_SubShaders"]["Array"].Children)
            {
                int pI = 0;
                foreach (var p in ss["m_Passes"]["Array"].Children)
                {
                    foreach (var prog in Progs)
                    {
                        var outer = p[prog]["m_PlayerSubPrograms"]["Array"].Children;
                        var parIdx = p[prog]["m_ParameterBlobIndices"]["Array"].Children;
                        for (var o = 0; o < outer.Count; o++)
                            for (var i = 0; i < outer[o]["Array"].Children.Count; i++)
                            {
                                var sp = outer[o]["Array"][i];
                                var type = (int)sp["m_GpuProgramType"].AsSByte;
                                var plat = PlatformOf(type);
                                var pi = plats.IndexOf(plat switch { "d3d11" => 4, "vulkan" => 18, "metal" => 14, _ => -1 });
                                if (pi < 0) throw new Exception($"{s.Name}: program type {type} has no platform blob");
                                var bi = (int)sp["m_BlobIndex"].AsUInt;
                                var par = (int)parIdx[o]["Array"][i].AsUInt;
                                var sub = blob[pi].Entry(bi); var pe = blob[pi].Entry(par);
                                var keys = sp["m_KeywordIndices"]["Array"].Children.Select(k => kw[k.AsUShort]).ToList();
                                var dots = keys.Contains("DOTS_INSTANCING_ON") ? 1 : 0;
                                string subSha = Sha(sub), parSha = Sha(pe);
                                Save(subSha, ".sub", sub); Save(parSha, ".par", pe);
                                var code = SubProgram.Read(sub).Code.Length;
                                tsv.Append($"{s.Name}\t{s.File}:{s.PathId}\t{ssI}.{pI}\t{prog[4..]}\t{o}\t{i}\t{type}\t{plat}\t{bi}\t{par}\t{dots}\t{string.Join(" ", keys)}\t{subSha}\t{parSha}\t{code}\n");
                                var k = $"{plat} {prog[4..]}{(dots == 1 ? " DOTS" : "")}";
                                refs[k] = refs.GetValueOrDefault(k) + 1;
                                if (!uniq.TryGetValue(k, out var u)) uniq[k] = u = new();
                                u.Add($"{s.File}:{s.PathId}:{bi}");
                            }
                    }
                    pI++;
                }
                ssI++;
            }
        }
        File.WriteAllText(Path.Combine(outDir, "programs.tsv"), tsv.ToString());
        Console.WriteLine("graphics programs (references / distinct blob entries per shader):");
        foreach (var k in refs.Keys.Order()) Console.WriteLine($"  {k,-24} {refs[k],6} / {uniq[k].Count}");
        Console.WriteLine($"  d3d11 total              {refs.Where(x => x.Key.StartsWith("d3d11")).Sum(x => x.Value),6} / {uniq.Where(x => x.Key.StartsWith("d3d11")).SelectMany(x => x.Value).Distinct().Count()}");

        var ctsv = new StringBuilder("shader\tobject\trenderer\tkernel\tvariant\tkind\tcode_bytes\tsha\tkeywords\n");
        var kinds = new Dictionary<string, int>();
        foreach (var (key, inst) in d.Files)
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.ComputeShader))
            {
                var bf = d.Am.GetBaseField(inst, info);
                var name = bf["m_Name"].AsString;
                foreach (var v in bf["variants"]["Array"].Children)
                {
                    var renderer = v["targetRenderer"].AsInt;
                    foreach (var k in v["kernels"]["Array"].Children)
                    {
                        var ukw = new Dictionary<uint, List<string>>();
                        foreach (var vi in k["variantIndices"]["Array"].Children)
                        {
                            if (!ukw.TryGetValue(vi["second"].AsUInt, out var l)) ukw[vi["second"].AsUInt] = l = new();
                            l.Add(vi["first"].AsString == "" ? "-" : vi["first"].AsString);
                        }
                        var u = 0u;
                        foreach (var uv in k["uniqueVariants"]["Array"].Children)
                        {
                            var code = uv["code"]["Array"].AsByteArray;
                            var kind = CodeKind(code);
                            var sha = Sha(code);
                            Save(sha, ".cs", code);
                            ctsv.Append($"{name}\t{key}:{info.PathId}\t{renderer}\t{k["name"].AsString}\t{u}\t{kind}\t{code.Length}\t{sha}\t{string.Join(" | ", ukw.GetValueOrDefault(u) ?? new())}\n");
                            var kk = $"renderer {renderer} {kind}";
                            kinds[kk] = kinds.GetValueOrDefault(kk) + 1;
                            u++;
                        }
                    }
                }
            }
        File.WriteAllText(Path.Combine(outDir, "compute.tsv"), ctsv.ToString());
        Console.WriteLine("compute kernel variants:");
        foreach (var k in kinds.Keys.Order()) Console.WriteLine($"  {k,-24} {kinds[k],6}");
        return 0;
    }

    // DXBC container (magic "DXBC"; DXIL lives in one too, in a "DXIL" chunk), SPIR-V (magic 0x07230203)
    public static string CodeKind(byte[] code)
    {
        if (code.Length >= 4 && BitConverter.ToUInt32(code, 0) == 0x07230203) return "spirv";
        var at = code.AsSpan().IndexOf("DXBC"u8);
        if (at < 0) return code.Length == 0 ? "empty" : "other";
        var count = BitConverter.ToInt32(code, at + 28);
        for (var i = 0; i < count; i++)
        {
            var off = BitConverter.ToInt32(code, at + 32 + 4 * i);
            if (code.AsSpan(at + off, 4).SequenceEqual("DXIL"u8)) return "dxil";
        }
        return "dxbc";
    }
}
