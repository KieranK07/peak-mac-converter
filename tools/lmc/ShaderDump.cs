using System.Text;
using AssetsTools.NET;

namespace Lmc;

// Dev aid: writes a readable manifest of a shader's player subprograms plus each program's raw code.
static class ShaderDump
{
    static readonly string[] Progs = { "progVertex", "progFragment", "progGeometry", "progHull", "progDomain", "progRayTracing" };

    public static void Dump(DataDir d, ShaderRef s, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var bf = d.Base(s.File, s.PathId);
        var blob = new ShaderBlob(bf, 0);
        var pf = bf["m_ParsedForm"];
        var kw = pf["m_KeywordNames"]["Array"].Children.Select(k => k.AsString).ToList();
        var m = new StringBuilder();
        m.AppendLine($"{s.Name} platforms={string.Join(",", s.Platforms)} segments={blob.Segments.Count} entries={blob.Entries.Count}");
        int ssI = 0;
        foreach (var ss in pf["m_SubShaders"]["Array"].Children)
        {
            int pI = 0;
            foreach (var p in ss["m_Passes"]["Array"].Children)
            {
                m.AppendLine($"== pass {ssI}.{pI} '{p["m_Name"].AsString}' type {p["m_Type"].AsInt}");
                var names = p["m_NameIndices"]["Array"].Children.ToDictionary(x => x["second"].AsInt, x => x["first"].AsString);
                foreach (var prog in Progs)
                {
                    var pg = p[prog];
                    var outer = pg["m_PlayerSubPrograms"]["Array"].Children;
                    var parIdx = pg["m_ParameterBlobIndices"]["Array"].Children;
                    for (var o = 0; o < outer.Count; o++)
                    {
                        var inner = outer[o]["Array"].Children;
                        for (var i = 0; i < inner.Count; i++)
                        {
                            var sp = inner[i];
                            var bi = (int)sp["m_BlobIndex"].AsUInt;
                            var pidx = parIdx.Count > o && parIdx[o]["Array"].Children.Count > i ? (int)parIdx[o]["Array"][i].AsUInt : -1;
                            var keys = string.Join(" ", sp["m_KeywordIndices"]["Array"].Children.Select(k => kw[k.AsUShort]));
                            m.AppendLine($"  {prog}[{o}][{i}] gpu={sp["m_GpuProgramType"].AsSByte} blob={bi} params={pidx} req={sp["m_ShaderRequirements"].AsLong} kw=[{keys}]");
                            var file = $"{ssI}.{pI}.{prog}.{o}.{i}";
                            m.Append(SubProgram(blob.Entry(bi), Path.Combine(outDir, file)));
                            if (pidx >= 0)
                            {
                                var pe = blob.Entry(pidx);
                                File.WriteAllBytes(Path.Combine(outDir, file) + ".params", pe);
                                m.Append(Params(pe));
                            }
                        }
                    }
                    var common = pg["m_CommonParameters"];
                    if (!common.IsDummy && common.Children.Any(c => c["Array"].Children.Count > 0))
                    {
                        m.AppendLine($"    {prog} common:");
                        Common(common, names, "      ", m);
                    }
                }
                pI++;
            }
            ssI++;
        }
        File.WriteAllText(Path.Combine(outDir, "manifest.txt"), m.ToString());
    }

    // m_CommonParameters, every list item on one line; m_NameIndex resolved through the pass's name table.
    static void Common(AssetTypeValueField f, Dictionary<int, string> names, string ind, StringBuilder m)
    {
        foreach (var list in f.Children)
            foreach (var item in list["Array"].Children)
            {
                var scalars = item.Children.Where(c => c.Children.Count == 0)
                    .Select(c => c.FieldName == "m_NameIndex" ? $"'{names[c.AsInt]}'" : $"{c.FieldName.Replace("m_", "")}={c.AsString}");
                m.AppendLine($"{ind}{list.FieldName.Replace("m_", "")}: {string.Join(" ", scalars)}");
                Common(item, names, ind + "  ", m);
            }
    }

    static string Str(BinaryReader r)
    {
        var n = r.ReadInt32();
        var s = Encoding.UTF8.GetString(r.ReadBytes(n));
        Align(r);
        return s;
    }

    static void Align(BinaryReader r) => r.BaseStream.Position = (r.BaseStream.Position + 3) & ~3L;

    static string SubProgram(byte[] e, string path)
    {
        File.WriteAllBytes(path + ".entry", e);
        var r = new BinaryReader(new MemoryStream(e));
        var sb = new StringBuilder();
        var ver = r.ReadInt32(); var type = r.ReadInt32();
        var stats = Enumerable.Range(0, 4).Select(_ => r.ReadInt32()).ToList();
        var nk = r.ReadInt32();
        var keys = Enumerable.Range(0, nk).Select(_ => Str(r)).ToList();
        var len = r.ReadInt32();
        var code = r.ReadBytes(len);
        Align(r);
        File.WriteAllBytes(path + ".bin", code);
        var srcMap = r.ReadInt32(); var nch = r.ReadInt32();
        var ch = Enumerable.Range(0, nch).Select(_ => $"{r.ReadInt32()}->{r.ReadInt32()}").ToList();
        var rest = e.Length - r.BaseStream.Position;
        sb.AppendLine($"    sub v{ver} type={type} stats=[{string.Join(",", stats)}] kw=[{string.Join(" ", keys)}] code={len}B channels(map {srcMap:x})=[{string.Join(" ", ch)}] trailing={rest}B");
        if (rest > 0) sb.AppendLine("      trailing: " + Convert.ToHexString(e, (int)r.BaseStream.Position, (int)Math.Min(rest, 64)));
        return sb.ToString();
    }

    static string Params(byte[] e)
    {
        var r = new BinaryReader(new MemoryStream(e));
        var sb = new StringBuilder();
        var ver = r.ReadInt32();
        var ncb = r.ReadInt32();
        for (var c = 0; c < ncb; c++)
        {
            var name = Str(r); var used = r.ReadInt32(); var np = r.ReadInt32();
            var ps = new List<string>();
            for (var i = 0; i < np; i++) ps.Add(Param(r));
            var ns = r.ReadInt32();
            for (var i = 0; i < ns; i++)
            {
                var sn = Str(r); var idx = r.ReadInt32(); var arr = r.ReadInt32(); var size = r.ReadInt32(); var n = r.ReadInt32();
                ps.Add($"struct {sn}@{idx}[{arr}] size {size} {{{string.Join(", ", Enumerable.Range(0, n).Select(_ => Param(r)))}}}");
            }
            sb.AppendLine($"    params v{ver} cb '{name}' size {used}: {string.Join("; ", ps)}");
        }
        var nb = r.ReadInt32();
        for (var i = 0; i < nb; i++)
        {
            var name = Str(r); var t = r.ReadInt32();
            var desc = t switch
            {
                0 => $"tex idx {r.ReadInt32()} sampler {r.ReadInt32()} extra {r.ReadUInt32():x}",
                1 => $"cbbind idx {r.ReadInt32()} arr {r.ReadInt32()}",
                2 => $"buffer idx {r.ReadInt32()} arr {r.ReadInt32()}",
                3 => $"uav idx {r.ReadInt32()} orig {r.ReadInt32()}",
                4 => $"sampler bind {r.ReadInt32()} state {r.ReadUInt32():x}",
                _ => throw new Exception($"binding type {t}")
            };
            sb.AppendLine($"    bind '{name}' {desc}");
        }
        var rest = e.Length - r.BaseStream.Position;
        if (rest > 0) sb.AppendLine($"    params trailing {rest}B: " + Convert.ToHexString(e, (int)r.BaseStream.Position, (int)Math.Min(rest, 64)));
        return sb.ToString();
    }

    static string Param(BinaryReader r)
    {
        var name = Str(r); var type = r.ReadInt32(); var rows = r.ReadInt32(); var cols = r.ReadInt32(); var mat = r.ReadInt32(); var arr = r.ReadInt32(); var idx = r.ReadInt32();
        return $"{name}@{idx} t{type} {rows}x{cols}{(mat > 0 ? "M" : "")}{(arr > 0 ? $"[{arr}]" : "")}";
    }
}
