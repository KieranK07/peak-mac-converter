using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Lmc;

// lmc: lethal-mac-converter's data tool. Dev commands while the shader translator is being built:
//   ls   <data dir>                         list Shader objects (file, pathID, name, platforms)
//   dump <data dir> <shader name> <out dir> write every blob entry of the shader's first platform
//   objdiff <data dir A> <data dir B>        per serialized file: objects whose bytes differ, by class
var cmd = args.Length > 0 ? args[0] : "";
switch (cmd)
{
    case "ls":
    {
        using var d = new DataDir(args[1]);
        foreach (var s in d.Shaders())
            Console.WriteLine($"{s.File}\t{s.PathId}\t{s.Name}\tplatforms={string.Join(",", s.Platforms)}");
        break;
    }
    case "dump":
    {
        using var d = new DataDir(args[1]);
        var s = d.Shaders().First(x => x.Name == args[2]);
        ShaderDump.Dump(d, s, args[3]);
        break;
    }
    case "xlat":
    {
        using var d = new DataDir(args[1]);
        var s = d.Shaders().First(x => x.Name == args[2]);
        XlatCmd.Run(d, s, args[3], Convert.ToUInt32(args.Length > 4 ? args[4] : "20080", 16));
        break;
    }
    case "cfields":
    {
        using var d = new DataDir(args[1]);
        foreach (var (key, inst) in d.Files)
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.ComputeShader))
            {
                var bf = d.Am.GetBaseField(inst, info);
                if (bf["m_Name"].AsString != args[2]) continue;
                void T(AssetTypeTemplateField t, string ind) { foreach (var c in t.Children) { Console.WriteLine($"{ind}{c.Name} : {c.Type}"); T(c, ind + "  "); } }
                T(d.Am.GetTemplateBaseField(inst, info), "");
                return 0;
            }
        break;
    }
    case "cbnames":
    {
        using var d = new DataDir(args[1]);
        foreach (var (key, inst) in d.Files)
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.ComputeShader))
            {
                var bf = d.Am.GetBaseField(inst, info);
                var names = bf["variants"]["Array"].Children.SelectMany(v => v["constantBuffers"]["Array"].Children.Select(c => c["name"].AsString)).Distinct();
                Console.WriteLine($"{key}:{info.PathId}\t{bf["m_Name"].AsString}\t{string.Join(",", names)}");
            }
        break;
    }
    case "cdump":
    {
        using var d = new DataDir(args[1]);
        foreach (var (key, inst) in d.Files)
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.ComputeShader))
            {
                var bf = d.Am.GetBaseField(inst, info);
                if (bf["m_Name"].AsString != args[2]) continue;
                IEnumerable<AssetTypeValueField> A(AssetTypeValueField f) => f["Array"].Children;
                string R(AssetTypeValueField r) => $"{r["name"].AsString}/{r["generatedName"].AsString}@{r["bindPoint"].AsInt}s{r["samplerBindPoint"].AsInt}d{r["texDimension"].AsInt}";
                foreach (var v in A(bf["variants"]))
                {
                    Console.WriteLine($"variant renderer={v["targetRenderer"].AsInt} level={v["targetLevel"].AsInt} resolved={v["resourcesResolved"].AsBool}");
                    foreach (var cb in A(v["constantBuffers"]))
                        Console.WriteLine($"  CB {cb["name"].AsString} size {cb["byteSize"].AsInt}: " + string.Join("; ", A(cb["params"]).Select(p => $"{p["name"].AsString}@{p["offset"].AsUInt} t{p["type"].AsInt} {p["rowCount"].AsUInt}x{p["colCount"].AsUInt}[{p["arraySize"].AsUInt}]")));
                    foreach (var k in A(v["kernels"]))
                    {
                        Console.WriteLine($"  kernel {k["name"].AsString} uniq={A(k["uniqueVariants"]).Count()} variantIndices=" + string.Join(" ", A(k["variantIndices"]).Select(p => $"[{p["first"].AsString}]->{p["second"].AsUInt}")) + $" global=[{string.Join(" ", A(k["globalKeywords"]).Select(x => x.AsString))}] local=[{string.Join(" ", A(k["localKeywords"]).Select(x => x.AsString))}] dyn=[{string.Join(" ", A(k["dynamicKeywords"]).Select(x => x.AsString))}]");
                        int u = 0;
                        foreach (var uv in A(k["uniqueVariants"]))
                        {
                            var code = uv["code"]["Array"].AsByteArray;
                            Console.WriteLine($"    uv{u} cbIdx=[{string.Join(",", A(uv["cbVariantIndices"]).Select(x => x.AsUInt))}] cbs=[{string.Join(" ", A(uv["cbs"]).Select(R))}] tex=[{string.Join(" ", A(uv["textures"]).Select(R))}] samp=[{string.Join(" ", A(uv["builtinSamplers"]).Select(x => $"{x["sampler"].AsUInt:x}@{x["bindPoint"].AsInt}"))}] in=[{string.Join(" ", A(uv["inBuffers"]).Select(R))}] out=[{string.Join(" ", A(uv["outBuffers"]).Select(R))}] tg=[{string.Join(",", A(uv["threadGroupSize"]).Select(x => x.AsUInt))}] req={uv["requirements"].AsLong} code={code.Length}B head={Convert.ToHexString(code, 0, Math.Min(64, code.Length))}");
                            if (args.Length > 3) File.WriteAllBytes(Path.Combine(args[3], $"{k["name"].AsString}.{u}.bin"), code);
                            u++;
                        }
                    }
                }
                return 0;
            }
        break;
    }
    case "objdiff":
    {
        using var a = new DataDir(args[1]);
        using var b = new DataDir(args[2]);
        static byte[] Raw(AssetsFileInstance inst, AssetFileInfo info)
        {
            inst.file.Reader.Position = info.GetAbsoluteByteOffset(inst.file);
            return inst.file.Reader.ReadBytes((int)info.ByteSize);
        }
        foreach (var name in a.Files.Keys.Union(b.Files.Keys).Order())
        {
            if (!a.Files.TryGetValue(name, out var fa) || !b.Files.TryGetValue(name, out var fb)) { Console.WriteLine($"{name}: only in {(fa == null ? "B" : "A")}"); continue; }
            var ib = fb.file.AssetInfos.ToDictionary(i => i.PathId);
            var diff = new Dictionary<string, int>();
            int same = 0;
            void Count(string what) => diff[what] = diff.GetValueOrDefault(what) + 1;
            foreach (var ia in fa.file.AssetInfos)
            {
                if (!ib.Remove(ia.PathId, out var other)) Count($"only-A {(AssetClassID)ia.TypeId}");
                else if (ia.TypeId != other.TypeId) Count($"type {(AssetClassID)ia.TypeId}->{(AssetClassID)other.TypeId}");
                else if (Raw(fa, ia).AsSpan().SequenceEqual(Raw(fb, other))) same++;
                else Count($"{(AssetClassID)ia.TypeId}");
            }
            foreach (var o in ib.Values) Count($"only-B {(AssetClassID)o.TypeId}");
            Console.WriteLine($"{name}: {same} identical" + string.Concat(diff.OrderBy(kv => kv.Key).Select(kv => $", {kv.Value} {kv.Key}")));
        }
        break;
    }
    case "progs":
    {
        using var d = new DataDir(args[1]);
        var s = d.Shaders().First(x => x.Name == args[2]);
        var bf = d.Base(s.File, s.PathId);
        foreach (var ss in bf["m_ParsedForm"]["m_SubShaders"]["Array"].Children)
            foreach (var p in ss["m_Passes"]["Array"].Children)
            {
                Console.WriteLine($"pass {p["m_Name"].AsString}/{p["m_State"]["m_Name"].AsString} names={p["m_NameIndices"]["Array"].Children.Count}");
                foreach (var prog in new[] { "progVertex", "progFragment", "progGeometry", "progHull", "progDomain" })
                {
                    var outer = p[prog]["m_PlayerSubPrograms"]["Array"].Children;
                    var types = outer.SelectMany(o => o["Array"].Children).Select(sp => sp["m_GpuProgramType"].AsSByte).Distinct();
                    Console.WriteLine($"  {prog}: tiers={outer.Count} subs=[{string.Join(",", outer.Select(o => o["Array"].Children.Count))}] params=[{string.Join(",", p[prog]["m_ParameterBlobIndices"]["Array"].Children.Select(o => o["Array"].Children.Count))}] types=[{string.Join(",", types)}] keywords={p[prog]["m_SerializedKeywordStateMask"]["Array"].Children.Count}");
                }
            }
        Console.WriteLine($"stageCounts=[{string.Join(",", bf["stageCounts"]["Array"].Children.Select(x => x.AsString))}] keywordNames={bf["m_ParsedForm"]["m_KeywordNames"]["Array"].Children.Count}");
        break;
    }
    case "bundle": // <bundle>: header, block and node table
    {
        using var b = new Bundle(args[1]);
        Console.WriteLine($"UnityFS {b.Format} {b.Version} {b.Revision} flags 0x{b.Flags:x} data at {b.DataStart}, {b.Blocks.Count} blocks ({string.Join(", ", b.Blocks.GroupBy(x => x.Flags & 0x3f).Select(g => $"{g.Count()} type {g.Key}"))}), {b.DataSize} bytes in {b.Nodes.Count} nodes");
        foreach (var n in b.Nodes) Console.WriteLine($"{n.Offset,12} {n.Size,12} 0x{n.Flags:x} {n.Path}");
        break;
    }
    case "repack": // <bundle> <out> [recompress]: no-op rewrite, then prove every node and object round-trips
        return Repack.Run(args[1], args[2], args.Length > 3 && args[3] == "recompress");
    case "treecheck": // <data> [all]: every Shader/ComputeShader (all: every object) parses to its exact size and re-serializes byte for byte
        return TreeCheck.Run(args[1], args.Length > 2 && args[2] == "all");
    case "inventory": // <data> <out dir> [blobs]: programs.tsv, compute.tsv (and every distinct blob entry)
        return Inventory.Run(args[1], args[2], args.Length > 3 && args[3] == "blobs");
    case "xlatall": // <data> <out dir> [cache dir]: every D3D program and compute kernel to MSL (for mtlcheck)
        return XlatAll.Run(args[1], args[2], args.Length > 3 ? args[3] : null);
    case "layouts": // <data>: per shader, cbuffers whose Metal layout is not the D3D one
    {
        using var d = new DataDir(args[1]);
        var tally = new Dictionary<string, int>();
        foreach (var s in d.Shaders())
        {
            ShaderPlan plan;
            try { plan = new ShaderPlan(d.Base(s.File, s.PathId), s.Name); } catch { continue; }
            foreach (var (cb, how) in plan.Layouts.Describe())
            {
                tally[$"{cb} {how}"] = tally.GetValueOrDefault($"{cb} {how}") + 1;
                if (how != "d3d") Console.WriteLine($"{s.Name}\t{cb}\t{how}");
            }
        }
        foreach (var (k, n) in tally.OrderByDescending(x => x.Value).Take(40)) Console.WriteLine($"  {n,4} {k}");
        break;
    }
    case "users":
        return ShaderUsers.Run(args[1], args[2]);
    case "videoosx": // <Unity Mac editor unity_builtin_extra> <data dir written by metalize>
        return VideoDecodeOSX.Run(args[1], args[2]);
    case "swap":
        Swap.Run(args[1], args[2], args[3], args[4]);
        break;
    case "cbdiff":
        CbDiff.Run(args[1], args[2]);
        break;
    case "metalcheck": // <source bundle> <metalized bundle> [msl dir]
        return MetalCheck.Run(args[1], args[2], args.Length > 3 ? args[3] : null);
    case "metalize": // <game Data dir or data.unity3d> <out bundle (or out dir for loose files)> [cache dir]
        return Metalize.Run(args[1], args[2], args.Length > 3 ? args[3] : null);
    case "info":
    {
        using var d = new DataDir(args[1]);
        foreach (var s in d.Shaders().Where(x => args.Length < 3 || x.Name == args[2]))
        {
            var bf = d.Base(s.File, s.PathId);
            string L(string f) => string.Join(" | ", bf[f]["Array"].Children.Select(p => string.Join(",", p["Array"].IsDummy ? new[] { p.AsString } : p["Array"].Children.Select(x => x.AsString))));
            Console.WriteLine($"{s.Name}: stageCounts=[{string.Join(",", bf["stageCounts"]["Array"].Children.Select(x => x.AsString))}] offsets={L("offsets")} clen={L("compressedLengths")} dlen={L("decompressedLengths")} blob={bf["compressedBlob"]["Array"].Children.Count}");
        }
        break;
    }
    case "fields":
    {
        using var d = new DataDir(args[1]);
        var s = d.Shaders().First(x => x.Name == args[2]);
        void P(AssetTypeValueField f, string ind)
        {
            foreach (var c in f.Children)
            {
                if (c.FieldName == "Array") { if (c.Children.Count > 0) P(c.Children[0], ind + "  "); continue; }
                Console.WriteLine($"{ind}{c.FieldName} : {c.TypeName}");
                P(c, ind + "  ");
            }
        }
        if (args.Length > 3)
        {
            var inst = d.Files[s.File];
            void T(AssetTypeTemplateField t, string ind) { foreach (var c in t.Children) { Console.WriteLine($"{ind}{c.Name} : {c.Type}"); T(c, ind + "  "); } }
            T(d.Am.GetTemplateBaseField(inst, inst.file.GetAssetInfo(s.PathId)), "");
        }
        else P(d.Base(s.File, s.PathId), "");
        break;
    }
    default:
        Console.Error.WriteLine("usage: lmc ls <data> | dump <data> <shader> <out>");
        return 1;
}
return 0;
