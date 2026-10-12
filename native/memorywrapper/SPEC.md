# MemoryWrapper spec (clean-room input)

This spec is the only input for the clean-room macOS replacement of PEAK's `MemoryWrapper-x64-Release.dll`. It states interfaces and observable behaviour as facts. It contains no code, pseudo-code, disassembly or byte data from the proprietary binary.

Sources of these facts:
- The P/Invoke declarations and call sites in the game's managed assemblies (`PortningsbolagetMemory.dll`, `EpicCommon.dll`, `User.dll`, `Assembly-CSharp.dll`). This is public interface information: names, signatures and call order only.
- Unity's public Native Plugin API headers `IUnityInterface.h` and `IUnityMemoryManager.h` (the `PluginAPI` folder of any Unity Editor install, Unity Companion License), and the Unity 6000.3 Manual page "Native plug-in API for memory management".
- Epic's public EOS SDK headers `eos_init.h` and `eos_base.h` for SDK 1.19.1.2-CL53289219, the version the game ships.
- Dirty-room analysis of the Windows DLL, done for interoperability. It appears here only as plain-language statements of which Unity calls the library makes, when, and with what arguments. Nothing structural from that analysis is passed on.

The implementer must work only from this document and the public headers and docs named above, and must never see the Windows DLL or any analysis of it.

Reference binary for the verifier only: the game's `PEAK_Data/Plugins/x86_64/MemoryWrapper-x64-Release.dll`, sha256 `c22dc3f4a89fb73fffe0cb318c7efcdde49454213117143ac241866ada69493a`, 11,264 bytes (PEAK build 2.6.b). The game also ships `MemoryWrapper-x64-Debug.dll`. Nothing references it, so it is out of scope.

## 1. What it is
A tiny native plugin that routes the Epic Online Services SDK's memory allocations through Unity's memory manager. Unity gives the plugin `IUnityMemoryManager` at load time. The plugin creates one Unity allocator and hands EOS three C callbacks (allocate, reallocate, release) that forward each request to that allocator. The plugin owns no memory and adds no policy: every request goes straight through.

## 2. Target
- One macOS arm64 Mach-O dynamic library. A universal x86_64+arm64 build is allowed but not required.
- **Name and location:** `<App>.app/Contents/PlugIns/MemoryWrapper-x64-Release.dylib`.
  - The managed `DllImport` name is exactly `MemoryWrapper-x64-Release`. Keep the `x64` in the file name; it is only a string.
  - It must be found by **Unity's own plugin loader**, not merely by a generic Mono probe, because only Unity's loader calls `UnityPluginLoad` (§4). Test A3 checks this.
  - If A3 shows `UnityPluginLoad` is not called for the `.dylib`, ship the same code as a Mach-O bundle named `MemoryWrapper-x64-Release.bundle` in the same folder (the format Photon and Steamworks.NET use for Unity Mac plugins), then re-run A3.
- Dependencies: system libraries only (libSystem). No C++ runtime is needed.
- No work at load time: no static constructors that allocate, start threads or call Unity.
- It is signed as part of the app (ad-hoc). Its install name doesn't matter.
- **If the file is missing**, the first P/Invoke (`Init`) throws `DllNotFoundException`. The game doesn't catch it, so `GameHandler.Initialize` aborts. The library must be present and loadable.

## 3. Exports
Exactly these 6 C symbols, with default visibility and the platform C calling convention. On macOS, Unity's `UNITY_INTERFACE_API` is empty and `UNITY_INTERFACE_EXPORT` is default visibility. The managed `DllImport`s use the default `CallingConvention.Winapi`, which on macOS is the platform C convention.

| Export | Parameters | Returns | Called by |
|---|---|---|---|
| `UnityPluginLoad` | `IUnityInterfaces*` | nothing | Unity, when it loads the plugin |
| `UnityPluginUnload` | none | nothing | Unity, when it unloads the plugin (player shutdown) |
| `Init` | none | nothing | managed `Portningsbolaget.Memory.MemoryWrapper.GetFunctionPointers` (declared `void Init()`) |
| `GetAllocateFunctionPointer` | none | pointer-sized code address (managed `IntPtr`) | same managed method |
| `GetReallocateFunctionPointer` | none | pointer-sized code address | same managed method |
| `GetReleaseFunctionPointer` | none | pointer-sized code address | same managed method |

The three callbacks behind those addresses are not exported. Their C signatures must be exactly EOS's allocator function types (§5):
- allocate: `(size_t SizeInBytes, size_t Alignment) → void*`
- reallocate: `(void* Pointer, size_t SizeInBytes, size_t Alignment) → void*`
- release: `(void* Pointer) → void`

`EOS_MEMORY_CALL` is empty on non-Windows platforms (`eos_base.h`), so these are plain C functions.

## 4. Unity Native Plugin API used (public headers)

### From `IUnityInterface.h`
- `UnityInterfaceGUID` is a struct of two `unsigned long long`: `m_GUIDHigh` first, then `m_GUIDLow`.
- `IUnityInterfaces` is a struct of 4 function pointers, in this order:
  1. `GetInterface(UnityInterfaceGUID guid) → IUnityInterface*` (the GUID is passed by value). It returns null if the interface is unavailable.
  2. `RegisterInterface(guid, ptr)`
  3. `GetInterfaceSplit(guidHigh, guidLow) → IUnityInterface*`
  4. `RegisterInterfaceSplit(guidHigh, guidLow, ptr)`
- `UnityPluginLoad(IUnityInterfaces*)` and `UnityPluginUnload()` are called by Unity if the plugin exports them.

### From `IUnityMemoryManager.h`
- The interface GUID is `0xBAF9E57C61A811EC` (high) / `0xC5A7CC7861A811EC` (low), from `UNITY_REGISTER_INTERFACE_GUID` in that header.
- `UnityAllocator` is an opaque handle type.
- `IUnityMemoryManager` is a struct of 5 function pointers, in this order:
  1. `CreateAllocator(const char* areaName, const char* objectName) → UnityAllocator*`
  2. `DestroyAllocator(UnityAllocator* allocator)`
  3. `Allocate(UnityAllocator* allocator, size_t size, size_t align, const char* file, int32_t line) → void*`
  4. `Deallocate(UnityAllocator* allocator, void* ptr, const char* file, int32_t line)`
  5. `Reallocate(UnityAllocator* allocator, void* ptr, size_t size, size_t align, const char* file, int32_t line) → void*`
- Per the Unity Manual, the Memory Profiler shows plugin allocations under the area and object names given to `CreateAllocator`.

Get the headers from a Unity Editor's `PluginAPI` folder, or declare these layouts yourself from the facts above. Either is fine.

## 5. EOS allocator contract (public `eos_init.h`, SDK 1.19.1.2)
- **Allocate** (`EOS_AllocateMemoryFunc`): return at least `SizeInBytes` bytes at an address that is a multiple of `Alignment`. The SDK always passes a power-of-2 `Alignment`. Return null on failure.
- **Reallocate** (`EOS_ReallocateMemoryFunc`): same capacity and alignment rules for the reallocated block. Return null on failure.
- **Release** (`EOS_ReleaseMemoryFunc`): called when the SDK is done with memory from the allocate or reallocate function.
- `EOS_InitializeOptions` has `AllocateMemoryFunction`, `ReallocateMemoryFunction` and `ReleaseMemoryFunction`. If the allocator is null, the other two must be null too. The game always passes all three non-null.

The plugin meets this contract by delegation: every guarantee (alignment, capacity, contents preserved across reallocate, failure returning null) is supplied by Unity's `IUnityMemoryManager`. The plugin itself enforces nothing.

## 6. Required behaviour

### State
The library has two process-wide values: the memory-manager interface pointer and the allocator handle. Both start as null. Nothing else is stored.

### `UnityPluginLoad(interfaces)`
1. Ask `interfaces` for `IUnityMemoryManager` using the `GetInterface` slot (the by-value GUID entry, which is what the header's C++ helper `Get<IUnityMemoryManager>()` uses). Do not use `GetInterfaceSplit`; the harness trace distinguishes them. Make exactly one lookup and store the result, even if it is null.
2. If the result is non-null, call its `CreateAllocator` once with **areaName = `PortningsbolagetMemory`** and **objectName = `PortningsbolagetMemory`**. This is the label Unity's Memory Profiler shows. It matches the managed assembly name, and the Windows build uses it for both arguments. Store the returned handle, even if it is null.
3. If the memory-manager lookup returned null, make no further calls.
4. Do not check `interfaces` for null (Unity never passes null).
5. If Unity calls this a second time, repeat steps 1–3 and overwrite both stored values. Do not destroy the earlier allocator.

### `UnityPluginUnload()`
- If both the stored memory manager and the stored allocator are non-null, call `DestroyAllocator` once with the stored allocator, then set the stored allocator to null. The stored memory-manager pointer is kept.
- Otherwise do nothing. A second unload therefore does nothing.

### `Init()`
Does nothing: no Unity calls and no state change. It exists because the managed code calls it first. **All setup happens in `UnityPluginLoad`, not here.**

### `Get…FunctionPointer()`
- Each getter returns the address of its callback: allocate, reallocate or release respectively.
- The 3 addresses are non-null and distinct, and each is the same on every call for the life of the process.
- The getters don't depend on whether `UnityPluginLoad` has run and don't touch Unity.

### Allocate callback `(size, alignment)`
Make exactly one call to `IUnityMemoryManager.Allocate` with:
- the stored allocator handle
- `size` and `alignment` exactly as received
- a source-file string and line number

Return Unity's result unchanged. Do not round size or alignment, do not add a minimum alignment, do not special-case size 0, do not add headers or bookkeeping, and do not check the result for null.

### Reallocate callback `(pointer, size, alignment)`
Make exactly one call to `IUnityMemoryManager.Reallocate` with:
- the stored allocator handle
- `pointer`, `size` and `alignment` exactly as received, including a null `pointer` and size 0
- a source-file string and line number

Return Unity's result unchanged. Null-pointer and zero-size semantics are Unity's; do not special-case them.

### Release callback `(pointer)`
Make exactly one call to `IUnityMemoryManager.Deallocate` with:
- the stored allocator handle
- `pointer` exactly as received, including null
- a source-file string and line number

### File and line arguments
The `file`/`line` arguments are diagnostic only; Unity uses them for allocation call-site info. Pass a non-null string (your own source file name is fine) and any line number. The Windows build passes its own source path. These values are **not** compared by the tests.

### Callbacks after unload
After `UnityPluginUnload`, the callbacks still forward to the stored memory manager, with the now-null allocator handle. This is the Windows behaviour; do not add a guard.

## 7. Thread safety
- The callbacks run on whatever thread EOS uses: the main thread during `EOS_Initialize` and `EOS_Platform_Tick`, and EOS's own worker threads (network, HTTP, storage and so on).
- The library adds **no locking and no thread-local state**. Thread safety is whatever Unity's memory manager provides.
- The two stored values are written only by `UnityPluginLoad`/`UnityPluginUnload`, on Unity's loader and shutdown path, and only read by the callbacks. Pointer-sized reads with no synchronisation match Windows. A relaxed atomic load is an acceptable equivalent, but a mutex is not.

## 8. Failure behaviour
- **Memory manager unavailable.** Either Unity never called `UnityPluginLoad`, or `GetInterface` returned null. On Windows, the first EOS allocation then dereferences the missing interface and the process crashes. That allocation happens inside `EOS_Initialize`, called from `EpicOnlineService`'s constructor during `GameHandler.Initialize`.
  - The Mac build must also fail at that point. It must **not** fall back to `malloc` or `posix_memalign`, which would hide a broken plugin load.
  - Acceptable: terminate (e.g. `abort`) after writing a single line to stderr that names the cause. This is an open decision for the coordinator: an identical crash versus a labelled abort. Both end the process at the same point.
- **Unity `Allocate`/`Reallocate` returns null:** pass the null to EOS unchanged. EOS handles it per its contract.
- **`CreateAllocator` returned null:** forward that null handle in every call, as Windows does. Unload then makes no `DestroyAllocator` call.

## 9. Managed callers and observable call sequence (for context and test A3)
1. Unity loads the plugin and calls `UnityPluginLoad`. It does this at or before the first P/Invoke into it; whether that happens at player start or lazily is Unity's choice and is observed in A3.
2. `GameHandler.Initialize` → `UserManager.Initialize` → `new EpicOnlineService(Production)` → `MemoryWrapper.GetFunctionPointers`. That method calls, in this order:
   1. `Init`
   2. `GetAllocateFunctionPointer`
   3. `GetReallocateFunctionPointer`
   4. `GetReleaseFunctionPointer`
3. The 3 pointers go into `EOS_InitializeOptions`, and `EOS_Initialize` is called. From then on EOS calls the callbacks at will.
4. On quit: `GameHandler.OnDestroy` → `UserManager.Teardown` → `EpicOnlineService.Shutdown`. That releases the EOS platform and calls `EOS_Shutdown`, after which EOS releases its memory through the release callback.
5. Unity then unloads plugins and calls `UnityPluginUnload`.

No other managed code references this library.

## 10. Acceptance tests (verifier)
The Windows reference is always the game's original DLL, used unmodified on a Windows PC, never redistributed. "Trace" means a list of `(function, arguments, return)` records in which every pointer is replaced by a symbolic ID (first-seen order). Example IDs: `MM` for the fake manager, `A1`/`A2` for allocator handles, `P1…` for blocks.

**A1. Static** (Mac only):
- `nm -gU` lists exactly the 6 exports in §3.
- The file has an arm64 slice.
- `otool -L` shows only system libraries.
- Loading it with `dlopen` makes no calls and spawns no threads.

**A2. Fake-Unity harness, run identically on both OSes.** One small C program, built x64 on Windows and arm64 on Mac. It provides a recording fake `IUnityInterfaces` + `IUnityMemoryManager`, loads the library (`LoadLibrary` / `dlopen`), runs the scenarios below, and writes a trace. **Pass: the traces are identical, ignoring `file`/`line` values.** The harness also asserts that callback arguments arrive bit-exact and that callback returns equal what the fake returned.

| # | Scenario | Expected |
|---|---|---|
| S1 | normal | `UnityPluginLoad(fake)` makes exactly one `GetInterface` lookup (memory-manager GUID) then `CreateAllocator("PortningsbolagetMemory","PortningsbolagetMemory")` → `A1`. `Init` makes no calls. Each getter called twice gives the same non-null value, and the 3 values are distinct. Allocate over sizes {0, 1, 7, 16, 4096, 1 MiB} × alignments {1, 2, 8, 16, 64, 4096} gives one `Allocate(A1, size, align)` each, with the result passed back unchanged; the fake returns real aligned memory. Reallocate cases (null, 64, 16), (P, 0, 16) and (P, 2 MiB, 64) each give one `Reallocate(A1, …)`. Release of null and of P each give one `Deallocate(A1, ptr)`. `UnityPluginUnload` gives one `DestroyAllocator(A1)`; a second unload gives no calls. A callback after unload is forwarded with a null allocator |
| S2 | `GetInterface` returns null | no `CreateAllocator`; unload makes no calls; getters and `Init` work. In a child process, the first allocate call terminates the process on both OSes (Windows: access violation; Mac: per the §8 decision) |
| S3 | `CreateAllocator` returns null | callbacks forward a null allocator; unload makes no `DestroyAllocator` |
| S4 | `UnityPluginLoad` twice | the second load repeats the lookup and `CreateAllocator` → `A2`. `A1` is never destroyed, callbacks use `A2`, and unload destroys only `A2` |
| S5 | concurrency | 8 threads × 100,000 mixed allocate/reallocate/release against a thread-safe fake. No crash, the forwarded call counts equal the issued counts, and every result is unchanged |

**A3. In the real app** (launches the game):
- Build a *trace variant* of the Mac library (a compile-time switch, never shipped). It appends to a log file: `UnityPluginLoad` (and whether the manager was found), `Init`, the getters, per-callback counts, the set of alignments seen, the peak and final count of live blocks, and `UnityPluginUnload`.
- On the PC, a test-only proxy DLL with the same name produces the same log from the original. It is placed next to the renamed original, forwards all 6 exports, and is removed afterwards.
- Run the same path on both: boot to the main menu, wait 60 s, quit.
- **Pass if all of these hold:**
  - `UnityPluginLoad` happens before `Init` on Mac, as on Windows, with the manager found.
  - The export call order matches §9.
  - Player.log has `EOS SDK Initialized` and no `DllNotFoundException` or `EntryPointNotFoundException`.
  - EOS Connect login succeeds (the online-stack check: the `ProductUserID` player property is set).
  - The alignments seen are a subset of the Windows set, and the live-block count at unload follows the same pattern as Windows (both zero, or both the same small residue).
- Absolute allocation counts may differ, because EOS's Mac and Windows builds allocate differently.

**A4. Memory label (optional).** Only if a Unity development player is available: a Memory Profiler snapshot shows EOS memory under area/object `PortningsbolagetMemory`. The release player can't be profiled, so the S1 trace covers this.
