using System.Runtime.InteropServices;

namespace Lmc;

// P/Invoke into tools/xlat (HLSLcc + our RDEF rebuild), built as libxlat.dylib.
static class Xlat
{
    [DllImport("xlat")] static extern int lmc_xlat_unity(byte[] dxbc, string desc, uint flags, out IntPtr msl, out IntPtr reflection);
    [DllImport("xlat")] static extern int lmc_xlat_unity_tess(byte[] vs, string vsDesc, byte[] hs, string hsDesc, byte[] ds, string dsDesc, uint flags, out IntPtr msl, out IntPtr reflection);
    [DllImport("xlat")] static extern void lmc_xlat_free(IntPtr p);

    public static (bool ok, string msl, string refl) Translate(byte[] dxbc, string desc, uint flags)
    {
        var ok = lmc_xlat_unity(dxbc, desc, flags, out var m, out var r) != 0;
        return Take(ok, m, r);
    }

    // A tessellated pass (vertex, hull, domain) as one Metal program; reflection records per stage after "stage\t<n>".
    public static (bool ok, string msl, string refl) TranslateTess(byte[][] dxbc, string[] desc, uint flags)
    {
        var ok = lmc_xlat_unity_tess(dxbc[0], desc[0], dxbc[1], desc[1], dxbc[2], desc[2], flags, out var m, out var r) != 0;
        return Take(ok, m, r);
    }

    static (bool, string, string) Take(bool ok, IntPtr m, IntPtr r)
    {
        var res = (ok, Marshal.PtrToStringUTF8(m) ?? "", Marshal.PtrToStringUTF8(r) ?? "");
        lmc_xlat_free(m); lmc_xlat_free(r);
        return res;
    }

    // Unity's D3D11 program data: a small header, then the DXBC container.
    public static byte[] Dxbc(byte[] programData)
    {
        var at = programData.AsSpan().IndexOf("DXBC"u8);
        if (at < 0) throw new Exception("no DXBC in program data");
        return programData[at..];
    }
}
