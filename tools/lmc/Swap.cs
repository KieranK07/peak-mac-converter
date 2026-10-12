using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// Dev bisect tool: rewrite the serialized files of <base data> with the Shader/ComputeShader objects of
// <donor data> whose name matches <regex> (prefix "compute:" or "shader:" to limit the class). Both builds
// must come from the same game data, so path IDs line up. Only files with a swapped object are written.
static class Swap
{
    public static void Run(string baseDir, string donorDir, string outDir, string filter)
    {
        using var b = new DataDir(baseDir);
        using var d = new DataDir(donorDir);
        var kind = filter.StartsWith("compute:") ? "compute" : filter.StartsWith("shader:") ? "shader" : "";
        var re = new Regex(kind == "" ? filter : filter[(kind.Length + 1)..]);
        foreach (var (key, inst) in b.Files)
        {
            var donor = d.Files[key];
            var repl = new Dictionary<long, byte[]>();
            foreach (var info in inst.file.AssetInfos)
            {
                var cls = (AssetClassID)info.TypeId;
                if (!(cls == AssetClassID.Shader && kind != "compute" || cls == AssetClassID.ComputeShader && kind != "shader")) continue;
                var bf = b.Am.GetBaseField(inst, info);
                var name = cls == AssetClassID.Shader ? bf["m_ParsedForm"]["m_Name"].AsString : bf["m_Name"].AsString;
                if (!re.IsMatch(name)) continue;
                var di = donor.file.GetAssetInfo(info.PathId);
                donor.file.Reader.Position = di.GetAbsoluteByteOffset(donor.file);
                repl[info.PathId] = donor.file.Reader.ReadBytes((int)di.ByteSize);
                Console.WriteLine($"{key}: {cls} {name}");
            }
            if (repl.Count == 0) continue;
            var outPath = key == "unity_builtin_extra" ? Path.Combine(outDir, "Resources", key) : Path.Combine(outDir, key);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, SerializedWriter.WriteSerialized(b.Raw(key), inst, repl, SerializedWriter.StandaloneOSX));
        }
    }
}
