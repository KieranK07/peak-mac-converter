using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// Dev: which Materials use a shader, and which renderers (with their root GameObject) use those materials.
//   lmc users <data dir> <shader name>
static class ShaderUsers
{
    public static int Run(string dir, string shaderName)
    {
        using var d = new DataDir(dir);
        var shaders = d.Shaders().Where(s => s.Name == shaderName).Select(s => (s.File, s.PathId)).ToHashSet();

        // fileID 0 = this file, n = Externals[n-1]; externals are stored as e.g. "sharedassets1.assets" or "library/unity default resources"
        (string, long) Resolve(string file, AssetTypeValueField pptr)
        {
            int fid = pptr["m_FileID"].AsInt;
            long pid = pptr["m_PathID"].AsLong;
            if (fid == 0) return (file, pid);
            var ext = d.Files[file].file.Metadata.Externals[fid - 1].PathName;
            return (Path.GetFileName(ext), pid);
        }

        var mats = new Dictionary<(string, long), string>();
        foreach (var (key, inst) in d.Files)
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Material))
            {
                var bf = d.Am.GetBaseField(inst, info);
                if (!shaders.Contains(Resolve(key, bf["m_Shader"]))) continue;
                mats[(key, info.PathId)] = bf["m_Name"].AsString;
                Console.WriteLine($"MATERIAL {key}:{info.PathId}\t{bf["m_Name"].AsString}");
            }

        string GoName(string file, AssetTypeValueField goPtr) => d.Base(file, goPtr["m_PathID"].AsLong)["m_Name"].AsString;
        string RootPath(string file, AssetTypeValueField goPtr)
        {
            var names = new List<string>();
            var go = d.Base(file, goPtr["m_PathID"].AsLong);
            names.Add(go["m_Name"].AsString);
            var tr = go["m_Component"]["Array"].Children.Select(c => c["component"]["m_PathID"].AsLong).First();
            for (var t = d.Base(file, tr); t["m_Father"]["m_PathID"].AsLong != 0;)
            {
                t = d.Base(file, t["m_Father"]["m_PathID"].AsLong);
                names.Add(GoName(file, t["m_GameObject"]));
            }
            names.Reverse();
            return string.Join("/", names);
        }

        foreach (var (key, inst) in d.Files)
            foreach (var cls in new[] { AssetClassID.MeshRenderer, AssetClassID.SkinnedMeshRenderer, AssetClassID.ParticleSystemRenderer, AssetClassID.TrailRenderer, AssetClassID.LineRenderer })
                foreach (var info in inst.file.GetAssetsOfType(cls))
                {
                    var bf = d.Am.GetBaseField(inst, info);
                    if (!bf["m_Materials"]["Array"].Children.Any(m => mats.ContainsKey(Resolve(key, m)))) continue;
                    Console.WriteLine($"RENDERER {key}:{info.PathId}\t{cls}\t{RootPath(key, bf["m_GameObject"])}");
                }

        // levelN -> scene path
        var gg = d.Files["globalgamemanagers"];
        foreach (var info in gg.file.GetAssetsOfType(AssetClassID.BuildSettings))
        {
            int i = 0;
            foreach (var s in d.Am.GetBaseField(gg, info)["scenes"]["Array"].Children)
                Console.WriteLine($"SCENE level{i++}\t{s.AsString}");
        }
        return 0;
    }
}
