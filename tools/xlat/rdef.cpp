// Rebuilds the RDEF (reflection) chunk that Unity strips from shipped D3D11 shaders, so HLSLcc can translate
// them. Resource kinds, dimensions, return types and strides come from the shader's own declarations; names
// and constant buffer layouts come from Unity's parameter data, passed in as text (see Parse below).
#include <cstdint>
#include <algorithm>
#include <cstring>
#include <map>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

namespace rdef {

struct Var { std::string name; uint32_t offset, cls, type, rows, cols, elements; std::vector<Var> members; uint32_t structSize = 0; };
struct Desc
{
    std::map<uint32_t, std::pair<std::string, uint32_t>> cbs;  // register -> (name, size in bytes)
    std::map<uint32_t, std::vector<Var>> vars;                 // cb register -> variables (D3D offsets)
    std::map<uint32_t, std::string> srv, samplers, uavs;       // t#, s#, u# -> name
};

// One record per line, tab-separated:
//   cb  <reg> <name> <size>
//   v   <reg> <name> <offset> <class> <type> <rows> <cols> <elements>
//   sv  <reg> <member of the last v (a struct)> ... same fields as v; "ss <reg> <size>" sets the struct's stride
//   t|s|u <reg> <name>
Desc Parse(const std::string &text)
{
    Desc d;
    std::istringstream in(text);
    std::string line;
    while (std::getline(in, line))
    {
        if (line.empty()) continue;
        std::vector<std::string> f;
        std::istringstream ls(line);
        for (std::string x; std::getline(ls, x, '\t');) f.push_back(x);
        uint32_t reg = std::stoul(f.at(1));
        const std::string &k = f[0];
        auto var = [&](size_t i) {
            return Var{f.at(i), uint32_t(std::stoul(f.at(i + 1))), uint32_t(std::stoul(f.at(i + 2))), uint32_t(std::stoul(f.at(i + 3))),
                       uint32_t(std::stoul(f.at(i + 4))), uint32_t(std::stoul(f.at(i + 5))), uint32_t(std::stoul(f.at(i + 6)))};
        };
        if (k == "cb") d.cbs[reg] = {f.at(2), uint32_t(std::stoul(f.at(3)))};
        else if (k == "v") d.vars[reg].push_back(var(2));
        else if (k == "sv") d.vars.at(reg).back().members.push_back(var(2));
        else if (k == "ss") d.vars.at(reg).back().structSize = std::stoul(f.at(2));
        else if (k == "t") d.srv[reg] = f.at(2);
        else if (k == "s") d.samplers[reg] = f.at(2);
        else if (k == "u") d.uavs[reg] = f.at(2);
        else throw std::runtime_error("bad desc record: " + line);
    }
    // Unity lists values grouped by kind; declare them in their original (offset) order like the source did.
    auto byOffset = [](const Var &a, const Var &b) { return a.offset < b.offset; };
    for (auto &kv : d.vars)
    {
        std::stable_sort(kv.second.begin(), kv.second.end(), byOffset);
        for (auto &v : kv.second) std::stable_sort(v.members.begin(), v.members.end(), byOffset);
    }
    return d;
}

// What the shader itself declares about each register (from the SHEX token stream).
struct Decl { int kind; uint32_t reg, dim = 0, ret = 0, samples = 0, stride = 0, mode = 0, cbVec4 = 0; bool counter = false; };
enum { K_CB, K_SAMPLER, K_TEX, K_SRV_RAW, K_SRV_STRUCT, K_UAV_TYPED, K_UAV_RAW, K_UAV_STRUCT };

static uint32_t OperandRegister(const uint32_t *op, uint32_t *second = nullptr)
{
    const uint32_t t = op[0];
    const uint32_t *p = op + 1 + ((t >> 31) ? 1 : 0);  // skip an extended operand token
    const uint32_t dims = (t >> 20) & 3;
    for (uint32_t i = 0; i < dims; i++)
        if (((t >> (22 + 3 * i)) & 7) != 0) throw std::runtime_error("declaration operand index is not immediate32");
    if (dims >= 2 && second) *second = p[1];
    return p[0];
}

static size_t OperandLength(const uint32_t *op)
{
    const uint32_t t = op[0];
    return 1 + ((t >> 31) ? 1 : 0) + ((t >> 20) & 3);
}

std::vector<Decl> ScanDecls(const uint32_t *shex, uint32_t &version)
{
    std::vector<Decl> out;
    std::set<uint32_t> counted;  // UAVs used by imm_atomic_alloc/consume: Append/ConsumeStructuredBuffers
    version = shex[0];  // program type << 16 | major << 4 | minor
    const uint32_t total = shex[1];
    for (uint32_t i = 2; i < total;)
    {
        const uint32_t tok = shex[i];
        const uint32_t op = tok & 0x7ff;
        uint32_t len = (tok >> 24) & 0x7f;
        if (op == 0x35) len = shex[i + 1];  // customdata: length in the next token
        if (len == 0) throw std::runtime_error("zero-length instruction");
        const uint32_t *o = shex + i + 1 + ((tok >> 31) ? 1 : 0);  // operands (after any extended opcode token)
        Decl d{};
        switch (op)
        {
        case 0x59: d.kind = K_CB; d.reg = OperandRegister(o, &d.cbVec4); out.push_back(d); break;
        case 0x5a: d.kind = K_SAMPLER; d.reg = OperandRegister(o); d.mode = (tok >> 11) & 0xf; out.push_back(d); break;
        case 0x58:
            d.kind = K_TEX; d.reg = OperandRegister(o); d.dim = (tok >> 11) & 0x1f; d.samples = (tok >> 16) & 0x7f;
            d.ret = o[OperandLength(o)] & 0xf; out.push_back(d); break;
        case 0xa1: d.kind = K_SRV_RAW; d.reg = OperandRegister(o); out.push_back(d); break;
        case 0xa2: d.kind = K_SRV_STRUCT; d.reg = OperandRegister(o); d.stride = o[OperandLength(o)]; out.push_back(d); break;
        case 0x9c:
            d.kind = K_UAV_TYPED; d.reg = OperandRegister(o); d.dim = (tok >> 11) & 0x1f; d.ret = o[OperandLength(o)] & 0xf;
            out.push_back(d); break;
        case 0x9d: d.kind = K_UAV_RAW; d.reg = OperandRegister(o); out.push_back(d); break;
        case 0x9e:
            d.kind = K_UAV_STRUCT; d.reg = OperandRegister(o); d.stride = o[OperandLength(o)]; d.counter = (tok >> 23) & 1;
            out.push_back(d); break;
        case 0xb2: case 0xb3: counted.insert(OperandRegister(o + OperandLength(o))); break;  // dest, then the UAV
        default: break;
        }
        i += len;
    }
    for (Decl &d : out)
        if (d.kind == K_UAV_STRUCT && counted.count(d.reg)) d.counter = 1;
    return out;
}

// DXBC RESOURCE_DIMENSION (declaration encoding) -> D3D_SRV_DIMENSION (reflection encoding)
static uint32_t ReflectDim(uint32_t dim)
{
    static const uint32_t map[] = {0, 1, 2, 4, 6, 8, 9, 3, 5, 7, 10, 1, 1};
    if (dim >= sizeof(map) / sizeof(map[0])) throw std::runtime_error("bad resource dimension");
    return map[dim];
}

struct Writer
{
    std::vector<uint8_t> b;
    uint32_t pos() const { return uint32_t(b.size()); }
    void u32(uint32_t v) { b.insert(b.end(), (uint8_t *)&v, (uint8_t *)&v + 4); }
    void u16(uint16_t v) { b.insert(b.end(), (uint8_t *)&v, (uint8_t *)&v + 2); }
    void set(uint32_t at, uint32_t v) { memcpy(&b[at], &v, 4); }
    uint32_t str(const std::string &s) { uint32_t p = pos(); b.insert(b.end(), s.begin(), s.end()); b.push_back(0); while (b.size() % 4) b.push_back(0); return p; }
};

// D3D11 packing size of a variable: every array element and matrix column/row starts on a 16-byte register.
static uint32_t VarSize(const Var &v)
{
    uint32_t one;
    if (v.cls == 5) one = v.structSize;
    else if (v.cls == 2) one = 16 * (v.rows - 1) + 4 * v.cols;  // row-major: one register per row
    else if (v.cls == 3) one = 16 * (v.cols - 1) + 4 * v.rows;  // column-major: one register per column
    else one = 4 * v.cols;
    const uint32_t n = v.elements > 1 ? v.elements : 1;
    return n > 1 ? 16 * ((one + 15) / 16) * (n - 1) + one : one;
}

static uint32_t WriteType(Writer &w, const Var &v)
{
    std::vector<uint32_t> memberTypes;
    for (const Var &m : v.members) memberTypes.push_back(WriteType(w, m));
    uint32_t members = 0;
    if (!v.members.empty())
    {
        std::vector<uint32_t> names;
        for (const Var &m : v.members) names.push_back(w.str(m.name));
        members = w.pos();
        for (size_t i = 0; i < v.members.size(); i++) { w.u32(names[i]); w.u32(memberTypes[i]); w.u32(v.members[i].offset); }
    }
    const uint32_t at = w.pos();
    w.u16(uint16_t(v.cls)); w.u16(uint16_t(v.type)); w.u16(uint16_t(v.rows)); w.u16(uint16_t(v.cols));
    w.u16(uint16_t(v.elements)); w.u16(uint16_t(v.members.size())); w.u32(members);
    return at;
}

// Returns a complete RDEF chunk payload (without the 8-byte chunk header).
std::vector<uint8_t> Build(const Desc &desc, const std::vector<Decl> &decls, uint32_t version)
{
    struct Res { std::string name; uint32_t type, ret, dim, samples, bind, flags; };
    struct Cb { std::string name; uint32_t size; std::vector<Var> vars; };
    std::vector<Res> res;
    std::vector<Cb> cbs;
    auto name = [](const std::map<uint32_t, std::string> &m, uint32_t reg, const char *what) {
        auto it = m.find(reg);
        if (it != m.end()) return it->second;
        // Pixel-shader UAVs are Unity "random write targets", bound by register, so Unity keeps no name for them.
        if (what[0] == 'u') return "_RandomWriteTarget" + std::to_string(reg);
        throw std::runtime_error(std::string("no name for ") + what + std::to_string(reg));
    };
    for (const Decl &d : decls)
    {
        switch (d.kind)
        {
        case K_CB:
        {
            auto it = desc.cbs.find(d.reg);
            if (it == desc.cbs.end()) throw std::runtime_error("no name for cb" + std::to_string(d.reg));
            auto vit = desc.vars.find(d.reg);
            cbs.push_back({it->second.first, std::max(it->second.second, d.cbVec4 * 16), vit == desc.vars.end() ? std::vector<Var>() : vit->second});
            res.push_back({it->second.first, 0, 0, 0, 0, d.reg, 0});
            break;
        }
        case K_SAMPLER: res.push_back({name(desc.samplers, d.reg, "s"), 3, 0, 0, 0, d.reg, d.mode == 1 ? 2u : 0u}); break;
        case K_TEX: res.push_back({name(desc.srv, d.reg, "t"), 2, d.ret, ReflectDim(d.dim), d.samples ? d.samples : 0xffffffffu, d.reg, 0}); break;
        case K_SRV_RAW: res.push_back({name(desc.srv, d.reg, "t"), 7, 0, 1, 0, d.reg, 0}); break;
        case K_SRV_STRUCT:
            res.push_back({name(desc.srv, d.reg, "t"), 5, 0, 1, 0, d.reg, 0});
            cbs.push_back({name(desc.srv, d.reg, "t"), d.stride, {}});
            break;
        case K_UAV_TYPED: res.push_back({name(desc.uavs, d.reg, "u"), 4, d.ret, ReflectDim(d.dim), 0, d.reg, 0}); break;
        case K_UAV_RAW: res.push_back({name(desc.uavs, d.reg, "u"), 8, 0, 1, 0, d.reg, 0}); break;
        case K_UAV_STRUCT:
            res.push_back({name(desc.uavs, d.reg, "u"), d.counter ? 11u : 6u, 0, 1, 0, d.reg, 0});
            cbs.push_back({name(desc.uavs, d.reg, "u"), d.stride, {}});
            break;
        }
    }

    Writer w;
    w.u32(uint32_t(cbs.size())); w.u32(0); w.u32(uint32_t(res.size())); w.u32(0);
    const uint32_t major = (version >> 4) & 0xf;
    w.u32(((version >> 16) << 16) | (major << 8) | (version & 0xf)); w.u32(0); w.u32(0);
    // resource bindings
    std::vector<uint32_t> resNameFix;
    const uint32_t resAt = w.pos();
    for (const Res &r : res)
    {
        resNameFix.push_back(w.pos());
        w.u32(0); w.u32(r.type); w.u32(r.ret); w.u32(r.dim); w.u32(r.samples); w.u32(r.bind); w.u32(1); w.u32(r.flags);
    }
    // constant buffers
    std::vector<uint32_t> cbFix;
    const uint32_t cbAt = w.pos();
    for (const Cb &c : cbs)
    {
        cbFix.push_back(w.pos());
        w.u32(0); w.u32(uint32_t(c.vars.size())); w.u32(0); w.u32(c.size); w.u32(0); w.u32(0);
    }
    w.set(4, cbAt); w.set(12, resAt);
    for (size_t i = 0; i < res.size(); i++) w.set(resNameFix[i], w.str(res[i].name));
    for (size_t i = 0; i < cbs.size(); i++)
    {
        const Cb &c = cbs[i];
        w.set(cbFix[i], w.str(c.name));
        std::vector<uint32_t> types, names;
        for (const Var &v : c.vars) { types.push_back(WriteType(w, v)); names.push_back(w.str(v.name)); }
        w.set(cbFix[i] + 8, w.pos());
        for (size_t j = 0; j < c.vars.size(); j++)
        {
            const Var &v = c.vars[j];
            w.u32(names[j]); w.u32(v.offset); w.u32(VarSize(v)); w.u32(2 /* D3D_SVF_USED */); w.u32(types[j]); w.u32(0);
            if (major >= 5) { w.u32(0xffffffff); w.u32(0); w.u32(0xffffffff); w.u32(0); }  // SM5 adds texture/sampler ranges
        }
    }
    return w.b;
}

// New DXBC container = the original chunks plus our RDEF (any existing RDEF is dropped).
std::vector<uint8_t> WithRdef(const uint8_t *dxbc, const std::vector<uint8_t> &rdef)
{
    uint32_t count, total;
    memcpy(&total, dxbc + 24, 4);
    memcpy(&count, dxbc + 28, 4);
    std::vector<std::pair<const uint8_t *, uint32_t>> chunks;
    for (uint32_t i = 0; i < count; i++)
    {
        uint32_t off, size;
        memcpy(&off, dxbc + 32 + 4 * i, 4);
        memcpy(&size, dxbc + off + 4, 4);
        if (memcmp(dxbc + off, "RDEF", 4) != 0) chunks.push_back({dxbc + off, size + 8});
    }
    std::vector<uint8_t> out(32 + 4 * (chunks.size() + 1));
    memcpy(out.data(), dxbc, 32);
    std::vector<uint32_t> offs;
    offs.push_back(uint32_t(out.size()));
    out.insert(out.end(), {'R', 'D', 'E', 'F'});
    uint32_t sz = uint32_t(rdef.size());
    out.insert(out.end(), (uint8_t *)&sz, (uint8_t *)&sz + 4);
    out.insert(out.end(), rdef.begin(), rdef.end());
    for (auto &c : chunks) { offs.push_back(uint32_t(out.size())); out.insert(out.end(), c.first, c.first + c.second); }
    uint32_t n = uint32_t(offs.size()), t = uint32_t(out.size());
    memcpy(&out[24], &t, 4);
    memcpy(&out[28], &n, 4);
    for (uint32_t i = 0; i < n; i++) memcpy(&out[32 + 4 * i], &offs[i], 4);
    return out;
}

const uint32_t *FindChunk(const uint8_t *dxbc, const char *fourcc)
{
    uint32_t count;
    memcpy(&count, dxbc + 28, 4);
    for (uint32_t i = 0; i < count; i++)
    {
        uint32_t off;
        memcpy(&off, dxbc + 32 + 4 * i, 4);
        if (memcmp(dxbc + off, fourcc, 4) == 0) return (const uint32_t *)(dxbc + off + 8);
    }
    return nullptr;
}

}  // namespace rdef
