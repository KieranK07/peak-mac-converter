using System.Text;
using AssetsTools.NET;

namespace Lmc;

// Unity's per-subprogram parameter tables (2022.3 player format), as stored in a parameter blob entry or in a
// program's m_CommonParameters. Index = byte offset inside the cbuffer for values, register/slot for bindings.
record Param(string Name, int Type, int Rows, int Cols, bool Matrix, int Array, int Index);
record StructParam(string Name, int Index, int Array, int Size, List<Param> Members);
class Cb
{
    public string Name = ""; public int Size; public bool Partial;
    public List<Param> Params = new(); public List<StructParam> Structs = new();
}
record Tex(string Name, int Index, int Sampler, uint Extra);
record Bind(string Name, int Index, int Array);
record Sampler(int Bind, uint State);

sealed class Params
{
    public List<Cb> Cbs = new();
    public List<Tex> Textures = new();
    public List<Bind> CbBinds = new(), Buffers = new(), Uavs = new();
    public List<Sampler> Samplers = new();

    static string Str(BinaryReader r)
    {
        var s = Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));
        r.BaseStream.Position = (r.BaseStream.Position + 3) & ~3L;
        return s;
    }

    static Param ReadParam(BinaryReader r) => new(Str(r), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32() > 0, r.ReadInt32(), r.ReadInt32());

    public static Params FromBlob(byte[] e)
    {
        var p = new Params();
        var r = new BinaryReader(new MemoryStream(e));
        r.ReadInt32(); // blob version
        var ncb = r.ReadInt32();
        for (var c = 0; c < ncb; c++)
        {
            var cb = new Cb { Name = Str(r), Size = r.ReadInt32() };
            var np = r.ReadInt32();
            for (var i = 0; i < np; i++) cb.Params.Add(ReadParam(r));
            var ns = r.ReadInt32();
            for (var i = 0; i < ns; i++)
            {
                var name = Str(r); var idx = r.ReadInt32(); var arr = r.ReadInt32(); var size = r.ReadInt32(); var n = r.ReadInt32();
                cb.Structs.Add(new StructParam(name, idx, arr, size, Enumerable.Range(0, n).Select(_ => ReadParam(r)).ToList()));
            }
            p.Cbs.Add(cb);
        }
        var nb = r.ReadInt32();
        for (var i = 0; i < nb; i++)
        {
            var name = Str(r);
            switch (r.ReadInt32())
            {
                case 0: p.Textures.Add(new Tex(name, r.ReadInt32(), r.ReadInt32(), r.ReadUInt32())); break;
                case 1: p.CbBinds.Add(new Bind(name, r.ReadInt32(), r.ReadInt32())); break;
                case 2: p.Buffers.Add(new Bind(name, r.ReadInt32(), r.ReadInt32())); break;
                case 3: p.Uavs.Add(new Bind(name, r.ReadInt32(), r.ReadInt32())); break;
                case 4: p.Samplers.Add(new Sampler(r.ReadInt32(), r.ReadUInt32())); break;
                case var t: throw new Exception($"binding type {t}");
            }
        }
        if (r.BaseStream.Position != e.Length) throw new Exception($"parameter blob: {e.Length - r.BaseStream.Position} trailing bytes");
        return p;
    }

    // m_CommonParameters (SerializedProgramParameters); names are indices into m_ParsedForm.m_NameIndices.
    public static Params FromCommon(AssetTypeValueField f, Dictionary<int, string> names)
    {
        var p = new Params();
        IEnumerable<AssetTypeValueField> A(AssetTypeValueField x) => x["Array"].Children;
        string N(AssetTypeValueField x) => names[x["m_NameIndex"].AsInt];
        Param Vec(AssetTypeValueField x) => new(N(x), x["m_Type"].AsInt, 1, x["m_Dim"].AsSByte, false, x["m_ArraySize"].AsInt, x["m_Index"].AsInt);
        Param Mat(AssetTypeValueField x) => new(N(x), x["m_Type"].AsInt, x["m_RowCount"].AsSByte, x["m_ColumnCount"].IsDummy ? x["m_RowCount"].AsSByte : x["m_ColumnCount"].AsSByte, true, x["m_ArraySize"].AsInt, x["m_Index"].AsInt);
        if (A(f["m_VectorParams"]).Any() || A(f["m_MatrixParams"]).Any()) throw new Exception("common top-level vector/matrix params (unexpected on D3D11)");
        foreach (var c in A(f["m_ConstantBuffers"]))
        {
            var cb = new Cb { Name = N(c), Size = c["m_Size"].AsInt, Partial = c["m_IsPartialCB"].AsBool };
            cb.Params.AddRange(A(c["m_MatrixParams"]).Select(Mat));
            cb.Params.AddRange(A(c["m_VectorParams"]).Select(Vec));
            foreach (var s in A(c["m_StructParams"]))
                cb.Structs.Add(new StructParam(N(s), s["m_Index"].AsInt, s["m_ArraySize"].AsInt, s["m_StructSize"].AsInt,
                    A(s["m_MatrixMembers"]).Select(Mat).Concat(A(s["m_VectorMembers"]).Select(Vec)).ToList()));
            p.Cbs.Add(cb);
        }
        foreach (var t in A(f["m_TextureParams"]))
            p.Textures.Add(new Tex(N(t), t["m_Index"].AsInt, t["m_SamplerIndex"].AsInt, (uint)((t["m_MultiSampled"].AsBool ? 1 : 0) | (t["m_Dim"].AsSByte << 1))));
        foreach (var b in A(f["m_ConstantBufferBindings"])) p.CbBinds.Add(new Bind(N(b), b["m_Index"].AsInt, b["m_ArraySize"].AsInt));
        foreach (var b in A(f["m_BufferParams"])) p.Buffers.Add(new Bind(N(b), b["m_Index"].AsInt, b["m_ArraySize"].AsInt));
        foreach (var b in A(f["m_UAVParams"])) p.Uavs.Add(new Bind(N(b), b["m_Index"].AsInt, b["m_OriginalIndex"].AsInt));
        foreach (var s in A(f["m_Samplers"])) p.Samplers.Add(new Sampler(s["bindPoint"].AsInt, s["sampler"].AsUInt));
        return p;
    }

    // This subprogram's view: its own tables plus the program's common ones. A partial common cbuffer adds its
    // values to the subprogram's cbuffer of the same name.
    public Params Merge(Params common)
    {
        var p = new Params();
        foreach (var cb in Cbs)
        {
            var copy = new Cb { Name = cb.Name, Size = cb.Size, Params = new(cb.Params), Structs = new(cb.Structs) };
            foreach (var part in common.Cbs.Where(c => c.Partial && c.Name == cb.Name)) { copy.Params.AddRange(part.Params); copy.Structs.AddRange(part.Structs); }
            p.Cbs.Add(copy);
        }
        p.Cbs.AddRange(common.Cbs.Where(c => !c.Partial && Cbs.All(x => x.Name != c.Name)));
        p.Textures = Textures.Concat(common.Textures).ToList();
        p.CbBinds = CbBinds.Concat(common.CbBinds).ToList();
        p.Buffers = Buffers.Concat(common.Buffers).ToList();
        p.Uavs = Uavs.Concat(common.Uavs).ToList();
        p.Samplers = Samplers.Concat(common.Samplers).ToList();
        return p;
    }

    // This program with each cbuffer holding every value any of `group` reads from it (the stages of one tessellated
    // program share one struct per cbuffer, declared by the first stage that uses it).
    public Params WithCbsOf(IEnumerable<Params> group)
    {
        var all = group.SelectMany(g => g.Cbs).ToList();
        var p = (Params)MemberwiseClone();
        p.Cbs = Cbs.Select(cb =>
        {
            var same = all.Where(c => c.Name == cb.Name).ToList();
            return new Cb
            {
                Name = cb.Name, Size = same.Max(c => c.Size), Partial = cb.Partial,
                Params = same.SelectMany(c => c.Params).DistinctBy(v => v.Name).ToList(),
                Structs = same.SelectMany(c => c.Structs).DistinctBy(v => v.Name).ToList(),
            };
        }).ToList();
        return p;
    }

    // Unity's inline sampler state: bits 0-1 filter (point/linear/trilinear), then 2 bits each for wrap U, V, W
    // (repeat/clamp/mirror/mirroronce), bit 8 depth compare. HLSLcc turns a sampler *name* containing those
    // words into the matching constexpr sampler, as Unity's own Metal compile does.
    // Two registers can carry the same state (e.g. s_point_clamp_sampler and sampler_PointClamp in the source);
    // `used` keeps their Metal names apart, since HLSLcc declares one constexpr sampler per register.
    public static string InlineSamplerName(uint state, HashSet<string> used)
    {
        if (state >> 9 != 0 || (state & 3) == 3) throw new Exception($"unsupported inline sampler state 0x{state:x}");
        string[] filters = { "point", "linear", "trilinear" }, wraps = { "repeat", "clamp", "mirror", "mirroronce" };
        uint u = (state >> 2) & 3, v = (state >> 4) & 3, w = (state >> 6) & 3;
        var wrap = u == v && v == w ? wraps[u] : $"{wraps[u]}u_{wraps[v]}v_{wraps[w]}w";
        var name = $"s_{filters[state & 3]}_{wrap}{((state & 0x100) != 0 ? "_compare" : "")}_sampler";
        return used.Add(name) ? name : $"{name}_{used.Count}";
    }

    // Description text for xlat's RDEF rebuild (record formats in tools/xlat/rdef.cpp).
    // Feed this program's cbuffers into the shader-wide layout decision (cbuffers holding structs keep their own).
    public void AddTo(CbLayouts layouts)
    {
        foreach (var cb in Cbs.Where(c => c.Structs.Count == 0 && c.Name != "")) layouts.Add(cb.Name, cb.Size, cb.Params);
    }

    public string ToDesc(CbLayouts layouts)
    {
        var sb = new StringBuilder();
        void Line(params object[] f) => sb.Append(string.Join('\t', f)).Append('\n');
        foreach (var b in CbBinds)
        {
            var cb = Cbs.FirstOrDefault(c => c.Name == b.Name) ?? throw new Exception($"cbuffer binding '{b.Name}' has no layout");
            var (size, vars) = cb.Structs.Count == 0 ? layouts.Get(cb.Name, cb.Size, cb.Params) : (cb.Size, cb.Params);
            Line("cb", b.Index, cb.Name, size);
            foreach (var v in vars) Line(VarRecord("v", b.Index, v));
            foreach (var s in cb.Structs)
            {
                Line("v", b.Index, s.Name, s.Index, 5, 0, 0, 0, s.Array);
                Line("ss", b.Index, s.Size);
                foreach (var m in s.Members) Line(VarRecord("sv", b.Index, m));
            }
        }
        // $Globals has no binding entry when it is the only cbuffer at register 0? Unity always emits one; checked by xlat.
        foreach (var t in Textures) Line("t", t.Index, t.Name);
        foreach (var b in Buffers) Line("t", b.Index, b.Name);
        foreach (var u in Uavs) Line("u", u.Index, u.Name);
        var used = new HashSet<string>();
        foreach (var s in Samplers)
            Line("s", s.Bind, InlineSamplerName(s.State, used));
        foreach (var t in Textures.Where(t => t.Sampler >= 0 && Samplers.All(s => s.Bind != t.Sampler)).GroupBy(t => t.Sampler))
            Line("s", t.Key, "sampler" + t.First().Name);
        return sb.ToString();
    }

    // Unity type 0 = float, 1 = int (uint is reported as int too); D3D classes: 0 scalar, 1 vector, 3 matrix (column-major).
    public static string VarRecord(string kind, int reg, Param v)
    {
        var svt = v.Type switch { 0 => 3, 1 => 2, 2 => 1, 5 => 19, _ => throw new Exception($"param type {v.Type} ({v.Name})") };
        var cls = v.Matrix ? 3 : v.Cols > 1 ? 1 : 0;
        return string.Join('\t', kind, reg, v.Name, v.Index, cls, svt, v.Rows, v.Cols, v.Array);
    }
}
