/* A2 fake-Unity harness for MemoryWrapper (SPEC.md §10). One portable C file:
 *   Mac:     clang -O2 -o harness harness.c
 *   Windows: cl /O2 harness.c            (x64 Developer Command Prompt; or x86_64 MinGW gcc)
 *          (build.sh also cross-builds a static harness.exe when x86_64-w64-mingw32-gcc is installed)
 * Usage:   harness <library path> <trace out>      exit 0 = every assertion held
 * Windows: harness.exe <PEAK>\PEAK_Data\Plugins\x86_64\MemoryWrapper-x64-Release.dll trace-win.txt
 *          fc /b trace-win.txt trace-mac.txt       (trace-mac.txt is the Mac run, kept next to this file)
 * Pass = the trace file is byte-identical across OSes (file/line arguments are never written to it).
 * S2 re-runs this program as a child (argv[0], so run it by a path) that must die on its first allocation.
 * Pointers in the trace are symbolic: UI fake IUnityInterfaces, MM fake IUnityMemoryManager, A<n> allocator
 * handles and P<n> blocks (a fresh id each time the fake returns one, so malloc address reuse can't make
 * ids differ between OSes), F<n> getter results (first-seen order), null. Ids restart per scenario. */
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <windows.h>
#define API __stdcall
#define CHILD_CMD "\"\"%s\" \"%s\" --s2-child\""  /* cmd.exe /c strips the outer quotes */
#else
#include <dlfcn.h>
#include <pthread.h>
#define API
#define CHILD_CMD "'%s' '%s' --s2-child"
#endif
#ifdef _MSC_VER
#define TLS __declspec(thread)
#else
#define TLS _Thread_local
#endif
#ifdef __APPLE__
#include <mach/mach.h>
#endif

typedef struct { unsigned long long hi, lo; } GUID128;
typedef struct UnityAllocator UnityAllocator;
typedef struct {
    /* Unity's C++ UnityInterfaceGUID has a user-defined copy constructor, so it is passed by hidden
     * reference (Itanium/AAPCS64 C++ ABI; Windows x64 passes 16-byte structs by reference anyway). */
    void *(API *GetInterface)(const GUID128 *);
    void (API *RegisterInterface)(const GUID128 *, void *);
    void *(API *GetInterfaceSplit)(unsigned long long, unsigned long long);
    void (API *RegisterInterfaceSplit)(unsigned long long, unsigned long long, void *);
} Interfaces;
typedef struct {
    UnityAllocator *(API *CreateAllocator)(const char *, const char *);
    void (API *DestroyAllocator)(UnityAllocator *);
    void *(API *Allocate)(UnityAllocator *, size_t, size_t, const char *, int32_t);
    void (API *Deallocate)(UnityAllocator *, void *, const char *, int32_t);
    void *(API *Reallocate)(UnityAllocator *, void *, size_t, size_t, const char *, int32_t);
} MemoryManager;
#define MM_HI 0xBAF9E57C61A811ECULL
#define MM_LO 0xC5A7CC7861A811ECULL

/* ---- trace + symbolic pointer ids ---- */
static FILE *out;
static int fails;
#define CHECK(c) do { if (!(c)) { fails++; fprintf(stderr, "FAIL harness.c:%d: %s\n", __LINE__, #c); } } while (0)
static void tr(const char *fmt, ...) { va_list ap; va_start(ap, fmt); vfprintf(out, fmt, ap); va_end(ap); }

static struct { const void *p; char id[16]; } syms[256];
static int nsyms, nA, nP, nF;
static const char *fresh(const void *p, char kind) {  /* new id for p; latest registration wins */
    int *n = kind == 'A' ? &nA : kind == 'P' ? &nP : &nF;
    if (!p || nsyms == 256) return "null";
    syms[nsyms].p = p;
    snprintf(syms[nsyms].id, sizeof syms[nsyms].id, "%c%d", kind, ++*n);
    return syms[nsyms++].id;
}
static const char *sym(const void *p) {
    int i;
    if (!p) return "null";
    for (i = nsyms - 1; i >= 0; i--) if (syms[i].p == p) return syms[i].id;
    return "?unknown";
}

/* ---- real aligned memory, thread-safe (malloc); header holds base + size ---- */
typedef struct { void *base; size_t size; } Hdr;
static void *blk_alloc(size_t size, size_t align) {
    char *base = malloc(sizeof(Hdr) + align + size);
    uintptr_t p;
    Hdr h;
    if (!base) return NULL;
    p = ((uintptr_t)base + sizeof(Hdr) + align - 1) & ~(uintptr_t)(align - 1);
    h.base = base; h.size = size;
    memcpy((char *)p - sizeof h, &h, sizeof h);
    return (void *)p;
}
static Hdr blk_hdr(void *p) { Hdr h; memcpy(&h, (char *)p - sizeof h, sizeof h); return h; }
static void blk_free(void *p) { if (p) free(blk_hdr(p).base); }

/* ---- recording fake Unity ---- */
static int mode_no_mm, mode_null_alloc, tracing, unity_calls;
static char handles[8];  /* allocator handles are &handles[i] */
static int nhandles;
static TLS struct { long n; char fn; void *a, *p, *r; size_t size, align; const char *file; } last;
static TLS long fwd[3];  /* forwarded Allocate / Reallocate / Deallocate on this thread */
static MemoryManager fake_mm;
static Interfaces fake_ui;

static void *API f_GetInterface(const GUID128 *g) {
    void *r = (!mode_no_mm && g->hi == MM_HI && g->lo == MM_LO) ? (void *)&fake_mm : NULL;
    unity_calls++;
    tr("  GetInterface(%016llX-%016llX) -> %s\n", g->hi, g->lo, sym(r));
    return r;
}
static void API f_RegisterInterface(const GUID128 *g, void *p) { unity_calls++; tr("  RegisterInterface(%016llX-%016llX,%s)\n", g->hi, g->lo, sym(p)); }
static void *API f_GetInterfaceSplit(unsigned long long hi, unsigned long long lo) {
    unity_calls++;
    tr("  GetInterfaceSplit(%016llX-%016llX) -> null\n", hi, lo);
    return NULL;
}
static void API f_RegisterInterfaceSplit(unsigned long long hi, unsigned long long lo, void *p) { unity_calls++; tr("  RegisterInterfaceSplit(%016llX-%016llX,%s)\n", hi, lo, sym(p)); }

static UnityAllocator *API f_CreateAllocator(const char *area, const char *object) {
    UnityAllocator *r = mode_null_alloc ? NULL : (UnityAllocator *)&handles[nhandles++ % 8];
    unity_calls++;
    tr("  CreateAllocator(\"%s\",\"%s\") -> %s\n", area ? area : "(null)", object ? object : "(null)", r ? fresh(r, 'A') : "null");
    return r;
}
static void API f_DestroyAllocator(UnityAllocator *a) { unity_calls++; tr("  DestroyAllocator(%s)\n", sym(a)); }

static void rec(char fn, UnityAllocator *a, void *p, size_t size, size_t align, void *r, const char *file) {
    last.n++; last.fn = fn; last.a = a; last.p = p; last.r = r; last.size = size; last.align = align; last.file = file;
    fwd[fn == 'A' ? 0 : fn == 'R' ? 1 : 2]++;
}
static void *API f_Allocate(UnityAllocator *a, size_t size, size_t align, const char *file, int32_t line) {
    void *r = blk_alloc(size, align);
    (void)line;
    rec('A', a, NULL, size, align, r, file);
    if (tracing) { tr("  Allocate(%s,%zu,%zu)", sym(a), size, align); tr(" -> %s\n", fresh(r, 'P')); }
    return r;
}
static void *API f_Reallocate(UnityAllocator *a, void *p, size_t size, size_t align, const char *file, int32_t line) {
    void *r = blk_alloc(size, align);
    (void)line;
    if (r && p) { size_t old = blk_hdr(p).size; memcpy(r, p, old < size ? old : size); }
    rec('R', a, p, size, align, r, file);
    if (tracing) { tr("  Reallocate(%s,%s,%zu,%zu)", sym(a), sym(p), size, align); tr(" -> %s\n", fresh(r, 'P')); }
    if (r) blk_free(p);
    return r;
}
static void API f_Deallocate(UnityAllocator *a, void *p, const char *file, int32_t line) {
    (void)line;
    rec('D', a, p, 0, 0, NULL, file);
    if (tracing) tr("  Deallocate(%s,%s)\n", sym(a), sym(p));
    blk_free(p);
}

/* ---- the library under test ---- */
static void (API *pLoad)(Interfaces *);
static void (API *pUnload)(void);
static void (API *pInit)(void);
static void *(API *pGet[3])(void);
static const char *getter_names[3] = { "GetAllocateFunctionPointer", "GetReallocateFunctionPointer", "GetReleaseFunctionPointer" };
static void *(*cb_alloc)(size_t, size_t);
static void *(*cb_realloc)(void *, size_t, size_t);
static void (*cb_release)(void *);

static void *load_symbol(void *lib, const char *name) {
#ifdef _WIN32
    void *p = (void *)GetProcAddress((HMODULE)lib, name);
#else
    void *p = dlsym(lib, name);
#endif
    if (!p) { fprintf(stderr, "missing export %s\n", name); exit(2); }
    return p;
}
static void open_library(const char *path) {
#ifdef _WIN32
    void *lib = (void *)LoadLibraryA(path);
#else
    void *lib = dlopen(path, RTLD_NOW | RTLD_LOCAL);
#endif
    int i;
    if (!lib) { fprintf(stderr, "cannot load %s\n", path); exit(2); }
    *(void **)&pLoad = load_symbol(lib, "UnityPluginLoad");
    *(void **)&pUnload = load_symbol(lib, "UnityPluginUnload");
    *(void **)&pInit = load_symbol(lib, "Init");
    for (i = 0; i < 3; i++) *(void **)&pGet[i] = load_symbol(lib, getter_names[i]);
}

/* ---- traced wrappers around plugin entry points ---- */
static UnityAllocator *expect_a;  /* allocator handle the callbacks must forward */
static void scenario(const char *name, int no_mm, int null_alloc) {
    mode_no_mm = no_mm; mode_null_alloc = null_alloc;
    nsyms = nA = nP = nF = 0;
    syms[nsyms].p = &fake_ui; strcpy(syms[nsyms++].id, "UI");
    syms[nsyms].p = &fake_mm; strcpy(syms[nsyms++].id, "MM");
    tr("== %s\n", name);
}
static void load(void) { tr("UnityPluginLoad(UI) {\n"); pLoad(&fake_ui); tr("}\n"); expect_a = mode_null_alloc ? NULL : (UnityAllocator *)&handles[(nhandles + 7) % 8]; }
static void unload(void) { tr("UnityPluginUnload() {\n"); pUnload(); tr("}\n"); }
static void init(void) { int n = unity_calls; tr("Init() {\n"); pInit(); tr("}\n"); CHECK(unity_calls == n); }
static void getters(void) {
    void *v[3];
    int i, n = unity_calls;
    for (i = 0; i < 6; i++) {
        void *p;
        tr("%s() {\n", getter_names[i % 3]);
        p = pGet[i % 3]();
        tr("} -> %s\n", sym(p)[0] == '?' ? fresh(p, 'F') : sym(p));
        if (i < 3) v[i] = p; else CHECK(p == v[i - 3]);
    }
    CHECK(v[0] && v[1] && v[2] && v[0] != v[1] && v[0] != v[2] && v[1] != v[2] && unity_calls == n);
    if (!cb_alloc) { *(void **)&cb_alloc = v[0]; *(void **)&cb_realloc = v[1]; *(void **)&cb_release = v[2]; }
    CHECK(*(void **)&cb_alloc == v[0] && *(void **)&cb_realloc == v[1] && *(void **)&cb_release == v[2]);
}
static void *alloc(size_t size, size_t align) {
    long n = last.n;
    void *r;
    tr("alloc(%zu,%zu) {\n", size, align);
    r = cb_alloc(size, align);
    tr("} -> %s\n", sym(r));
    CHECK(last.n == n + 1 && last.fn == 'A' && last.a == expect_a && last.size == size && last.align == align && last.r == r && last.file);
    return r;
}
static void *realloc_(void *p, size_t size, size_t align) {
    long n = last.n;
    void *r;
    tr("realloc(%s,%zu,%zu) {\n", sym(p), size, align);
    r = cb_realloc(p, size, align);
    tr("} -> %s\n", sym(r));
    CHECK(last.n == n + 1 && last.fn == 'R' && last.a == expect_a && last.p == p && last.size == size && last.align == align && last.r == r && last.file);
    return r;
}
static void release(void *p) {
    long n = last.n;
    tr("release(%s) {\n", sym(p));
    cb_release(p);
    tr("}\n");
    CHECK(last.n == n + 1 && last.fn == 'D' && last.a == expect_a && last.p == p && last.file);
}

/* ---- S5: 8 threads x 100,000 mixed ops ---- */
#define NTHR 8
#define OPS 100000
static long s5_iss[NTHR][3], s5_fwd[NTHR][3], s5_fail[NTHR];
static void s5_thread(int t) {
    unsigned seed = 0x9E3779B9u * (unsigned)(t + 1);
    void *live[16] = { 0 };
    long iss[3] = { 0, 0, 0 }, fail = 0;
    int i, k;
    for (i = 0; i < OPS + 16; i++) {
        int slot, op;
        size_t size, align;
        void *p, *r = NULL;
        long n = last.n;
        seed = seed * 1664525u + 1013904223u;
        slot = i < OPS ? (int)((seed >> 8) & 15) : i - OPS;  /* last 16 iterations release what's left */
        size = (seed >> 12) % 2049;
        align = (size_t)1 << ((seed >> 24) % 8);
        p = live[slot];
        if (i >= OPS) { if (!p) continue; op = 2; }
        else op = !p ? (seed >> 31 ? 0 : 1) : (seed >> 31 ? 2 : 1);
        if (op == 0) r = cb_alloc(size, align);
        else if (op == 1) r = cb_realloc(p, size, align);
        else cb_release(p);
        iss[op]++;
        if (last.n != n + 1 || last.fn != "ARD"[op] || last.a != expect_a || !last.file) fail++;
        if (op != 0 && last.p != p) fail++;
        if (op != 2 && (last.size != size || last.align != align || last.r != r || !r || ((uintptr_t)r & (align - 1)))) fail++;
        if (r) memset(r, t, size);
        live[slot] = r;
    }
    for (k = 0; k < 3; k++) { s5_iss[t][k] = iss[k]; s5_fwd[t][k] = fwd[k]; }
    s5_fail[t] = fail;
}
#ifdef _WIN32
static DWORD WINAPI thread_main(LPVOID a) { s5_thread((int)(intptr_t)a); return 0; }
#else
static void *thread_main(void *a) { s5_thread((int)(intptr_t)a); return NULL; }
#endif
static void run_s5(void) {
    long iss[3] = { 0, 0, 0 }, f[3] = { 0, 0, 0 }, fail = 0;
    int t, k;
#ifdef _WIN32
    HANDLE th[NTHR];
    for (t = 0; t < NTHR; t++) th[t] = CreateThread(NULL, 0, thread_main, (LPVOID)(intptr_t)t, 0, NULL);
    WaitForMultipleObjects(NTHR, th, TRUE, INFINITE);
#else
    pthread_t th[NTHR];
    for (t = 0; t < NTHR; t++) pthread_create(&th[t], NULL, thread_main, (void *)(intptr_t)t);
    for (t = 0; t < NTHR; t++) pthread_join(th[t], NULL);
#endif
    for (t = 0; t < NTHR; t++) { fail += s5_fail[t]; for (k = 0; k < 3; k++) { iss[k] += s5_iss[t][k]; f[k] += s5_fwd[t][k]; } }
    tr("threads %d x %d ops: issued alloc=%ld realloc=%ld release=%ld; forwarded alloc=%ld realloc=%ld release=%ld; mismatches=%ld\n",
       NTHR, OPS, iss[0], iss[1], iss[2], f[0], f[1], f[2], fail);
    CHECK(fail == 0 && iss[0] == f[0] && iss[1] == f[1] && iss[2] == f[2]);
}

#ifdef __APPLE__
static unsigned thread_count(void) {
    thread_act_array_t list;
    mach_msg_type_number_t n = 0;
    if (task_threads(mach_task_self(), &list, &n) == KERN_SUCCESS)
        vm_deallocate(mach_task_self(), (vm_address_t)list, n * sizeof *list);
    return n;
}
#endif

int main(int argc, char **argv) {
    static const size_t sizes[] = { 0, 1, 7, 16, 4096, 1 << 20 }, aligns[] = { 1, 2, 8, 16, 64, 4096 };
    void *blocks[36], *p;
    char cmd[4096];
    int i, j, status;
#ifdef __APPLE__
    unsigned threads_before = thread_count();
#endif
    fake_ui.GetInterface = f_GetInterface; fake_ui.RegisterInterface = f_RegisterInterface;
    fake_ui.GetInterfaceSplit = f_GetInterfaceSplit; fake_ui.RegisterInterfaceSplit = f_RegisterInterfaceSplit;
    fake_mm.CreateAllocator = f_CreateAllocator; fake_mm.DestroyAllocator = f_DestroyAllocator;
    fake_mm.Allocate = f_Allocate; fake_mm.Deallocate = f_Deallocate; fake_mm.Reallocate = f_Reallocate;
    if (argc != 3) { fprintf(stderr, "usage: %s <library> <trace out>\n", argv[0]); return 2; }
    open_library(argv[1]);

    if (!strcmp(argv[2], "--s2-child")) {  /* S2 child: no memory manager, then the first allocation */
#ifdef _WIN32
        SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);  /* crash without a WER dialog */
#endif
        out = stderr; mode_no_mm = 1;
        pLoad(&fake_ui);
        *(void **)&cb_alloc = pGet[0]();
        cb_alloc(16, 16);
        fprintf(stderr, "S2 child: allocate returned\n");
        return 0;
    }
#ifdef __APPLE__
    { unsigned after = thread_count(); fprintf(stderr, "A1 dlopen: threads %u -> %u\n", threads_before, after); CHECK(after == threads_before); }
#endif
    CHECK(unity_calls == 0 && last.n == 0);
    if (!(out = fopen(argv[2], "wb"))) { fprintf(stderr, "cannot write %s\n", argv[2]); return 2; }
    tracing = 1;

    scenario("S1 normal", 0, 0);
    load();
    init();
    getters();
    for (i = 0; i < 6; i++)
        for (j = 0; j < 6; j++) {
            p = blocks[i * 6 + j] = alloc(sizes[i], aligns[j]);
            CHECK(p && ((uintptr_t)p & (aligns[j] - 1)) == 0);
        }
    for (i = 0; i < 36; i++) blk_free(blocks[i]);
    p = realloc_(NULL, 64, 16);
    p = realloc_(p, 0, 16);
    p = realloc_(p, 2 << 20, 64);
    release(NULL);
    release(p);
    unload();
    unload();
    expect_a = NULL;  /* after unload the callbacks forward the now-null handle */
    p = alloc(16, 16);
    p = realloc_(p, 32, 16);
    release(p);

    scenario("S2 GetInterface returns null", 1, 0);
    load();
    init();
    getters();
    unload();
    snprintf(cmd, sizeof cmd, CHILD_CMD, argv[0], argv[1]);
    fflush(NULL);
    status = system(cmd);
    fprintf(stderr, "S2 child exit status %d (0x%X)\n", status, (unsigned)status);
    tr("child: UnityPluginLoad(GetInterface -> null), alloc(16,16) -> %s\n", status ? "process terminated" : "RETURNED");
    CHECK(status != 0);

    scenario("S3 CreateAllocator returns null", 0, 1);
    load();
    p = alloc(32, 16);
    p = realloc_(p, 64, 16);
    release(p);
    unload();

    scenario("S4 UnityPluginLoad twice", 0, 0);
    load();
    load();
    p = alloc(64, 8);
    release(p);
    unload();
    unload();

    scenario("S5 concurrency", 0, 0);
    load();
    tracing = 0;
    run_s5();
    tracing = 1;
    unload();

    fclose(out);
    printf("A2 harness: %s (%d failed assertions)\n", fails ? "FAIL" : "all assertions held", fails);
    return fails ? 1 : 0;
}
