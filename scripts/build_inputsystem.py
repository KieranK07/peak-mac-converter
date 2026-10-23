#!/usr/bin/env python3
"""build_inputsystem.py: compile Unity's Input System 1.19.0 for the macOS player, like Unity's own Mac build.

    python3 -I scripts/build_inputsystem.py --game <Windows game dir> --out <dir>   # writes <dir>/Unity.InputSystem.dll

PEAK ships the Windows compile of com.unity.inputsystem 1.19.0 (`#if UNITY_STANDALONE_WIN`): it has no macOS Xbox
HID layouts and no OSX GameController (Nimbus+) support, so Mac pads aren't gamepads. This builds the same
package from Unity's registry with Unity 6000.3.15f1's compiler, options and defines for a macOS Mono player.

Proof (`--platform win`, then scripts/il_diff.py against the game's DLL): the Windows build of the same inputs is
IL-identical to the shipped DLL, manifest included, except Unity's editor-only source-path table
(UnitySourceGeneratedAssemblyMonoScriptTypes_v1 + its two data blobs, made by an editor source generator; the
player never names it). The macOS build differs from that Windows build only in the platform code:
  InputSystem.PerformDefaultPluginInitialization  + OSXSupport.Initialize()           (InputSystem.cs:3788)
  XInputSupport.Initialize   XInputControllerWindows -> 4 macOS Xbox layouts + matchers  (XInputSupport.cs:23,27)
  SwitchSupportHID.Initialize  4 PDP/PowerA Switch-pad matchers left out on macOS   (SwitchSupportHID.cs:86)
  types  - XInputControllerWindows(+State)   + OSXSupport, OSXGameController (Nimbus+), XboxGamepadMacOS* (+states)
References are the game's own (Windows) UnityEngine modules: a build against the official 6000.3.15f1 macOS player
modules (UnitySetup-Mac-Mono-Support pkg) is IL-identical, manifest included (bytes differ only in MVID/PDB id),
so the 725 MB pkg isn't needed here.

Downloads (sha256-pinned, cached under $PMC_CACHE): com.unity.inputsystem 1.19.0 (Unity Companion License),
Roslyn 4.3.1 and netstandard 2.1 ref (MIT, nuget.org). Needs `dotnet` (any .NET 6+ runtime). stdlib only.
"""
import argparse, json, os, re, sys, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C

VERSION = '1.19.0'
PACKAGE = (f'https://download.packages.unity.com/com.unity.inputsystem/-/com.unity.inputsystem-{VERSION}.tgz',
           '4a08d3d4ad260da37a0dd76c4575bb860a1d85fe305cf76b7c1a0de70496389c')
PKG_DIR = 'com.unity.inputsystem@21a28c3a6c83'  # Unity 6 PackageCache name: first 12 hex of the tarball's sha1
GAME_SHA256 = 'acfe2788d3f891022178c0ee1bf4d307473a99079925b3cbb2418dde8e615850'  # build 26155610 (IL-identical to 25739797's)
# The build is deterministic (same bytes from any cache path). Pinned after the il_diff proof above.
# Build 26155610 rebuilt the game's UnityEngine modules (our references), so both pins moved: the win build is now
# byte-identical to the shipped DLL, and the osx build is IL-identical to the one pinned for build 25739797 (e1a9056c).
OUT_SHA256 = {'osx': '53aa06b749824d23fb5ad189a8b3db2a3c25948ff481e4fb7afecd0d24c06c4d',
              'win': 'acfe2788d3f891022178c0ee1bf4d307473a99079925b3cbb2418dde8e615850'}
GAME_PACKAGES = {'com.unity.ugui': '2.0.0'}  # no xr.oculus / xr.googlevr / xr.openvr / xr.windowsmr in PEAK


def vkey(v):
    """Unity version -> comparable tuple: 6000.3.15f1 -> (6000, 3, 15, 2, 1)."""
    # ponytail: a release without suffix sorts below its own betas (6000.1.0 < 6000.1.0b9); no asmdef here hits it
    return tuple(int(x) if x.isdigit() else 'abfp'.index(x) for x in re.findall(r'\d+|[abfp]', v))


def in_range(version, expr):
    """Unity asmdef versionDefines expression: '1.2' (>=), '[a,b)', '(a,b]', '[a]'."""
    v = vkey(version)
    if expr[0] not in '[(':
        return v >= vkey(expr)
    lo, _, hi = expr[1:-1].partition(',')
    if not _:
        return v == vkey(lo)
    return ((not lo or (v >= vkey(lo) if expr[0] == '[' else v > vkey(lo))) and
            (not hi or (v <= vkey(hi) if expr[-1] == ']' else v < vkey(hi))))


def version_defines(asmdef, versions):
    return sorted({d['define'] for d in asmdef['versionDefines']
                   if d['name'] in versions and in_range(versions[d['name']], d['expression'])})


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--game', required=True, help='Windows game dir (contains PEAK_Data)')
    ap.add_argument('--out', required=True, help='output dir; gets Unity.InputSystem.dll')
    ap.add_argument('--platform', choices=('osx', 'win'), default='osx', help='win = the proof build')
    a = ap.parse_args()
    gdata = C.game_data(a.game)
    managed = os.path.join(gdata, 'Managed')
    C.check(os.path.join(managed, 'Unity.InputSystem.dll'), GAME_SHA256, 'game Unity.InputSystem.dll')

    pkg = os.path.join(C.fetch(*PACKAGE, f'inputsystem-{VERSION}', lambda n: n == 'package/LICENSE.md' or (
        n.startswith('package/InputSystem/') and n.endswith(('.cs', '.asmdef')))), 'package')
    roslyn, netstd = C.roslyn(), C.netstandard()

    # Sources: Unity's asmdef rule, every .cs under the asmdef's folder except folders with their own asmdef
    # (Plugins/InputForUI is Unity.InputSystem.ForUI). Editor/ stays in: it's `#if UNITY_EDITOR` code, as in Unity.
    # Order = Unity 6's (ordinal path order); it decides the type order in the DLL.
    top = os.path.join(pkg, 'InputSystem')
    srcs = []
    for dp, dn, fn in os.walk(top):
        if dp != top and any(f.endswith('.asmdef') for f in fn):
            dn[:] = []
            continue
        srcs += [os.path.join(dp, f) for f in fn if f.endswith('.cs')]
    srcs.sort()

    asmdef = json.load(open(os.path.join(top, 'Unity.InputSystem.asmdef')))
    names = json.load(open(os.path.join(gdata, 'ScriptingAssemblies.json')))['names']
    versions = dict(GAME_PACKAGES, Unity=C.UNITY)
    versions.update({'com.unity.modules.' + n[12:-10].lower(): '1.0.0' for n in names
                     if n.startswith('UnityEngine.') and n.endswith('Module.dll')})
    defines = C.unity_defines(a.platform) + version_defines(asmdef, versions)

    refs = [netstd, os.path.join(managed, 'UnityEngine.UI.dll'), os.path.join(managed, 'UnityEngine.dll')] + sorted(
        os.path.join(managed, f) for f in os.listdir(managed) if f.startswith('UnityEngine.') and f.endswith('Module.dll'))
    with tempfile.TemporaryDirectory() as build:
        out = os.path.join(build, 'Unity.InputSystem.dll')
        args = C.unity_csc_args(out, build, f'{pkg}=Library/PackageCache/{PKG_DIR}', asmdef['allowUnsafeCode'])
        args += [f'-define:{d}' for d in defines] + [f'-r:{r}' for r in refs] + srcs
        print(f'compiling Unity.InputSystem {VERSION} for {a.platform} ({len(srcs)} files)')
        C.csc(args, build, roslyn)
        got = C.sha256(out)
        if got != OUT_SHA256[a.platform]:
            C.die(f'built DLL sha256 {got} is not the verified {OUT_SHA256[a.platform]}')
        C.clone(out, os.path.join(a.out, 'Unity.InputSystem.dll'))
    print(f'built {os.path.join(a.out, "Unity.InputSystem.dll")} ({got})')


if __name__ == '__main__':
    main()
