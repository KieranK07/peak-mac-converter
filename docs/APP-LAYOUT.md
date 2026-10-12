# App layout: where every file in `PEAK.app` comes from

Measured 2026-10-07 on a local build of `PEAK.app` (271 files, 1.8 GB). It was built with
`--data build/shaders/out/data.unity3d`, the patched bundle: 1,475,143,217 B, sha256 `e186438e…5fff`. A dry
assembly with `--allow-unpatched-data` came first and produced the same paths (270 then, before `MacMicWatchdog.dll`). `scripts/assemble_app.py` makes it. Every file was matched by sha256 to its source; Mach-O
files were matched after `codesign --remove-signature`, because the final `codesign --deep` re-signs them.
Base: the Lethal converter's `assemble_app.py` and `docs/APP-LAYOUT.md`.

Sources:
- **Support pkg:** `UnitySetup-Mac-Mono-Support-for-Editor-6000.3.15f1.pkg`, 724,929,213 B, sha256 `4606aadb…7e1b`.
  It is read once as an HTTP Range stream and the whole pkg's sha256 is checked. 108 files are kept, ~46 MB.
- **Editor pkg:** `MacEditorInstallerArm64/Unity-6000.3.15f1.pkg`, 5,112,633,639 B. It is Range-streamed only up to
  `unityjit-macos/`, which sits about 1.08 GB in. 283 files (45 MB) are kept.
- **Game:** build 25739797, `gamedata/`.
- **Ours:** the converter scripts and native builds. Each one pins its own output.

Both kept sets are pinned as a tree hash, `tree_sha256` = sha256 of the sorted `sha256  path` lines. On the PC, the
script's own extractor produced the same trees from the downloaded pkgs: support `17fb6cbd…`, editor `e6eaceae…`. The
support tree also equals `runtime/6000.3.15f1/` (`MANIFEST.sha256`).

## 1. Sources at a glance

| Source | Files | How |
|---|---|---|
| Unity support pkg | 107 | Clone from `<cache>/unity-c1aa84e375f6/support`. Contents: player exe, 4 dylibs, `unity default resources`, `MainMenu.nib` (2), `PrivacyInfo.xcprivacy`, Mono `etc/` (18), 80 `UnityEngine*.dll` |
| Unity editor pkg | 30 | Mono class libraries from `unityjit-macos/` (27, plus 3 from `Facades/`) |
| User's Windows game | 121 | 5 `.resource`, `app.info`, `boot.config`, `ScriptingAssemblies.json`, `RuntimeInitializeOnLoads.json`, 2 `StreamingAssets` files, 110 Managed DLLs |
| Ours | 8 | 4 Managed DLLs and 4 PlugIns dylibs, built by the scripts on each run |
| Patched data (`--data`) | 1 | `data.unity3d` from the shader/graphics stage |
| Generated | 4 | `Info.plist`, `DefaultPreferences.plist`, `PlayerIcon.icns`, `_CodeSignature/CodeResources` |

`--data <patched data.unity3d>` replaces the game's `data.unity3d`. Nothing else in Data changes.
- Without `--data`, the script stops. `--allow-unpatched-data` keeps the D3D-only original for a dry run.
- A `--data` file that isn't UnityFS, or that is byte-identical to the game's own file, is refused.

## 2. Outside `Contents/Resources/Data/`

| `.app` path | Source | Proof |
|---|---|---|
| `Contents/MacOS/PEAK` | pkg `Variations/macos_arm64_player_nondevelopment_mono/UnityPlayer.app/Contents/MacOS/UnityPlayer`, renamed | equal after removing the signature; arm64 |
| `Contents/Frameworks/{UnityPlayer,libmonobdwgc-2.0,libmono-native,libMonoPosixHelper}.dylib` | pkg, same folder | equal after removing the signature; arm64 |
| `Contents/Resources/MainMenu.nib/{designable,keyedobjects}.nib` | pkg `Source/Player/MacPlayer/MacPlayerEntryPoint/Resources/MainMenu.nib/` | sha256-equal |
| `Contents/Resources/PrivacyInfo.xcprivacy` | pkg `Tools/XCode/PrivacyInfo.xcprivacy` (238 B) | sha256-equal. Unity would merge in package manifests (`com.unity.modules.insights.xcprivacy`). That is App Store metadata and is never read at runtime |
| `Contents/MonoBleedingEdge/etc/mono/**` (18) | pkg `MonoBleedingEdge/etc/` | sha256-equal. Also byte-equal to the game's Windows `MonoBleedingEdge/etc` |
| `Contents/PlugIns/libsteam_api.dylib` | `build_steamworks.py` (Valve SDK 1.61, universal) | pin `b2260d2b…` checked before signing |
| `Contents/PlugIns/libEOSSDK-Win64-Shipping.dylib` | `fetch_eos.py` (EOS 1.19.1.2, universal) | pin `51b644a1…` checked before signing |
| `Contents/PlugIns/opus_egpv.dylib` | `native/opus/build.sh` (arm64) | 62/62 byte-identical to the Windows DLL |
| `Contents/PlugIns/MemoryWrapper-x64-Release.dylib` | `native/memorywrapper/build.sh` (universal) | trace parity A1+A2 |
| `Contents/Info.plist`, `Contents/Resources/{DefaultPreferences.plist,PlayerIcon.icns}` | generated (§4) | |
| `Contents/_CodeSignature/CodeResources` | `codesign --force --deep -s -`, then `--verify --deep --strict` | `Signature=adhoc`, `Identifier=com.LandCrab.PEAK` |

## 3. `Contents/Resources/Data/`

| Path | Source | Note |
|---|---|---|
| `data.unity3d` | `--data` (patched), or the game's with `--allow-unpatched-data` | sha256-equal to `build/shaders/out/data.unity3d`. All 56 serialized files are platform-patched and the API list is Metal (shader stage) |
| `resources.resource`, `sharedassets{0,1,3,4}.resource`, `app.info`, `StreamingAssets/{UnityServicesProjectConfiguration.json,SaveIcon_PS5.png}` | game, unchanged | |
| `boot.config` | game, verbatim | `gfx-threading-mode=6` stays: the Metal player keeps SplitJobs (`docs/metal-format-6.3.md` §8). The file has no empty `xrsdk-pre-init-library=` line, so Lethal's edit isn't needed |
| `ScriptingAssemblies.json` (194), `RuntimeInitializeOnLoads.json` (73) | game, plus one `MacMicWatchdog` entry each (`register_watchdog`) | game originals pinned (`7cd8e804…`, `953a59fd…`) and must re-serialize byte-exactly; the entry is appended once and the results are pinned (`d958df69…`, `f53e109a…`). Every DLL they name must be present, or the script stops |
| `Resources/unity default resources` | **pkg** `…/UnityPlayer.app/Contents/Resources/Data/Resources/` (sha256 `4c1f5760…`, Metal) | **New in 6.3:** the player keeps this file here instead of in `Contents/Resources/`. It replaces the game's D3D copy (platform 5) |
| `Managed/*.dll` (224) | §5 | |
| ~~`Plugins/`~~ | dropped (§6) | |

## 4. Generated files

`Info.plist` starts from the pkg template `MacPlayerEntryPoint/Info.plist`. The script then sets the keys that Unity's
`MacStandalonePlayerBuildProgram` writes (their names appear in that program's strings):

| Key | Value | Where it comes from |
|---|---|---|
| `CFBundleExecutable`, `CFBundleName` | `PEAK` | `app.info` line 2 |
| `CFBundleIdentifier` | `com.LandCrab.PEAK` | Unity's default `com.<company>.<product>`, from `app.info` |
| `CFBundleShortVersionString` | `2.6.b` | `version.txt` line 1, equal to PlayerSettings `bundleVersion` |
| `CFBundleVersion` | `0` | Unity's default macOS build number. Cosmetic |
| `CFBundleGetInfoString` | `Unity Player version 6000.3.15f1 (c1aa84e375f6). (c) 2005-2026 Unity Technologies. All rights reserved.` | the game's `UnityPlayer.dll` `LegalCopyright` |
| `LSApplicationCategoryType` | `public.app-category.games` | PlayerSettings `macAppStoreCategory` |
| `UnityBuildNumber` | `c1aa84e375f6` | |
| `NSMicrophoneUsageDescription` | `PEAK uses the microphone for voice chat.` | macOS refuses microphone access without it (Photon Voice records through Unity's `Microphone`). Unity's build program also refuses a build that uses the microphone without it |

More on the bundle identifier:
- It decides the prefs and save folder names.
- The player data has no `applicationIdentifier` override. Unity strips that field from player builds, so a
  developer override wouldn't show there either.
- PlayerSettings were read with UnityPy from `globalgamemanagers` inside `data.unity3d`.

Other generated files:
- **`DefaultPreferences.plist`:** `{"Screenmanager Is Fullscreen mode": "True"}`. This is the template string in
  `UnityEditor.OSXStandalone.Extensions.dll`. PEAK's `fullscreenMode` is 1 (FullScreenWindow).
- **`PlayerIcon.icns`:** made at convert time from `PEAK.exe`'s largest icon resource (256 px), with `sips` and
  `iconutil`. It is never committed (`.gitignore` has `*.icns`).

## 5. Managed DLLs (224)

| Class | Count | Source |
|---|---|---|
| Engine modules | 80 | pkg `Variations/mono/Managed/`. The same 80 names the game has, minus the three rows below |
| Engine modules with no Mac build | 2 | game `UnityEngine.NVIDIAModule.dll`, `UnityEngine.AMDModule.dll`. `ScriptingAssemblies.json` lists both and the pkg has no Mac copy, which is Lethal's approach. **First-boot check:** a call into `NVUnityPlugin`/`AMDUnityPlugin` would throw `MissingMethodException`. SRP Core carries DLSS/FSR2 code, which needed an IL patch in Lethal's HDRP |
| `UnityEngine.UI.dll` | 1 | game. It is the `com.unity.ugui` package, not an engine module |
| Mono class libraries | 30 | editor pkg `unityjit-macos/`, chosen by name: 27 from the root, plus `netstandard`, `System.Runtime` and `System.Memory` from `Facades/`. The game's copies are the Windows `unityjit-win32` builds; Lethal's test F showed those call `kernel32`/`BCrypt`. `System.Memory.dll` happens to be byte-equal to the game's |
| Ours | 4 | `MacMicWatchdog.dll` `7497020b…` (`native/micwatchdog/build_micwatchdog.py`: Roslyn 4.3.1, deterministic, against the Mac `UnityEngine.CoreModule`/`AudioModule` and the game's PhotonVoice DLLs). It restarts Photon recording when Unity's mic stalls after a device format change (AirPods); it does nothing while samples arrive. Also `Unity.InputSystem.dll` `e1a9056c…` (`build_inputsystem.py`), `com.rlabrecque.steamworks.net.dll` `9ca87350…` (`build_steamworks.py`), `Platforms.dll` `af18497e…` (`patch_platforms.py`). Each is sha256-equal to its pin and to `build/managed/final/`, whose IL diffs are `build/managed/proof_*.txt` |
| Game, unchanged | 107 | everything else, including the Windows-only GDK assemblies (below) |

The GDK assemblies (`Unity.Microsoft.GDK`, `Unity.Microsoft.GDK.Tools`, `XblPCSandbox`) stay:
- Nothing requires their removal.
- They are listed in `ScriptingAssemblies.json`, which stays verbatim, but are absent from `RuntimeInitializeOnLoads.json`.
- They are only P/Invoked if called. Game code never calls them.

## 6. Dropped on Mac (the script checks that none of these reach the app)

| Windows file | Why it isn't needed |
|---|---|
| `D3D12/D3D12Core.dll`, `UnityPlayer.dll`, `PEAK.exe`, `UnityCrashHandler64.exe` | Windows player. The Mac player replaces it |
| `MonoBleedingEdge/EmbedRuntime/{mono-2.0-bdwgc,MonoPosixHelper}.dll` | Windows Mono. The pkg's `Frameworks/` provides the Mac runtime |
| `PEAK_Data/Plugins/x86_64/{steam_api64,EOSSDK-Win64-Shipping,opus_egpv,MemoryWrapper-x64-Release}.dll` | replaced by the 4 dylibs in `Contents/PlugIns` |
| `Plugins/x86_64/{webrtc-audio,AudioIn,MemoryWrapper-x64-Debug}.dll` | not used: no WebRtcAudioDsp component, every Recorder uses Unity's mic, Debug is unused |
| `Plugins/x86_64/lib_burst_generated.dll` | Burst falls back to managed code, which boots. Performance is still to be measured |
| `Plugins/Steamworks.NET.txt` | a version note |
| `PEAK_Data/Resources/unity default resources` (D3D) | replaced by the player's Metal copy (§3) |
| Windows Mono class libraries in `Managed/` | replaced (§5) |

What the script checks, failing with a clear message:
- No `.exe` and no `steam_appid.txt` anywhere, no `.dll` outside `Data/Managed`, no `D3D12`, `EmbedRuntime` or `Data/Plugins`.
- PlugIns holds exactly the 4 dylibs, and each has an arm64 slice.
- Every `ScriptingAssemblies`/`RuntimeInitializeOnLoads` DLL exists.
- Our 4 DLLs and 2 dylibs match their pins, and so do both Unity JSON files before and after the watchdog entry.
- Both Unity trees match their pins.
- The game DLLs that our builds replace match the hashes the build scripts expect.

## 7. Steam: no `steam_appid.txt`

This follows the Lethal converter, which ships no app-id file in the app:
- Steam launches the Mac app as the real game, 3527290 (Lethal's `steam_launch.py`/`steam_appinfo.py` pattern), so
  Steam checks ownership as it does on Windows.
- `SteamManager` calls `RestartAppIfNecessary(3527290)`. A direct launch from Finder is therefore handed back to Steam.
- For a dev run outside Steam, put `steam_appid.txt` (`3527290`) in the working directory. Steam still requires the
  logged-in account to own the game.

## 8. Build and swap

- The output is built as `<name>.updating.app` next to `--out`. After every check and the ad-hoc signature pass, it is
  renamed into place. Any old app becomes `<name>.old.app` and is then deleted.
- A failed run therefore never touches the working app.
- Run time with warm caches: 15–31 s. The managed and native builds run each time.
- A first run on a new machine streams about 1.8 GB (725 MB + ~1.08 GB) and keeps 94 MB in the cache.
- `--unity-pkg-cache` (default `$PMC_CACHE` or `~/Library/Caches/peak-mac-converter`) may already contain
  `unity-c1aa84e375f6/{support,editor}`. Each set is checked against its tree pin. On this Mac, `support` is a symlink
  to `runtime/6000.3.15f1/`, and `editor` was extracted on the PC by the same extractor from the downloaded pkg.

## 9. Open before first boot

1. Steam launch integration (the PEAK version of `steam_launch.py`/`steam_appinfo.py`), or a dev `steam_appid.txt`
   in the working directory.
2. MemoryWrapper A3: confirm that Unity calls `UnityPluginLoad` for the `.dylib`. If it doesn't, ship
   `MemoryWrapper-x64-Release.bundle` instead (SPEC §2).
3. Confirm that the player finds `opus_egpv` and `libEOSSDK-Win64-Shipping` in PlugIns (it uses the `%s.dylib` and
   `lib%s.dylib` callback).
4. Check `Player.log` for NVIDIA/AMD module calls (§5) and for Burst fallback messages.
