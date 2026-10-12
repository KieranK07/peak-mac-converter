using System.Text;

namespace Lmc;

// Unity's parameter blob entry for a Metal subprogram (2022.3, unchanged in 6000.3), built from HLSLcc's reflection records.
// Value offsets are Metal struct offsets: HLSLcc declares each cbuffer as a struct holding only the members the
// shader uses, in order, so the offsets follow Metal's alignment rules rather than D3D's register packing.
static class MetalParams
{
    const int BlobVersion = 202012090;

    record Value(string Name, int Type, int Rows, int Cols, bool Matrix, int Array, int Offset);
    record StructValue(string Name, int Offset, int Array, int Size, List<Value> Members);
    sealed class Cb { public string Name = ""; public int Size; public List<Value> Values = new(); public List<StructValue> Structs = new(); }

    public static byte[] Build(List<string[]> refl)
    {
        var cbs = new List<Cb>();
        var binds = new List<(string name, int type, int a, int b, uint c)>();
        Cb? cur = null;
        int off = 0, maxAlign = 4;
        var members = new List<Value>(); int memberOff = 0, memberAlign = 4; // pending array-of-struct members
        void Close()
        {
            if (cur == null) return;
            cur.Size = off;  // end of the last member, not rounded to the alignment (as Unity writes it, 2022.3 and 6.3)
            cbs.Add(cur);
            cur = null;
        }
        foreach (var l in refl)
        {
            if (cur != null && l[0] != "const") Close();
            switch (l[0])
            {
                case "cb":
                    cur = new Cb { Name = l[1] };
                    off = 0; maxAlign = 4;
                    break;
                case "const" when l[1].Contains("[]."):
                {
                    // member of an array of structs (instancing buffers); HLSLcc reports the struct itself after its members
                    int svt = int.Parse(l[3]), rows = int.Parse(l[4]), cols = int.Parse(l[5]), arr = int.Parse(l[7]);
                    var matrix = l[6] == "1";
                    var (size, align) = Layout(rows, cols, matrix, arr);
                    memberOff = Round(memberOff, align);
                    memberAlign = Math.Max(memberAlign, align);
                    members.Add(new Value(l[1][(l[1].IndexOf("[].") + 3)..], UnityType(svt, l[1]), rows, cols, matrix, arr, memberOff));
                    memberOff += size;
                    break;
                }
                case "const" when l[3] == "0":
                {
                    // the array of structs: svt void, element count in arraySize
                    if (members.Count == 0) throw new Exception($"struct {l[1]} without members");
                    var size = Round(memberOff, memberAlign);
                    off = Round(off, memberAlign);
                    maxAlign = Math.Max(maxAlign, memberAlign);
                    var n = Math.Max(int.Parse(l[7]), 1);
                    cur!.Structs.Add(new StructValue(l[1], off, int.Parse(l[7]), size, members));
                    off += size * n;
                    members = new(); memberOff = 0; memberAlign = 4;
                    break;
                }
                case "const":
                {
                    // const name d3dOffset svt rows cols isMatrix arraySize isUsed
                    if (l[1].Contains('.')) throw new Exception($"struct value {l[1]} (plain structs are not supported)");
                    int svt = int.Parse(l[3]), rows = int.Parse(l[4]), cols = int.Parse(l[5]), arr = int.Parse(l[7]);
                    var matrix = l[6] == "1";
                    var (size, align) = Layout(rows, cols, matrix, arr);
                    off = Round(off, align);
                    maxAlign = Math.Max(maxAlign, align);
                    if (!l[1].StartsWith(CbLayouts.PadPrefix)) cur!.Values.Add(new Value(l[1], UnityType(svt, l[1]), rows, cols, matrix, arr, off));
                    off += size;
                    break;
                }
                case "cbbind": binds.Add((l[1], 1, int.Parse(l[2]), 0, 0u)); break;
                case "tex":
                {
                    // tex name bind sampler multisampled dim isUAV
                    if (l[1].StartsWith("_RandomWriteTarget")) break; // bound by index, like D3D: no parameter entry
                    int bind = int.Parse(l[2]), sampler = int.Parse(l[3]);
                    if (l[6] == "1") binds.Add((l[1], 3, bind, bind, 0u));
                    else binds.Add((l[1], 0, bind, sampler, (uint)((l[4] == "1" ? 1 : 0) | (UnityDim(int.Parse(l[5])) << 1))));
                    break;
                }
                case "buf":
                    if (l[1].StartsWith("_RandomWriteTarget")) break;
                    binds.Add(l[3] == "1" ? (l[1], 3, int.Parse(l[2]), int.Parse(l[2]), 0u) : (l[1], 2, int.Parse(l[2]), 0, 0u));
                    break;
            }
        }
        Close();
        // A tessellated program's stages report each shared cbuffer again, without values; the stage that declared
        // it (and bound it) has them.
        cbs = cbs.GroupBy(c => c.Name).Select(g => g.MaxBy(c => c.Values.Count + c.Structs.Count)!).ToList();
        binds = binds.Distinct().ToList();

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write(b.Length); w.Write(b); while (ms.Length % 4 != 0) w.Write((byte)0); }
        w.Write(BlobVersion);
        w.Write(cbs.Count + 1);
        Str(""); w.Write(0); w.Write(0); w.Write(0); // the nameless base table: loose values (none on Metal)
        foreach (var cb in cbs)
        {
            Str(cb.Name); w.Write(cb.Size); w.Write(cb.Values.Count);
            void Val(Value v) { Str(v.Name); w.Write(v.Type); w.Write(v.Rows); w.Write(v.Cols); w.Write(v.Matrix ? 1 : 0); w.Write(v.Array); w.Write(v.Offset); }
            foreach (var v in cb.Values) Val(v);
            w.Write(cb.Structs.Count);
            foreach (var st in cb.Structs)
            {
                Str(st.Name); w.Write(st.Offset); w.Write(st.Array); w.Write(st.Size); w.Write(st.Members.Count);
                foreach (var m in st.Members) Val(m);
            }
        }
        w.Write(binds.Count);
        foreach (var (name, type, a, b, c) in binds)
        {
            Str(name); w.Write(type);
            w.Write(a);
            if (type == 0) { w.Write(b); w.Write(c); } else w.Write(b);
        }
        return ms.ToArray();
    }

    static int Round(int x, int a) => (x + a - 1) / a * a;

    // Size and alignment of a member as HLSLcc declares it for Metal (non-compute stages):
    // matrices become float4 arrays; vectors use Metal's float2/float3/float4 (float3 occupies 16 bytes).
    static (int size, int align) Layout(int rows, int cols, bool matrix, int arr)
    {
        var n = Math.Max(arr, 1);
        if (matrix) return (16 * cols * n, 16);
        var (one, align) = cols switch { 1 => (4, 4), 2 => (8, 8), 3 or 4 => (16, 16), _ => throw new Exception($"vector of {cols}") };
        return (one * n, align);
    }

    // HLSLcc SHADER_VARIABLE_TYPE -> Unity parameter type (0 float, 1 int, 5 uint). Bools arrive as int.
    static int UnityType(int svt, string name) => svt switch
    {
        3 => 0, 2 => 1, 19 => 5, 1 => 1,
        _ => throw new Exception($"constant {name}: variable type {svt}"),
    };

    // HLSLcc texture dimension -> Unity TextureDimension (2D 2, 3D 3, Cube 4, 2DArray 5, CubeArray 6).
    static int UnityDim(int td) => td switch
    {
        2 or 5 => 2, 3 => 3, 4 => 4, 6 => 5, 7 => 6,
        _ => throw new Exception($"texture dimension {td}"),
    };
}
