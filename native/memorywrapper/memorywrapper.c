/* MemoryWrapper-x64-Release (macOS): routes the EOS SDK's allocations through Unity's IUnityMemoryManager.
 * Clean-room implementation of SPEC.md. Layouts below are from Unity's public IUnityInterface.h and
 * IUnityMemoryManager.h and EOS's eos_init.h (EOS_MEMORY_CALL is empty off Windows). No locking by design
 * (SPEC §7): the two pointers are written only on Unity's load/unload path and read by the callbacks. */
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#define EXPORT __attribute__((visibility("default")))

typedef struct { unsigned long long m_GUIDHigh, m_GUIDLow; } UnityInterfaceGUID;
typedef struct PluginAllocator UnityAllocator;
/* In C++ (how Unity is built) UnityInterfaceGUID has a user-defined copy constructor, so the by-value
 * GUID is passed by hidden reference on arm64 (Itanium C++ ABI), not in x0/x1 as a plain C struct would
 * be. Declared as a pointer to match; Windows x64 passes 16-byte structs by reference too. */
typedef struct {
    void *(*GetInterface)(const UnityInterfaceGUID *guid);
    void (*RegisterInterface)(const UnityInterfaceGUID *guid, void *ptr);
    void *(*GetInterfaceSplit)(unsigned long long guidHigh, unsigned long long guidLow);
    void (*RegisterInterfaceSplit)(unsigned long long guidHigh, unsigned long long guidLow, void *ptr);
} IUnityInterfaces;
typedef struct {
    UnityAllocator *(*CreateAllocator)(const char *areaName, const char *objectName);
    void (*DestroyAllocator)(UnityAllocator *allocator);
    void *(*Allocate)(UnityAllocator *allocator, size_t size, size_t align, const char *file, int32_t line);
    void (*Deallocate)(UnityAllocator *allocator, void *ptr, const char *file, int32_t line);
    void *(*Reallocate)(UnityAllocator *allocator, void *ptr, size_t size, size_t align, const char *file, int32_t line);
} IUnityMemoryManager;

static IUnityMemoryManager *g_mm;
static UnityAllocator *g_alloc;

/* SPEC §8, labelled-abort option: no malloc fallback, which would hide a broken plugin load. */
static IUnityMemoryManager *mm(void) {
    if (!g_mm) {
        fputs("MemoryWrapper: IUnityMemoryManager unavailable (UnityPluginLoad not called or GetInterface returned null); aborting\n", stderr);
        abort();
    }
    return g_mm;
}

static void *Allocate(size_t size, size_t align) { return mm()->Allocate(g_alloc, size, align, "memorywrapper.c", __LINE__); }
static void *Reallocate(void *p, size_t size, size_t align) { return mm()->Reallocate(g_alloc, p, size, align, "memorywrapper.c", __LINE__); }
static void Release(void *p) { mm()->Deallocate(g_alloc, p, "memorywrapper.c", __LINE__); }

EXPORT void UnityPluginLoad(IUnityInterfaces *ui) {
    UnityInterfaceGUID guid = { 0xBAF9E57C61A811ECULL, 0xC5A7CC7861A811ECULL };
    g_mm = ui->GetInterface(&guid);
    if (g_mm) g_alloc = g_mm->CreateAllocator("PortningsbolagetMemory", "PortningsbolagetMemory");
}

EXPORT void UnityPluginUnload(void) {
    if (g_mm && g_alloc) { g_mm->DestroyAllocator(g_alloc); g_alloc = NULL; }
}

EXPORT void Init(void) {}
EXPORT void *GetAllocateFunctionPointer(void) { return (void *)Allocate; }
EXPORT void *GetReallocateFunctionPointer(void) { return (void *)Reallocate; }
EXPORT void *GetReleaseFunctionPointer(void) { return (void *)Release; }
