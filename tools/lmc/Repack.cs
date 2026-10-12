using System.Security.Cryptography;

namespace Lmc;

// Dev proof for the bundle writer: rewrite a bundle with nothing replaced and check that every node, and every
// object of every serialized file, reads back byte for byte.
static class Repack
{
    public static int Run(string src, string dst, bool recompress)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var b = new Bundle(src)) b.Write(dst, new Dictionary<string, byte[]>(), recompress);
        Console.WriteLine($"wrote {dst} ({new FileInfo(dst).Length} bytes, source {new FileInfo(src).Length}) in {sw.Elapsed.TotalSeconds:F0}s");
        using var a = new Bundle(src);
        using var c = new Bundle(dst);
        int bad = 0, objs = 0;
        if (a.Nodes.Count != c.Nodes.Count) { Console.WriteLine("node count differs"); return 1; }
        // one manager per side: AssetsManager hands back an already loaded file of the same name
        var am = new AssetsTools.NET.Extra.AssetsManager(); var amC = new AssetsTools.NET.Extra.AssetsManager();
        for (var i = 0; i < a.Nodes.Count; i++)
        {
            var (x, y) = (a.Nodes[i], c.Nodes[i]);
            var (bx, by) = (a.Read(x), c.Read(y));
            if (x.Path != y.Path || x.Flags != y.Flags || !SHA256.HashData(bx).AsSpan().SequenceEqual(SHA256.HashData(by))) { bad++; Console.WriteLine($"node {x.Path} differs"); continue; }
            if ((x.Flags & 4) == 0) continue;
            // objects: compare through the serialized file's own object table
            var fa = am.LoadAssetsFile(new MemoryStream(bx), x.Path, false);
            var fc = amC.LoadAssetsFile(new MemoryStream(by), y.Path, false);
            var oc = fc.file.AssetInfos.ToDictionary(o => o.PathId);
            foreach (var o in fa.file.AssetInfos)
            {
                objs++;
                if (!oc.TryGetValue(o.PathId, out var p) || !DataDir.ObjectBytes(fa, o).AsSpan().SequenceEqual(DataDir.ObjectBytes(fc, p))) { bad++; Console.WriteLine($"{x.Path}:{o.PathId} differs"); }
            }
            if (oc.Count != fa.file.AssetInfos.Count) { bad++; Console.WriteLine($"{x.Path}: object count differs"); }
            am.UnloadAll(); amC.UnloadAll();
        }
        var same = new FileInfo(src).Length == new FileInfo(dst).Length && FilesEqual(src, dst);
        Console.WriteLine($"{a.Nodes.Count} nodes, {objs} objects compared: {(bad == 0 ? "all identical" : bad + " differ")}; output file {(same ? "byte-identical to the source" : "differs from the source (recompressed)")}; {sw.Elapsed.TotalSeconds:F0}s");
        return bad == 0 ? 0 : 1;
    }

    static bool FilesEqual(string a, string b)
    {
        using var x = File.OpenRead(a); using var y = File.OpenRead(b);
        var (p, q) = (new byte[1 << 22], new byte[1 << 22]);
        for (int n; (n = x.Read(p)) > 0;) { y.ReadExactly(q, 0, n); if (!p.AsSpan(0, n).SequenceEqual(q.AsSpan(0, n))) return false; }
        return true;
    }
}
