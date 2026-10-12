using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

record ShaderRef(string File, long PathId, string Name, List<int> Platforms);

// The serialized files of a player build, loaded with AssetsTools.NET and a class database (player builds carry no
// type trees). Source: a Data folder of loose files, or a UnityFS bundle (PEAK packs everything into
// Data/data.unity3d; pass the bundle or the folder holding it). Scene files (levelN) are skipped unless levels=true.
sealed class DataDir : IDisposable
{
    public readonly AssetsManager Am = new();
    public readonly Dictionary<string, AssetsFileInstance> Files = new();
    public readonly Bundle? Bundle;
    readonly Dictionary<string, byte[]> raw = new();      // file key -> original bytes
    public readonly Dictionary<string, string> NodePath = new(); // file key -> path inside the bundle

    public DataDir(string path, bool levels = false)
    {
        Am.LoadClassPackage(Tpk());
        if (Directory.Exists(path) && File.Exists(Path.Combine(path, "data.unity3d"))) path = Path.Combine(path, "data.unity3d");
        if (File.Exists(path))
        {
            Bundle = new Bundle(path);
            foreach (var n in Bundle.Nodes.Where(n => (n.Flags & 4) != 0 && IsSerialized(n.Path) && (levels || !Path.GetFileName(n.Path).StartsWith("level"))))
            {
                var key = Path.GetFileName(n.Path);
                NodePath[key] = n.Path;
                Load(key, Bundle.Read(n));
            }
        }
        else
            foreach (var p in Directory.EnumerateFiles(path).Concat(Directory.EnumerateFiles(Path.Combine(path, "Resources")).Where(p => Path.GetFileName(p) == "unity_builtin_extra")))
                if (IsSerialized(p) && (levels || !Path.GetFileName(p).StartsWith("level"))) Load(Path.GetFileName(p), File.ReadAllBytes(p));
    }

    void Load(string key, byte[] bytes)
    {
        raw[key] = bytes;
        var inst = Am.LoadAssetsFile(new MemoryStream(bytes, false), key, false);
        Am.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);
        Files[key] = inst;
    }

    public byte[] Raw(string key) => raw[key];

    public static byte[] ObjectBytes(AssetsFileInstance inst, AssetFileInfo info)
    {
        lock (inst.file.Reader)
        {
            inst.file.Reader.Position = info.GetAbsoluteByteOffset(inst.file);
            return inst.file.Reader.ReadBytes((int)info.ByteSize);
        }
    }

    // Unity serialized files have no extension (levelN, globalgamemanagers) or end in .assets
    static bool IsSerialized(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".assets") || name.StartsWith("level") || name is "globalgamemanagers" or "unity_builtin_extra") return !name.Contains('.') || name.EndsWith(".assets");
        return false;
    }

    // Class database (AssetRipper TPK format). LMC_TPK overrides; default <cache>/classdata.tpk.
    internal static string Tpk() => Environment.GetEnvironmentVariable("LMC_TPK") ?? Path.Combine(Environment.GetEnvironmentVariable("LMC_CACHE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Caches/peak-mac-converter"), "classdata.tpk");

    public AssetTypeValueField Base(string file, long pid)
    {
        var inst = Files[file];
        lock (inst.file.Reader) return Am.GetBaseField(inst, inst.file.GetAssetInfo(pid));
    }

    public IEnumerable<ShaderRef> Shaders()
    {
        foreach (var (key, inst) in Files)
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Shader))
            {
                var bf = Am.GetBaseField(inst, info);
                yield return new ShaderRef(key, info.PathId, bf["m_ParsedForm"]["m_Name"].AsString, bf["platforms"]["Array"].Children.Select(p => p.AsInt).ToList());
            }
    }

    public void Dispose() { Am.UnloadAll(); Bundle?.Dispose(); }
}
