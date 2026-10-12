namespace Lmc;

// Decides how each constant buffer is declared for Metal, once per shader (or compute shader), so that:
// - buffers whose memory comes from C# in a fixed layout (HDRP's ShaderVariablesGlobal, UnityPerDraw, ...)
//   are read at the same byte offsets as on D3D11. Unity's own Metal build declares the full struct, whose
//   natural Metal layout equals the D3D one for these float4-aligned structs.
// - every pass of a shader sees the same layout (the SRP batcher requires that of UnityPerMaterial).
// Unity's player data only lists the values each program uses, so the layout is the union over all programs.
// If those values sit at their D3D offsets under Metal alignment, the gaps are padded and the D3D layout is
// kept exactly. Otherwise, e.g. a float2 packed at offset 4, the buffer gets Metal's natural layout of the
// union, which Unity's reflection data then describes.
sealed class CbLayouts
{
    public const string PadPrefix = "_lmcpad";
    readonly bool compute;
    readonly Dictionary<string, (int size, Dictionary<string, Param> vars)> union = new();
    readonly HashSet<string> conflicted = new(); // laid out differently by different variants (e.g. stereo builtins)
    Dictionary<string, (int size, List<Param> vars)>? built;

    public CbLayouts(bool compute) => this.compute = compute;

    public void Add(string name, int size, IEnumerable<Param> vars)
    {
        if (!union.TryGetValue(name, out var u)) union[name] = u = (size, new());
        else if (size > u.size) union[name] = u = (size, u.vars);
        foreach (var v in vars)
            if (u.vars.TryGetValue(v.Name, out var old) && old != v) conflicted.Add(name);
            else u.vars[v.Name] = v;
    }

    // The shared layout; a cbuffer whose variants disagree is laid out per program from its own values instead.
    public (int size, List<Param> vars) Get(string name, int size, IEnumerable<Param> own)
    {
        if (built == null) Freeze();
        return built!.TryGetValue(name, out var l) ? l : Build(size, own);
    }

    // Dev: how each cbuffer is laid out: "d3d" (D3D offsets kept), "natural" (values don't fit Metal alignment at their
    // D3D offsets) or "per-program" (variants disagree).
    public IEnumerable<(string name, string how)> Describe() => union.Select(kv => (kv.Key,
        conflicted.Contains(kv.Key) ? "per-program" : IsPreservable(kv.Value.vars.Values.OrderBy(v => v.Index).ToList()) ? "d3d" : "natural"));

    public void Freeze() => built = union.Where(kv => !conflicted.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => Build(kv.Value.size, kv.Value.vars.Values));

    (int, List<Param>) Build(int size, IEnumerable<Param> vars)
    {
        var sorted = vars.OrderBy(v => v.Index).ToList();
        if (!IsPreservable(sorted)) return (size, sorted);
        var outv = new List<Param>();
        int cur = 0, n = 0;
        void Pad(int to)
        {
            while (cur < to)
            {
                var wide = cur % 16 == 0 && to - cur >= 16;
                outv.Add(new Param($"{PadPrefix}{n++}", 5, 1, wide ? 4 : 1, false, 0, cur));
                cur += wide ? 16 : 4;
            }
        }
        foreach (var v in sorted)
        {
            Pad(v.Index);
            outv.Add(v);
            cur = v.Index + Metal(v, compute).size;
        }
        Pad((size + 15) / 16 * 16);
        return (size, outv);
    }

    bool IsPreservable(IEnumerable<Param> vars)
    {
        var end = 0;
        foreach (var v in vars.OrderBy(v => v.Index))
        {
            var (msize, align) = Metal(v, compute);
            if (v.Index % align != 0 || v.Index < end) return false;
            if (v.Array > 1 && !v.Matrix && !compute && v.Cols < 3) return false; // Metal strides scalar/float2 arrays by 4/8, D3D by 16
            end = v.Index + msize;
        }
        return true;
    }

    // Size and alignment of a value as HLSLcc declares it in a Metal struct (TRANSLATE_MATRICES: a matrix is a
    // float4 array, one per column; in compute shaders every array element is a 4-vector).
    public static (int size, int align) Metal(Param p, bool compute)
    {
        var n = Math.Max(p.Array, 1);
        if (p.Matrix) return (16 * p.Cols * n, 16);
        if (compute && p.Array > 1) return (16 * n, 16);
        var (one, align) = p.Cols switch { 1 => (4, 4), 2 => (8, 8), 3 or 4 => (16, 16), _ => throw new Exception($"{p.Name}: vector of {p.Cols}") };
        return (one * n, align);
    }
}
