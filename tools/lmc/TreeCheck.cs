using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// Proof that the class database describes this build's objects: each object must parse to exactly its stored size
// and re-serialize to the same bytes. Default: Shader and ComputeShader; all: every class (MonoBehaviours are
// skipped, their script fields are not in the class database).
static class TreeCheck
{
    public static int Run(string data, bool all)
    {
        using var d = new DataDir(data);
        var stats = new SortedDictionary<string, (int ok, int bad)>();
        foreach (var (key, inst) in d.Files)
            foreach (var info in inst.file.AssetInfos)
            {
                var cls = (AssetClassID)info.TypeId;
                if (!all && cls is not (AssetClassID.Shader or AssetClassID.ComputeShader)) continue;
                if (cls == AssetClassID.MonoBehaviour) continue;
                var name = $"{cls}";
                string? why;
                try
                {
                    var t = d.Am.GetTemplateBaseField(inst, info);
                    var r = inst.file.Reader;
                    var start = info.GetAbsoluteByteOffset(inst.file);
                    var v = t.MakeValue(r, start);
                    var used = r.Position - start;
                    why = used != info.ByteSize ? $"parsed {used} of {info.ByteSize} bytes"
                        : !v.WriteToByteArray().AsSpan().SequenceEqual(DataDir.ObjectBytes(inst, info)) ? "re-serialized bytes differ" : null;
                }
                catch (Exception e) { why = e.Message; }
                var (ok, bad) = stats.GetValueOrDefault(name);
                stats[name] = why == null ? (ok + 1, bad) : (ok, bad + 1);
                if (why != null && bad < 3) Console.WriteLine($"  {key}:{info.PathId} {cls}: {why}");
            }
        foreach (var (k, (ok, bad)) in stats) Console.WriteLine($"{k}: {ok} exact{(bad > 0 ? $", {bad} FAIL" : "")}");
        return stats.Values.Any(s => s.bad > 0) ? 1 : 0;
    }
}
