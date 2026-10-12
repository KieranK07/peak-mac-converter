using System.Text;
using AssetsTools.NET;

namespace Lmc;

// Dev command: translate every D3D11 subprogram of one shader and report failures.
static class XlatCmd
{
    static readonly string[] Progs = { "progVertex", "progFragment", "progGeometry", "progHull", "progDomain", "progRayTracing" };

    public static void Run(DataDir d, ShaderRef s, string outDir, uint flags)
    {
        Directory.CreateDirectory(outDir);
        var bf = d.Base(s.File, s.PathId);
        var blob = new ShaderBlob(bf, 0);
        var pf = bf["m_ParsedForm"];
        int ok = 0, fail = 0;
        var log = new StringBuilder();
        int ssI = 0;
        foreach (var ss in pf["m_SubShaders"]["Array"].Children)
        {
            int pI = 0;
            foreach (var p in ss["m_Passes"]["Array"].Children)
            {
                var names = p["m_NameIndices"]["Array"].Children.ToDictionary(x => x["second"].AsInt, x => x["first"].AsString);
                foreach (var prog in Progs)
                {
                    var pg = p[prog];
                    var common = pg["m_CommonParameters"].IsDummy ? new Params() : Params.FromCommon(pg["m_CommonParameters"], names);
                    var outer = pg["m_PlayerSubPrograms"]["Array"].Children;
                    var parIdx = pg["m_ParameterBlobIndices"]["Array"].Children;
                    for (var o = 0; o < outer.Count; o++)
                    {
                        var inner = outer[o]["Array"].Children;
                        for (var i = 0; i < inner.Count; i++)
                        {
                            var sp = inner[i];
                            var sub = SubProgram.Read(blob.Entry((int)sp["m_BlobIndex"].AsUInt));
                            var par = Params.FromBlob(blob.Entry((int)parIdx[o]["Array"][i].AsUInt)).Merge(common);
                            var tag = $"{ssI}.{pI}.{prog}.{o}.{i}";
                            try
                            {
                                var lay = new CbLayouts(compute: false);
                                par.AddTo(lay);
                                var desc = par.ToDesc(lay);
                                if (Environment.GetEnvironmentVariable("LMC_XLAT_DEBUG") != null) { File.WriteAllText(Path.Combine(outDir, tag + ".desc"), desc); File.WriteAllBytes(Path.Combine(outDir, tag + ".dxbc"), Xlat.Dxbc(sub.Code)); }
                                var (good, msl, refl) = Xlat.Translate(Xlat.Dxbc(sub.Code), desc, flags);
                                if (!good || Environment.GetEnvironmentVariable("LMC_XLAT_WRITE") != null)
                                    File.WriteAllText(Path.Combine(outDir, tag + ".metal"), msl + "\n/* reflection\n" + refl + "*/\n/* desc\n" + desc + "*/\n");
                                if (good) ok++; else { fail++; log.AppendLine($"{tag}: {refl.Split('\n').FirstOrDefault(l => l.StartsWith("error"))}"); }
                            }
                            catch (Exception e) { fail++; log.AppendLine($"{tag}: {e.Message}"); }
                        }
                    }
                }
                pI++;
            }
            ssI++;
        }
        File.WriteAllText(Path.Combine(outDir, "xlat.txt"), log.ToString());
        Console.WriteLine($"{s.Name}: {ok} ok, {fail} failed");
        Console.Write(string.Join("\n", log.ToString().Split('\n').Take(10)));
    }
}

// A player subprogram blob entry (2022.3): version, gpu program type, 4 stats, keywords, code, vertex channels.
sealed class SubProgram
{
    public int Version, Type; public int[] Stats = new int[4]; public List<string> Keywords = new();
    public byte[] Code = Array.Empty<byte>(); public int SourceMap; public List<(int Source, int Target)> Channels = new();

    public static SubProgram Read(byte[] e)
    {
        var r = new BinaryReader(new MemoryStream(e));
        string Str() { var s = Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32())); Align(); return s; }
        void Align() => r.BaseStream.Position = (r.BaseStream.Position + 3) & ~3L;
        var sp = new SubProgram { Version = r.ReadInt32(), Type = r.ReadInt32() };
        for (var i = 0; i < 4; i++) sp.Stats[i] = r.ReadInt32();
        var nk = r.ReadInt32();
        for (var i = 0; i < nk; i++) sp.Keywords.Add(Str());
        sp.Code = r.ReadBytes(r.ReadInt32()); Align();
        sp.SourceMap = r.ReadInt32();
        var n = r.ReadInt32();
        for (var i = 0; i < n; i++) sp.Channels.Add((r.ReadInt32(), r.ReadInt32()));
        if (r.BaseStream.Position != e.Length) throw new Exception("subprogram: trailing bytes");
        return sp;
    }
}
