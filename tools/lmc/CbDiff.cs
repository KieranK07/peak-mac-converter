using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// Dev check: constant buffer layouts of two Metal builds (Unity's own vs ours), shader by shader. Values that
// sit at different offsets matter when C# uploads the buffer as raw bytes (HDRP's ShaderVariables* structs).
static class CbDiff
{
    // cbuffer name -> (sizes seen, member name -> offsets seen)
    sealed class Seen { public SortedSet<int> Sizes = new(); public Dictionary<string, SortedSet<int>> Members = new(); }

    public static void Run(string dirA, string dirB)
    {
        using var a = new DataDir(dirA);
        using var b = new DataDir(dirB);
        var sa = Shaders(a); var sb = Shaders(b);
        foreach (var name in sa.Keys.Intersect(sb.Keys).Order()) Report(name, sa[name], sb[name]);
        var ca = Computes(a); var cc = Computes(b);
        foreach (var name in ca.Keys.Intersect(cc.Keys).Order()) Report("compute " + name, ca[name], cc[name]);
    }

    static void Report(string what, Dictionary<string, Seen> a, Dictionary<string, Seen> b)
    {
        foreach (var cb in a.Keys.Intersect(b.Keys).Order())
        {
            var (x, y) = (a[cb], b[cb]);
            var bad = x.Members.Keys.Intersect(y.Members.Keys).Where(m => !x.Members[m].SetEquals(y.Members[m]))
                .Select(m => $"{m} {string.Join("/", x.Members[m])}->{string.Join("/", y.Members[m])}").ToList();
            var sizes = x.Sizes.SetEquals(y.Sizes) ? "" : $" size {string.Join("/", x.Sizes)}->{string.Join("/", y.Sizes)}";
            if (bad.Count > 0) Console.WriteLine($"{what} | {cb}{sizes}: {bad.Count} moved: {string.Join(", ", bad.Take(6))}");
            else if (sizes != "") Console.WriteLine($"{what} | {cb}{sizes} (offsets equal)");
        }
        foreach (var cb in a.Keys.Except(b.Keys)) Console.WriteLine($"{what} | {cb}: only in A");
    }

    static void Add(Dictionary<string, Seen> d, string cb, int size, IEnumerable<(string name, int off)> members)
    {
        if (!d.TryGetValue(cb, out var s)) d[cb] = s = new();
        s.Sizes.Add(size);
        foreach (var (n, o) in members)
        {
            if (n.StartsWith(CbLayouts.PadPrefix)) continue;
            if (!s.Members.TryGetValue(n, out var set)) s.Members[n] = set = new();
            set.Add(o);
        }
    }

    static Dictionary<string, Dictionary<string, Seen>> Shaders(DataDir d)
    {
        var all = new Dictionary<string, Dictionary<string, Seen>>();
        foreach (var s in d.Shaders())
        {
            var bf = d.Base(s.File, s.PathId);
            if (bf["platforms"]["Array"].Children.Count == 0 || bf["platforms"]["Array"][0].AsInt != MetalShader.PlatformMetal) continue;
            var blob = new ShaderBlob(bf, 0);
            var cbs = all[s.Name] = new();
            foreach (var p in bf["m_ParsedForm"]["m_SubShaders"]["Array"].Children.SelectMany(ss => ss["m_Passes"]["Array"].Children))
            {
                var names = p["m_NameIndices"]["Array"].Children.ToDictionary(x => x["second"].AsInt, x => x["first"].AsString);
                foreach (var prog in new[] { "progVertex", "progFragment" })
                {
                    var common = Params.FromCommon(p[prog]["m_CommonParameters"], names);
                    foreach (var idx in p[prog]["m_ParameterBlobIndices"]["Array"].Children.SelectMany(t => t["Array"].Children).Select(x => (int)x.AsUInt).Distinct())
                        foreach (var cb in Params.FromBlob(blob.Entry(idx)).Merge(common).Cbs)
                            Add(cbs, cb.Name, cb.Size, cb.Params.Select(v => (v.Name, v.Index)));
                }
            }
        }
        return all;
    }

    static Dictionary<string, Dictionary<string, Seen>> Computes(DataDir d)
    {
        var all = new Dictionary<string, Dictionary<string, Seen>>();
        foreach (var (_, inst) in d.Files)
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.ComputeShader))
            {
                var bf = d.Am.GetBaseField(inst, info);
                var cbs = all[bf["m_Name"].AsString] = new();
                foreach (var v in bf["variants"]["Array"].Children)
                    foreach (var cb in v["constantBuffers"]["Array"].Children)
                        Add(cbs, cb["name"].AsString, cb["byteSize"].AsInt, cb["params"]["Array"].Children.Select(p => (p["name"].AsString, (int)p["offset"].AsUInt)));
            }
        return all;
    }
}
