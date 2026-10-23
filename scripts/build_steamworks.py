#!/usr/bin/env python3
"""build_steamworks.py: Steamworks.NET for the macOS player + Valve's universal libsteam_api.dylib (SDK 1.61).

    python3 -I scripts/build_steamworks.py --game <Windows game dir> --out <dir>
        # writes <dir>/com.rlabrecque.steamworks.net.dll and <dir>/libsteam_api.dylib

PEAK ships the Windows compile of Steamworks.NET (`DllImport("steam_api64")`, callback/struct Pack = 8). On macOS
Valve's SDK packs callbacks to 4 (VALVE_CALLBACK_PACK_SMALL), so e.g. LobbyCreated_t's lobby id would be read at
the wrong offset. This rebuilds the same source, commit 5289ecd8 (the game's DLL is a master snapshot there:
Version.cs says 2024.8.0 / SDK 1.61; tag 2024.8.0 is SDK 1.60), as Unity 6000.3.15f1 compiles the embedded package
Packages/com.rlabrecque.steamworks.net for a macOS Mono player. Same assembly name.

Proof (`--platform win`, then scripts/il_diff.py): the Windows build is IL-identical to the game's DLL except
Unity's editor-only source-path table (UnitySourceGeneratedAssemblyMonoScriptTypes_v1). The macOS build differs
from it only in the platform bits: Packsize.value 8 -> 4, the 217 `.pack 8` structs -> `.pack 4`, Packsize.Test()'s
expected sizes (32/616 -> 24/612), and the library names steam_api64 -> steam_api (963 pinvokes) and
sdkencryptedappticket64 -> sdkencryptedappticket (11), with their two `.module extern` rows and NativeMethods
constants (il_diff.py --subst for each name; nothing else in the renamed members changes). As for InputSystem,
building against the official macOS engine modules instead of the game's gives identical IL.

The dylib comes from Steamworks.NET's release 2025.161.0 unitypackage: Valve-signed (team MXGJJ98X76), universal,
the same file as in the commit's tree, shipped beside a steam_api64.dll byte-identical to the game's, bundle
version 1.61, and its 1,089 exports equal the game's steam_api64.dll's.

Downloads (sha256-pinned, cached under $PMC_CACHE): Steamworks.NET source (MIT) at the commit, the 2025.161.0
unitypackage (dylib: Valve Steamworks SDK redistributable), Roslyn 4.3.1 + netstandard 2.1 ref (MIT, nuget.org).
Needs `dotnet` (any .NET 6+ runtime). stdlib only.
"""
import argparse, os, sys, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C

COMMIT = '5289ecd8a3de6c0076efe096c36b95087c441eff'
SOURCE = (f'https://github.com/rlabrecque/Steamworks.NET/archive/{COMMIT}.tar.gz',
          'b995d79708ada4cf901d20840d731d239d3217d7f83c6bdc85a07f563a3704f2')
RUNTIME = f'Steamworks.NET-{COMMIT}/com.rlabrecque.steamworks.net/Runtime/'
RELEASE = ('https://github.com/rlabrecque/Steamworks.NET/releases/download/2025.161.0/'
           'Steamworks.NET_2025.161.0.unitypackage',
           'ef14fd0d1e1d1445b50374fb72e66a2701761effa92210cdb8f1d209bcf40be3')
DYLIB_GUID = '804ae5441da39374086dbff7d4955f60'  # .../Plugins/steam_api.bundle/Contents/MacOS/libsteam_api.dylib
DYLIB_SHA256 = 'b2260d2b2ff6ac8d2d10770047967ceb18022fc5c27f94e3246bd7d2a1da82c0'
NAME = 'com.rlabrecque.steamworks.net'
GAME_SHA256 = {f'Managed/{NAME}.dll': '17afda90ce1056dae23633edfdd4fd8c6920e65d011881adc4cab7b6a42c8e85',
               'Plugins/x86_64/steam_api64.dll': '670d654aa3255c5061cf0236a1cdbb2f5076cbd5ae44f613b6f4921558e83e2d'}
OUT_SHA256 = {'osx': '9ca8735065de971e7890ea54edc5102cad86d399dbdc97761b43de6f59661678',
              'win': 'ba694f5704f013cbe81774d6a018b0d4a34194c02c717a8e2a3b2784633a8452'}


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--game', required=True, help='Windows game dir (contains PEAK_Data)')
    ap.add_argument('--out', required=True, help=f'output dir; gets {NAME}.dll and libsteam_api.dylib')
    ap.add_argument('--platform', choices=('osx', 'win'), default='osx', help='win = the proof build (DLL only)')
    a = ap.parse_args()
    gdata = C.game_data(a.game)
    for rel, sha in GAME_SHA256.items():
        C.check(os.path.join(gdata, rel), sha, f'game {os.path.basename(rel)}')
    managed = os.path.join(gdata, 'Managed')

    src = C.fetch(*SOURCE, f'steamworks.net-{COMMIT[:8]}', lambda n: n.startswith(RUNTIME) and n.endswith('.cs') or
                  n == f'Steamworks.NET-{COMMIT}/LICENSE.txt')
    runtime = os.path.join(src, RUNTIME)
    srcs = sorted(os.path.join(dp, f) for dp, _, fn in os.walk(runtime) for f in fn if f.endswith('.cs'))
    refs = [C.netstandard(), os.path.join(managed, 'UnityEngine.dll')] + sorted(
        os.path.join(managed, f) for f in os.listdir(managed) if f.startswith('UnityEngine.') and f.endswith('Module.dll'))
    with tempfile.TemporaryDirectory() as build:
        out = os.path.join(build, f'{NAME}.dll')
        args = C.unity_csc_args(out, build, f'{runtime.rstrip("/")}=Packages/{NAME}/Runtime', unsafe=False)
        args += [f'-define:{d}' for d in C.unity_defines(a.platform)] + [f'-r:{r}' for r in refs] + srcs
        print(f'compiling Steamworks.NET {COMMIT[:8]} for {a.platform} ({len(srcs)} files)')
        C.csc(args, build, C.roslyn())
        got = C.sha256(out)
        if got != OUT_SHA256[a.platform]:
            C.die(f'built DLL sha256 {got} is not the verified {OUT_SHA256[a.platform]}')
        C.clone(out, os.path.join(a.out, f'{NAME}.dll'))
    print(f'built {os.path.join(a.out, NAME + ".dll")} ({got})')
    if a.platform == 'osx':
        pkg = C.fetch(*RELEASE, 'steamworks.net-2025.161.0', lambda n: n == f'{DYLIB_GUID}/asset')
        dylib = C.check(os.path.join(pkg, DYLIB_GUID, 'asset'), DYLIB_SHA256, 'libsteam_api.dylib')
        C.clone(dylib, os.path.join(a.out, 'libsteam_api.dylib'))
        print(f'wrote {os.path.join(a.out, "libsteam_api.dylib")}')


if __name__ == '__main__':
    main()
