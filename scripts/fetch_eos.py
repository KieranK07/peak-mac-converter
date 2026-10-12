#!/usr/bin/env python3
"""fetch_eos.py: Epic's universal EOS SDK 1.19.1.2-CL53289219 dylib, named so the game's Windows-built wrapper finds it.

    python3 -I scripts/fetch_eos.py --game <Windows game dir> --out <dir>   # writes <dir>/libEOSSDK-Win64-Shipping.dylib

The game's EpicOnlineService.dll P/Invokes `DllImport("EOSSDK-Win64-Shipping")` (639 entry points; the 637 the game
can reach are all exported by the Mac dylib, the other 2 are Windows-only and unused: EOS_Platform_Create_Windows,
EOS_IntegratedPlatformOptionsContainer_Add_WindowsSteam). The dylib comes from PlayEveryWare's EOS Plugin for Unity
v6.1.0, whose Runtime/Windows/x64/EOSSDK-Win64-Shipping.dll is byte-identical to the game's (asserted below via
the pinned hash), so it is the same SDK build. Epic-signed (team 96DBZ92D3Y), x86_64+arm64.

Placement: Contents/PlugIns/libEOSSDK-Win64-Shipping.dylib. The Unity Mac player maps DllImport names through its
plugin callback (UnityPlayer.dylib: mono_set_find_plugin_callback, "Contents/PlugIns", "%s.dylib", "lib%s.dylib";
the Lethal port loads PlugIns/libsteam_api.dylib for "steam_api" this way). Unity's Mono on its own (test with
6000.3.15f1's libmonobdwgc-2.0.dylib, no player, 2026-10-07) finds only lib<name>.dylib, lib<name>.so or a bare
<name>, and only next to the calling assembly; it does NOT find <name>.dylib. The lib-prefixed name works on both
paths; the real dylib under it answers EOS_EResult_ToString(0) == "EOS_Success".
Licence: use is governed by the EOS Developer Agreement (Runtime/macOS/license.txt).
Downloads (sha256-pinned, cached under $PMC_CACHE; only the dylib and its licence are kept). stdlib only.
"""
import argparse, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C

PLUGIN = ('https://github.com/EOS-Contrib/eos_plugin_for_unity/releases/download/v6.1.0/com.playeveryware.eos-6.1.0.tgz',
          'f7bb3e481947a7a5c9ba64885759dfbd08288953ae9b173411470e5685f39d1a')
DYLIB = 'package/Runtime/macOS/libEOSSDK-Mac-Shipping.dylib'
DYLIB_SHA256 = '51b644a123d9446e1f6e7098d264f57d09826d47e1b5bbcdd1d8172a3e208f9d'
# = the plugin's package/Runtime/Windows/x64/EOSSDK-Win64-Shipping.dll (checked 2026-10-07)
GAME_SHA256 = {'Plugins/x86_64/EOSSDK-Win64-Shipping.dll': '9c498fe31c8df1d9085dfa9c71c6d50ec6b569acdb29f1decb97be105b6341dc',
               'Managed/EpicOnlineService.dll': 'bed68d1a33bd4b204d9c9aea9a3136d65376ce873c67fad9eb750c30697def07'}
OUT_NAME = 'libEOSSDK-Win64-Shipping.dylib'


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--game', required=True, help='Windows game dir (contains PEAK_Data)')
    ap.add_argument('--out', required=True, help=f'output dir (-> Contents/PlugIns); gets {OUT_NAME}')
    a = ap.parse_args()
    gdata = C.game_data(a.game)
    for rel, sha in GAME_SHA256.items():
        C.check(os.path.join(gdata, rel), sha, f'game {os.path.basename(rel)}')
    root = C.fetch(*PLUGIN, 'eos-plugin-6.1.0', lambda n: n in (DYLIB, 'package/Runtime/macOS/license.txt'))
    dylib = C.check(os.path.join(root, DYLIB), DYLIB_SHA256, 'libEOSSDK-Mac-Shipping.dylib')
    C.clone(dylib, os.path.join(a.out, OUT_NAME))
    print(f'wrote {os.path.join(a.out, OUT_NAME)}')


if __name__ == '__main__':
    main()
