// C ABI over Unity's open-source HLSLcc: DXBC in, Metal Shading Language + reflection records out.
// Reflection is one record per line, tab-separated, in callback order (see Reflect below).
#include <cstdlib>
#include <cstring>
#include <sstream>
#include <string>
#include "hlslcc.h"

namespace rdef {
struct Var; struct Desc; struct Decl;
}
#include "rdef.cpp"

namespace {
struct Reflect : HLSLccReflection
{
    std::ostringstream out;
    void OnDiagnostics(const std::string &e, int line, bool isError) override { out << (isError ? "error\t" : "diag\t") << line << '\t' << e << '\n'; }
    void OnInputBinding(const std::string &name, int bind) override { out << "input\t" << name << '\t' << bind << '\n'; }
    bool OnConstantBuffer(const std::string &name, size_t size, size_t members) override { out << "cb\t" << name << '\t' << size << '\t' << members << '\n'; return true; }
    bool OnConstant(const std::string &name, int bind, SHADER_VARIABLE_TYPE t, int rows, int cols, bool isMatrix, int arraySize, bool isUsed) override
    {
        out << "const\t" << name << '\t' << bind << '\t' << int(t) << '\t' << rows << '\t' << cols << '\t' << isMatrix << '\t' << arraySize << '\t' << isUsed << '\n';
        return true;
    }
    void OnConstantBufferBinding(const std::string &name, int bind) override { out << "cbbind\t" << name << '\t' << bind << '\n'; }
    void OnTextureBinding(const std::string &name, int bind, int sampler, bool ms, HLSLCC_TEX_DIMENSION dim, bool uav) override
    {
        out << "tex\t" << name << '\t' << bind << '\t' << sampler << '\t' << ms << '\t' << int(dim) << '\t' << uav << '\n';
    }
    void OnBufferBinding(const std::string &name, int bind, bool uav) override { out << "buf\t" << name << '\t' << bind << '\t' << uav << '\n'; }
    void OnThreadGroupSize(unsigned x, unsigned y, unsigned z) override { out << "threads\t" << x << '\t' << y << '\t' << z << '\n'; }
    void OnTessellationInfo(uint32_t part, uint32_t wind, uint32_t maxf, uint32_t patches) override { out << "tess\t" << part << '\t' << wind << '\t' << maxf << '\t' << patches << '\n'; }
    void OnTessellationKernelInfo(uint32_t n) override { out << "tesskernel\t" << n << '\n'; }
    void OnVertexProgramOutput(const std::string &name, const std::string &sem, int idx) override { out << "vout\t" << name << '\t' << sem << '\t' << idx << '\n'; }
    void OnBuiltinOutput(SPECIAL_NAME n) override { out << "builtin\t" << int(n) << '\n'; }
    void OnFragmentOutputDeclaration(int comps, int idx) override { out << "fout\t" << comps << '\t' << idx << '\n'; }
    void OnStorageImage(int bind, unsigned access) override { out << "image\t" << bind << '\t' << access << '\n'; }
};

char *Dup(const std::string &s)
{
    char *p = (char *)malloc(s.size() + 1);
    memcpy(p, s.c_str(), s.size() + 1);
    return p;
}
}  // namespace

extern "C" __attribute__((visibility("default"))) int lmc_xlat(const void *dxbc, unsigned flags, char **msl, char **reflection)
{
    GlExtensions ext = {};
    GLSLCrossDependencyData deps;
    HLSLccSamplerPrecisionInfo precisions;
    Reflect r;
    GLSLShader result;
    int ok = TranslateHLSLFromMem((const char *)dxbc, flags, LANG_METAL, &ext, &deps, precisions, r, &result);
    *msl = Dup(ok ? result.sourceCode : std::string());
    *reflection = Dup(r.out.str());
    return ok;
}

// Unity player DXBC (RDEF stripped) + Unity parameter description (see rdef::Parse) -> DXBC with an RDEF.
static std::vector<uint8_t> UnityDxbc(const void *dxbc, const char *desc)
{
    const uint8_t *d = (const uint8_t *)dxbc;
    const uint32_t *shex = rdef::FindChunk(d, "SHEX");
    if (!shex) shex = rdef::FindChunk(d, "SHDR");
    if (!shex) throw std::runtime_error("no SHEX/SHDR chunk");
    uint32_t type;
    auto decls = rdef::ScanDecls(shex, type);
    return rdef::WithRdef(d, rdef::Build(rdef::Parse(desc), decls, type));
}

// Unity player DXBC + Unity parameter description -> MSL.
extern "C" __attribute__((visibility("default"))) int lmc_xlat_unity(const void *dxbc, const char *desc, unsigned flags, char **msl, char **reflection)
{
    try
    {
        auto full = UnityDxbc(dxbc, desc);
        return lmc_xlat(full.data(), flags, msl, reflection);
    }
    catch (const std::exception &e)
    {
        *msl = Dup("");
        *reflection = Dup(std::string("error\t0\t") + e.what() + "\n");
        return 0;
    }
}

// A tessellated pass. Unity's Metal build has no hull or domain programs: the vertex and hull stages become the
// compute kernel `patchKernel`, the domain stage the post-tessellation vertex function `xlatMtlMain`, in one
// source. The stages are translated vertex -> hull -> domain through one GLSLCrossDependencyData (shared
// arguments, structs and buffer slots) and joined with Unity's stage markers. Reflection: each stage's records
// follow a "stage\t<0|1|2>" line.
extern "C" __attribute__((visibility("default"))) int lmc_xlat_unity_tess(const void *vs, const char *vsDesc, const void *hs, const char *hsDesc,
                                                                         const void *ds, const char *dsDesc, unsigned flags, char **msl, char **reflection)
{
    const void *dxbc[3] = {vs, hs, ds};
    const char *desc[3] = {vsDesc, hsDesc, dsDesc};
    std::string src, refl;
    try
    {
        GLSLCrossDependencyData deps;
        for (int s = 0; s < 3; s++)
        {
            auto full = UnityDxbc(dxbc[s], desc[s]);
            GlExtensions ext = {};
            HLSLccSamplerPrecisionInfo precisions;
            Reflect r;
            GLSLShader result;
            int ok = TranslateHLSLFromMem((const char *)full.data(), flags | HLSLCC_FLAG_METAL_TESSELLATION, LANG_METAL, &ext, &deps, precisions, r, &result);
            refl += "stage\t" + std::to_string(s) + "\n" + r.out.str();
            if (!ok) throw std::runtime_error("stage " + std::to_string(s) + " failed");
            if (s == 0) src = result.sourceCode;
            else if (s == 1) src += "// SHADER_STAGE_HULL_begin\n" + result.sourceCode + "\n// SHADER_STAGE_HULL_end\n";
            else src += "// SHADER_STAGE_DOMAIN_begin\n" + result.sourceCode + "\n// SHADER_STAGE_DOMAIN_end\n";
        }
    }
    catch (const std::exception &e)
    {
        *msl = Dup("");
        *reflection = Dup(refl + "error\t0\t" + e.what() + "\n");
        return 0;
    }
    *msl = Dup(src);
    *reflection = Dup(refl);
    return 1;
}

extern "C" __attribute__((visibility("default"))) void lmc_xlat_free(void *p) { free(p); }

#ifdef XLAT_CLI
#include <fstream>
#include <iostream>
#include <iterator>
#include <vector>
// xlat <unity program data or raw DXBC file> <flags hex> [desc file]
int main(int argc, char **argv)
{
    std::ifstream f(argv[1], std::ios::binary);
    std::vector<char> b((std::istreambuf_iterator<char>(f)), {});
    size_t at = std::string(b.begin(), b.end()).find("DXBC");
    unsigned flags = argc > 2 ? strtoul(argv[2], nullptr, 16) : 0;
    char *msl, *refl;
    int ok;
    if (argc > 3)
    {
        std::ifstream df(argv[3]);
        std::string desc((std::istreambuf_iterator<char>(df)), {});
        ok = lmc_xlat_unity(b.data() + at, desc.c_str(), flags, &msl, &refl);
    }
    else
        ok = lmc_xlat(b.data() + at, flags, &msl, &refl);
    std::cout << msl << "\n//---- reflection\n" << refl;
    return ok ? 0 : 1;
}
#endif
