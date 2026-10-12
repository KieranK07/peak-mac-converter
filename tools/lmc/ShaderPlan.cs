using AssetsTools.NET;

namespace Lmc;

// What a Shader's Metal translation consists of: the unique (code, parameters) D3D programs to translate (jobs),
// where each is referenced (sites), and the shader-wide cbuffer layouts.
// Unity 6 keeps every platform's player subprograms in the same lists (PEAK: D3D11 and Vulkan SPIR-V interleaved
// in list 3), told apart by GPU program type; only the D3D11 ones are planned, from the D3D11 platform's blob.
// A tessellated pass has no hull or domain programs on Metal: D3D subprogram n of the vertex, hull and domain lists
// (same keywords) becomes one Metal vertex program, docs/METAL-TESSELLATION.md.
sealed class ShaderPlan
{
    public record Job(string Tag, SubProgram[] Subs, Params[] Pars, string[] Stages, List<string> Keywords, string Pass);
    public readonly List<Job> Jobs = new();
    public readonly List<(AssetTypeValueField sp, AssetTypeValueField par, int job)> Sites = new();
    public readonly List<AssetTypeValueField> Commons = new(), TessLists = new();
    public readonly List<AssetTypeValueField> Passes;
    public readonly CbLayouts Layouts = new(compute: false);
    public readonly int PlatformIndex;  // of D3D11 in `platforms`

    public static bool IsD3D(AssetTypeValueField sp) => sp["m_GpuProgramType"].AsSByte is >= 15 and <= 22;

    public ShaderPlan(AssetTypeValueField bf, string name)
    {
        var platforms = bf["platforms"]["Array"].Children.Select(p => p.AsInt).ToList();
        PlatformIndex = platforms.IndexOf(MetalShader.PlatformD3D11);
        if (PlatformIndex < 0) throw new Exception($"{name}: no D3D11 programs (platforms {string.Join(",", platforms)})");
        var blob = new ShaderBlob(bf, PlatformIndex);
        var kwNames = bf["m_ParsedForm"]["m_KeywordNames"]["Array"].Children.Select(k => k.AsString).ToList();
        Passes = bf["m_ParsedForm"]["m_SubShaders"]["Array"].Children.SelectMany(ss => ss["m_Passes"]["Array"].Children).ToList();
        var progs = MetalShader.Progs; var tessProgs = MetalShader.TessProgs;
        foreach (var p in Passes)
        {
            var names = p["m_NameIndices"]["Array"].Children.ToDictionary(x => x["second"].AsInt, x => x["first"].AsString);
            var tess = MetalShader.HasPrograms(p["progHull"]) || MetalShader.HasPrograms(p["progDomain"]);
            foreach (var group in tess ? new[] { tessProgs }.Concat(progs.Except(tessProgs).Select(x => new[] { x })) : progs.Select(x => new[] { x }))
            {
                var pgs = group.Select(x => p[x]).ToArray();
                var common = pgs.Select(pg => Params.FromCommon(pg["m_CommonParameters"], names)).ToArray();
                var outer = pgs.Select(pg => pg["m_PlayerSubPrograms"]["Array"].Children).ToArray();
                var parIdx = pgs.Select(pg => pg["m_ParameterBlobIndices"]["Array"].Children).ToArray();
                var seen = new Dictionary<string, int>();
                for (var o = 0; o < outer[0].Count; o++)
                {
                    // (subprogram, its index in the list) of the D3D programs only
                    var inner = outer.Select(x => x[o]["Array"].Children.Select((sp, i) => (sp, i)).Where(t => IsD3D(t.sp)).ToList()).ToArray();
                    if (inner.Any(x => x.Count != inner[0].Count)) throw new Exception($"{name}: list {o} has {string.Join("/", inner.Select(x => x.Count))} vertex/hull/domain programs");
                    for (var n = 0; n < inner[0].Count; n++)
                    {
                        var sps = inner.Select(x => x[n]).ToArray();
                        if (sps.Select(t => MetalShader.Keywords(t.sp)).Distinct().Count() != 1) throw new Exception($"{name}: vertex/hull/domain program {n} differ in keywords");
                        var keys = sps.Select((t, k) => (blob: t.sp["m_BlobIndex"].AsUInt, par: parIdx[k][o]["Array"][t.i].AsUInt)).ToArray();
                        var key = string.Join(";", keys);
                        if (!seen.TryGetValue(key, out var job))
                        {
                            var subs = keys.Select(k => SubProgram.Read(blob.Entry((int)k.blob))).ToArray();
                            var pars = keys.Select((k, m) => Params.FromBlob(blob.Entry((int)k.par)).Merge(common[m])).ToArray();
                            seen[key] = job = Jobs.Count;
                            foreach (var par in pars) par.AddTo(Layouts);
                            var kws = sps[0].sp["m_KeywordIndices"]["Array"].Children.Select(k => kwNames[k.AsUShort]).ToList();
                            Jobs.Add(new Job($"{name}.{string.Join("+", group)}.{string.Join(".", keys.Select(k => k.blob))}", subs, pars, group, kws, p["m_State"]["m_Name"].AsString));
                        }
                        Sites.Add((sps[0].sp, parIdx[0][o]["Array"][sps[0].i], job));
                    }
                }
                foreach (var pg in pgs) Commons.Add(pg["m_CommonParameters"]);
                if (group.Length > 1)
                    foreach (var pg in pgs.Skip(1)) TessLists.AddRange(new[] { pg["m_PlayerSubPrograms"], pg["m_ParameterBlobIndices"] });
            }
        }
        Layouts.Freeze(); // decide every layout before the parallel part reads them
    }

    // Every job through HLSLcc, in parallel (HLSLcc keeps no global state). Never throws: a failed job has ok=false
    // and its error in the reflection text.
    public (bool ok, string msl, string refl)[] Translate()
    {
        var res = new (bool, string, string)[Jobs.Count];
        Parallel.For(0, Jobs.Count, j =>
        {
            var job = Jobs[j];
            try
            {
                res[j] = job.Subs.Length == 1
                    ? Xlat.Translate(Xlat.Dxbc(job.Subs[0].Code), job.Pars[0].ToDesc(Layouts), MetalShader.HlslccFlags)
                    // each stage declares the cbuffers it uses first, for all three: give it every member the three read
                    : Xlat.TranslateTess(job.Subs.Select(x => Xlat.Dxbc(x.Code)).ToArray(), job.Pars.Select(x => x.WithCbsOf(job.Pars).ToDesc(Layouts)).ToArray(), MetalShader.HlslccFlags);
            }
            catch (Exception e) { res[j] = (false, "", $"error\t0\t{e.Message}\n"); }
            if (res[j].Item1 && job.Stages.Contains("progFragment"))
                try { res[j].Item2 = FbFetch.Apply(res[j].Item2); }
                catch (Exception e) { res[j] = (false, "", $"error\t0\t{e.Message}\n"); }
        });
        return res;
    }
}
