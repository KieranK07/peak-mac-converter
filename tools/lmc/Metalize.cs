using System.Diagnostics;
using System.Security.Cryptography;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// The shader stage of the converter: writes the game's serialized files with the platform set to macOS, each
// Shader/ComputeShader replaced by its Metal translation, and the graphics settings the player reads to pick Metal.
// Input: a UnityFS bundle -> output: a patched bundle at `outPath` (never in place), then MetalCheck proves it.
// A Data folder of loose files -> loose files under `outPath`, then each map bundle in its StreamingAssets/maps (PEAK
// since build 26155610) -> the same path under `outPath`. Older PEAK builds packed everything into Data/data.unity3d.
// With a cache dir, each translated object is kept under a hash of the game's original object and of this
// tool's binaries, so a re-run (or a game update) only translates shaders that are new or changed.
static class Metalize
{
    public static int Run(string gameData, string outPath, string? cacheDir = null)
    {
        var only = Environment.GetEnvironmentVariable("LMC_ONLY")?.Split('|'); // dev: translate just these names
        var cache = cacheDir == null || Mark.Any ? null : new ObjectCache(cacheDir);
        var maps = Path.Combine(gameData, "StreamingAssets", "maps");
        var jobs = new List<(string In, string Out)> { (gameData, outPath) };
        if (Directory.Exists(maps))
            jobs.AddRange(Directory.GetFiles(maps, "*.assetbundle").Order().Select(b => (b, Path.Combine(outPath, "StreamingAssets", "maps", Path.GetFileName(b)))));
        foreach (var (i, o) in jobs)
        {
            var rc = One(i, o, cache, only);
            if (rc != 0) return rc;
        }
        if (cache != null && only == null) cache.Prune();  // keep just this game version's set
        return 0;
    }

    static int One(string gameData, string outPath, ObjectCache? cache, string[]? only)
    {
        var problems = new List<string>();
        var sw = Stopwatch.StartNew();
        int shaders = 0, computes = 0;
        string? bundleIn = null;
        using (var d = new DataDir(gameData))
        {
            byte[] Cached(AssetsFileInstance inst, AssetFileInfo info, Func<byte[]> make)
            {
                if (cache == null) return make();
                var key = cache.Key(DataDir.ObjectBytes(inst, info));
                if (cache.TryGet(key, out var hit)) return hit;
                var before = problems.Count;
                var bytes = make();
                if (problems.Count == before) cache.Put(key, bytes);
                return bytes;
            }
            var nodeRepl = new Dictionary<string, byte[]>();
            foreach (var (key, inst) in d.Files)
            {
                var repl = new Dictionary<long, byte[]>();
                foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Shader))
                {
                    var bf = d.Am.GetBaseField(inst, info);
                    var s = new ShaderRef(key, info.PathId, bf["m_ParsedForm"]["m_Name"].AsString, new());
                    if (only != null && !only.Contains(s.Name)) continue;
                    try { repl[info.PathId] = Cached(inst, info, () => NeedsGeometry(bf) ? NoPrograms(bf) : MetalShader.Convert(d, s, problems)); shaders++; }
                    catch (Exception e) { problems.Add($"{key}:{info.PathId} {s.Name}: {e.Message}"); }
                }
                foreach (var info in inst.file.GetAssetsOfType(AssetClassID.ComputeShader))
                {
                    if (only != null && !only.Contains(d.Am.GetBaseField(inst, info)["m_Name"].AsString)) continue;
                    try { repl[info.PathId] = Cached(inst, info, () => MetalCompute.Convert(d, inst, info)); computes++; }
                    catch (Exception e) { problems.Add($"{key}:{info.PathId} compute: {e.Message}"); }
                }
                if (key == "globalgamemanagers") PatchGraphicsSettings(d, inst, repl);
                if (d.Bundle == null)
                {
                    var outFile = key == "unity_builtin_extra" ? Path.Combine(outPath, "Resources", key) : Path.Combine(outPath, key);
                    Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
                    File.WriteAllBytes(outFile, SerializedWriter.WriteSerialized(d.Raw(key), inst, repl, SerializedWriter.StandaloneOSX));
                }
                else if (repl.Count > 0) nodeRepl[d.NodePath[key]] = SerializedWriter.WriteSerialized(d.Raw(key), inst, repl, SerializedWriter.StandaloneOSX);
                if (repl.Count > 0) Console.WriteLine($"{key}: {repl.Count} objects replaced ({sw.Elapsed.TotalSeconds:F0}s)");
            }
            if (Mark.Any) Console.WriteLine($"LMC_MARK: {Mark.Applied} fragment programs marked");
            Console.WriteLine($"{Path.GetFileName(gameData)}: {shaders} shaders and {computes} compute shaders done ({(cache == null ? "no cache" : $"{cache.Hits} reused, {cache.Misses} translated")}), {problems.Count} problems, {sw.Elapsed.TotalSeconds:F0}s");
            foreach (var p in problems) Console.WriteLine("  " + p);
            if (problems.Count > 0) return 1;
            if (d.Bundle != null)
            {
                // every other serialized file (scenes included) only changes its target platform: patch the 4 bytes
                var patch = new Dictionary<string, (long, byte[])>();
                var head = new byte[512];
                foreach (var n in d.Bundle.Nodes.Where(n => (n.Flags & 4) != 0 && !nodeRepl.ContainsKey(n.Path)))
                {
                    d.Bundle.ReadData(n.Offset, head.AsSpan(0, (int)Math.Min(head.Length, n.Size)));
                    var (at, plat) = SerializedWriter.PlatformField(head);
                    if (plat != SerializedWriter.StandaloneWindows64) throw new Exception($"{n.Path}: target platform {plat}, expected {SerializedWriter.StandaloneWindows64}");
                    patch[n.Path] = (at, BitConverter.GetBytes(SerializedWriter.StandaloneOSX));
                }
                if (Path.GetFullPath(outPath) == Path.GetFullPath(d.Bundle.FilePath)) throw new Exception("refusing to write the bundle in place");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                d.Bundle.Write(outPath, nodeRepl, patch: patch);
                bundleIn = d.Bundle.FilePath;
                Console.WriteLine($"wrote {outPath}: {nodeRepl.Count} files rewritten, {patch.Count} platform-patched ({sw.Elapsed.TotalSeconds:F0}s)");
            }
        }
        return bundleIn == null ? 0 : MetalCheck.Run(bundleIn, outPath, Environment.GetEnvironmentVariable("LMC_CHECK_MSL"));
    }

    static bool NeedsGeometry(AssetTypeValueField bf) =>
        bf["m_ParsedForm"]["m_SubShaders"]["Array"].Children.SelectMany(ss => ss["m_Passes"]["Array"].Children)
            .Any(p => p["progGeometry"]["m_PlayerSubPrograms"]["Array"].Children.Any(t => t["Array"].Children.Count > 0));

    // Metal has no geometry stage. Unity's own Metal build keeps such a shader (only Hidden/VR/BlitFromTex2DToTexArraySlice
    // here) with every pass's program lists emptied, its names and shared parameters dropped and an empty blob.
    static byte[] NoPrograms(AssetTypeValueField bf)
    {
        foreach (var p in bf["m_ParsedForm"]["m_SubShaders"]["Array"].Children.SelectMany(ss => ss["m_Passes"]["Array"].Children))
        {
            MetalShader.SetArray(p["m_NameIndices"], Array.Empty<AssetTypeValueField>());
            foreach (var prog in MetalShader.Progs)
            {
                foreach (var list in new[] { "m_PlayerSubPrograms", "m_ParameterBlobIndices" })
                    foreach (var tier in p[prog][list]["Array"].Children) MetalShader.SetArray(tier, Array.Empty<AssetTypeValueField>());
                foreach (var c in p[prog]["m_CommonParameters"].Children) MetalShader.SetArray(c, Array.Empty<AssetTypeValueField>());
            }
        }
        MetalShader.OnePlatform(bf, bf["platforms"]["Array"].Children.Select(x => x.AsInt).ToList().IndexOf(MetalShader.PlatformD3D11));
        MetalShader.WriteBlob(bf, new());
        return bf.WriteToByteArray();
    }

    // BuildSettings.m_GraphicsAPIs: PEAK's [Direct3D12 18, Direct3D11 2, Vulkan 21] -> [Metal 16]; the player picks
    // its renderer from this list.
    // GraphicsSettings.m_ShaderDefinesPerShaderCompiler: the D3D11 (4) and Vulkan (18) entries become one Metal (14)
    // entry. Words are BuiltinShaderDefine bits (bit n = word n/32, bit n%32), numbering from 6000.3's
    // UnityEngine.CoreModule. Derived, docs/metal-format-6.3.md section 7:
    // - D3D [142984712, 64]: word 0 = this project's settings + SHADER_API_DESKTOP; word 1 bit 6 = 38 DEPTH_FETCH.
    // - What a 6000.x Mac (Metal) build writes for the same word-0 bits: a 6000.1.10f1 URP Mac player build writes
    //   [142984728, 8] in 6000.1's numbering = +bit 4 FRAMEBUFFER_FETCH_AVAILABLE, +bit 35 NEEDS_RENDERPASS_FBFETCH_FALLBACK,
    //   no DEPTH_FETCH (bit 36 there); Lethal's 2022.3 Mac build wrote the same. 6000.3 inserted GLES31/GLES32 at 32/33,
    //   so FBFETCH_FALLBACK is bit 37 = word 1 value 32 -> Metal [142984728, 32].
    // Anything else stops the run.
    static void PatchGraphicsSettings(DataDir d, AssetsFileInstance inst, Dictionary<long, byte[]> repl)
    {
        var bs = inst.file.GetAssetsOfType(AssetClassID.BuildSettings).Single();
        var b = d.Am.GetBaseField(inst, bs);
        var apis = b["m_GraphicsAPIs"]["Array"].Children;
        if (string.Join(",", apis.Select(a => a.AsInt)) != "18,2,21") throw new Exception($"m_GraphicsAPIs: expected [18,2,21], found [{string.Join(",", apis.Select(a => a.AsInt))}]");
        apis[0].AsInt = MetalApi;
        MetalShader.SetArray(b["m_GraphicsAPIs"], new[] { apis[0] });
        repl[bs.PathId] = b.WriteToByteArray();

        var gs = inst.file.GetAssetsOfType(AssetClassID.GraphicsSettings).Single();
        var g = d.Am.GetBaseField(inst, gs);
        var defs = g["m_ShaderDefinesPerShaderCompiler"]["Array"].Children;
        if (string.Join(",", defs.Select(x => x["shaderPlatform"].AsInt)) != "4,18") throw new Exception("m_ShaderDefinesPerShaderCompiler: expected D3D11 and Vulkan entries");
        foreach (var e in defs)
            foreach (var tier in Tiers)
                if (string.Join(",", e[tier]["Array"].Children.Select(x => x.AsUInt)) != "142984712,64") throw new Exception($"{tier} of platform {e["shaderPlatform"].AsInt}: unexpected defines {string.Join(",", e[tier]["Array"].Children.Select(x => x.AsUInt))}");
        var m = defs[0];
        m["shaderPlatform"].AsInt = MetalShader.PlatformMetal;
        foreach (var tier in Tiers) { var w = m[tier]["Array"].Children; w[0].AsUInt = MetalDefines[0]; w[1].AsUInt = MetalDefines[1]; }
        MetalShader.SetArray(g["m_ShaderDefinesPerShaderCompiler"], new[] { m });
        repl[gs.PathId] = g.WriteToByteArray();
    }

    public const int MetalApi = 16;
    public static readonly uint[] MetalDefines = { 142984728, 32 };
    public static readonly string[] Tiers = { "defines_Tier1", "defines_Tier2", "defines_Tier3" };
}

// Translated objects on disk, keyed by sha256(tool binaries, original object bytes).
sealed class ObjectCache
{
    readonly string dir;
    readonly byte[] version;
    readonly HashSet<string> used = new();
    public int Hits, Misses;

    public ObjectCache(string dir)
    {
        this.dir = dir;
        Directory.CreateDirectory(dir);
        // any change to lmc, AssetsTools or the translator library gives every object a new key
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in Directory.GetFiles(AppContext.BaseDirectory).Where(f => f.EndsWith(".dll") || f.EndsWith(".dylib")).Order())
            h.AppendData(File.ReadAllBytes(f));
        version = h.GetHashAndReset();
    }

    public string Key(byte[] original) => Convert.ToHexStringLower(SHA256.HashData(version.Concat(original).ToArray()));

    public bool TryGet(string key, out byte[] bytes)
    {
        var path = Path.Combine(dir, key + ".bin");
        used.Add(key);
        if (File.Exists(path)) { bytes = File.ReadAllBytes(path); Hits++; return true; }
        bytes = Array.Empty<byte>();
        Misses++;
        return false;
    }

    public void Put(string key, byte[] bytes)
    {
        var tmp = Path.Combine(dir, key + ".tmp");
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, Path.Combine(dir, key + ".bin"), true);
    }

    public void Prune()
    {
        foreach (var f in Directory.GetFiles(dir))
            if (!used.Contains(Path.GetFileNameWithoutExtension(f))) File.Delete(f);
    }
}
