using System.Text;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// Dev: translate every D3D11 graphics program and every compute kernel variant of a build to MSL, exactly as metalize
// plans them, and write each distinct result once for the Metal compile check (tools/xlat/mtlcheck.swift).
//   <out>/msl/<sha>.metal  one file per distinct MSL text
//   <out>/xlat.tsv         one row per job: category, shader, job, keywords, ok, msl sha, error
// With a cache dir, each Shader/ComputeShader's rows are kept under sha256(tool binaries + original object bytes).
static class XlatAll
{
    public record Row(string Category, string Shader, string Job, string Keywords, bool Ok, string Msl, string Error);

    public static int Run(string data, string outDir, string? cacheDir)
    {
        using var d = new DataDir(data);
        var mslDir = Path.Combine(outDir, "msl");
        Directory.CreateDirectory(mslDir);
        var cache = cacheDir == null ? null : new ObjectCache(cacheDir);
        var rows = new List<Row>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var only = Environment.GetEnvironmentVariable("LMC_ONLY")?.Split('|');
        foreach (var (key, inst) in d.Files)
            foreach (var info in inst.file.AssetInfos.Where(i => i.TypeId is (int)AssetClassID.Shader or (int)AssetClassID.ComputeShader))
            {
                var bf = d.Am.GetBaseField(inst, info);
                var isShader = info.TypeId == (int)AssetClassID.Shader;
                var name = isShader ? bf["m_ParsedForm"]["m_Name"].AsString : bf["m_Name"].AsString;
                if (only != null && !only.Contains(name)) continue;
                List<Row>? got = null;
                var ck = cache?.Key(DataDir.ObjectBytes(inst, info));
                if (ck != null && cache!.TryGet(ck, out var hit)) got = JsonSerializer.Deserialize<List<Row>>(hit);
                if (got == null)
                {
                    try { got = isShader ? Shader(bf, name) : Compute(bf, name); }
                    catch (Exception e) { got = new() { new Row(isShader ? "shader" : "compute", name, "-", "", false, "", e.Message) }; }
                    if (ck != null) cache!.Put(ck, JsonSerializer.SerializeToUtf8Bytes(got));
                }
                foreach (var r in got)
                {
                    if (r.Ok)
                    {
                        var path = Path.Combine(mslDir, Inventory.Sha(Encoding.UTF8.GetBytes(r.Msl)) + ".metal");
                        if (!File.Exists(path)) File.WriteAllText(path, r.Msl);
                    }
                    rows.Add(r with { Msl = r.Ok ? Inventory.Sha(Encoding.UTF8.GetBytes(r.Msl)) : "" });
                }
            }
        if (cache != null && only == null) cache.Prune();
        if (only == null)  // keep just this run's MSL, so mtlcheck's directory results describe it
        {
            var live = rows.Select(r => r.Msl + ".metal").ToHashSet();
            foreach (var f in Directory.GetFiles(mslDir, "*.metal")) if (!live.Contains(Path.GetFileName(f))) File.Delete(f);
        }
        var tsv = new StringBuilder("category\tshader\tjob\tkeywords\tok\tmsl\terror\n");
        foreach (var r in rows) tsv.Append($"{r.Category}\t{r.Shader}\t{r.Job}\t{r.Keywords}\t{(r.Ok ? 1 : 0)}\t{r.Msl}\t{r.Error}\n");
        File.WriteAllText(Path.Combine(outDir, "xlat.tsv"), tsv.ToString());
        Console.WriteLine($"{rows.Count} jobs, {rows.Count(r => r.Ok)} translated, {rows.Select(r => r.Msl).Where(m => m != "").Distinct().Count()} distinct MSL ({(cache == null ? "no cache" : $"{cache.Hits} objects reused, {cache.Misses} translated")}), {sw.Elapsed.TotalSeconds:F0}s");
        foreach (var g in rows.GroupBy(r => r.Category).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-28} {g.Count(r => r.Ok),5} ok {g.Count(r => !r.Ok),5} failed");
        foreach (var g in rows.Where(r => !r.Ok).GroupBy(r => r.Error.Length > 90 ? r.Error[..90] : r.Error).OrderByDescending(g => g.Count()).Take(15))
            Console.WriteLine($"  {g.Count(),5}x {g.Key}  (e.g. {g.First().Shader} {g.First().Job})");
        return 0;
    }

    static string Err(string refl) => refl.Split('\n').FirstOrDefault(l => l.StartsWith("error"))?.Replace('\t', ' ') ?? "failed";

    static List<Row> Shader(AssetTypeValueField bf, string name)
    {
        var plan = new ShaderPlan(bf, name);
        // Metal has no geometry stage: such a shader keeps no programs (as in Unity's own Metal build)
        if (plan.Jobs.Any(j => j.Stages.Contains("progGeometry")))
            return new() { new Row("geometry (emptied)", name, "-", "", true, "", "") };
        var xl = plan.Translate();
        return plan.Jobs.Select((j, i) =>
        {
            var stage = string.Join("+", j.Stages.Select(s => s[4..].ToLowerInvariant()));
            var cat = $"{stage}{(j.Keywords.Contains("DOTS_INSTANCING_ON") ? " DOTS" : "")}";
            return new Row(cat, name, j.Tag, string.Join(" ", j.Keywords), xl[i].ok, xl[i].ok ? xl[i].msl : "", xl[i].ok ? "" : Err(xl[i].refl));
        }).ToList();
    }

    static List<Row> Compute(AssetTypeValueField bf, string name)
    {
        var (res, _, _) = MetalCompute.Translate(bf);
        return res.Select(r => new Row(r.Kind == "dxbc" ? "compute" : $"compute {r.Kind} (skipped)", name, $"{r.Kernel}.{r.Variant}", "", r.Ok, r.Ok ? r.Msl : "", r.Ok ? "" : Err(r.Refl))).ToList();
    }
}
