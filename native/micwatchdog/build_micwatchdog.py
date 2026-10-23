#!/usr/bin/env python3
"""build_micwatchdog.py: compile MacMicWatchdog.dll (ours) for the macOS player, like a Unity player script assembly.

    python3 -I native/micwatchdog/build_micwatchdog.py --game <Windows game dir> --modules <Mac UnityEngine modules dir> \
        --out <dir>                                    # writes <dir>/MacMicWatchdog.dll and prints its sha256

The compiler is Roslyn 4.3.1, the one Unity 6000.3.15f1 uses, with Unity's player options and macOS defines
(scripts/common.py). It is pinned and sha256-checked like build_inputsystem.py. References:
  * netstandard 2.1 ref
  * the official 6000.3.15f1 macOS UnityEngine.CoreModule/AudioModule (support pkg Variations/mono/Managed)
  * the game's PhotonVoice.dll and PhotonVoice.API.dll (pinned to build 26155610; IL-identical to 25739797's)
The build is deterministic (-deterministic, -pathmap), so the bytes are pinned.
Why it exists: native/micwatchdog/MacMicWatchdog.cs. Needs `dotnet` (any .NET 6+ runtime). stdlib only.
"""
import argparse, os, sys, tempfile
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', '..', 'scripts'))
import common as C

NAME = 'MacMicWatchdog'
SRC = os.path.join(HERE, NAME + '.cs')
GAME_REFS = {'PhotonVoice.dll': 'e893d7bbed2524d44b09d5c73bdfb5940ab951ba529bebd9d96429c8a34a45be',
             'PhotonVoice.API.dll': '50f82b46dfdf3a81c6278c4755da94ddb95a4d7656b4527509c4d51aef748ba0'}
MAC_REFS = ('UnityEngine.CoreModule.dll', 'UnityEngine.AudioModule.dll')
OUT_SHA256 = '293a0c641ef8732488db88c5fbf5a0f7d9c947482a53883f6030d7ae02ee54c0'  # build 26155610 refs; IL-identical to 7497020b


def build(game, modules, out_dir):
    managed = os.path.join(C.game_data(game), 'Managed')
    refs = [C.netstandard()] + [os.path.join(modules, f) for f in MAC_REFS]
    refs += [C.check(os.path.join(managed, f), sha, f'game {f}') for f, sha in GAME_REFS.items()]
    for r in refs:
        if not os.path.isfile(r):
            C.die(f'reference {r} not found')
    with tempfile.TemporaryDirectory() as b:
        out = os.path.join(b, NAME + '.dll')
        args = C.unity_csc_args(out, b, f'{HERE}=Assets/{NAME}', unsafe=False)
        args += [f'-define:{d}' for d in C.unity_defines('osx')] + [f'-r:{r}' for r in refs] + [SRC]
        C.csc(args, b, C.roslyn())
        got = C.sha256(out)
        if got != OUT_SHA256:
            C.die(f'built {NAME}.dll sha256 {got} is not the pinned {OUT_SHA256} (source or toolchain changed?)')
        C.clone(out, os.path.join(out_dir, NAME + '.dll'))
    print(f'built {os.path.join(out_dir, NAME + ".dll")} ({got})')
    return got


if __name__ == '__main__':
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--game', required=True, help='Windows game dir (contains PEAK_Data)')
    ap.add_argument('--modules', required=True, help='Unity 6000.3.15f1 macOS UnityEngine modules (Variations/mono/Managed)')
    ap.add_argument('--out', required=True, help='output dir; gets MacMicWatchdog.dll')
    a = ap.parse_args()
    build(a.game, a.modules, a.out)
