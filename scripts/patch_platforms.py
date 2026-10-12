#!/usr/bin/env python3
"""patch_platforms.py: make Portningsbolaget.Platforms report the Windows platform on macOS, so Windows players see the same platform.

    python3 -I scripts/patch_platforms.py --game <Windows game dir> --out <dir>   # writes <dir>/Platforms.dll

PlatformUtility.InitializePlatform() maps Application.platform OSXPlayer/OSXEditor to PlatformFamily.OSX (2). On a
Mac that changes the room-code platform letter (E-G instead of A-D) and makes every Windows peer look
cross-platform (a Mac host with Crossplay off kicks them). One byte, one instruction: that case's `ldc.i4.2`
becomes `ldc.i4.1` (PlatformFamily.Windows), exactly what the WindowsPlayer case stores. Every other platform,
the switch table and the rest of the file are untouched (proof: scripts/il_diff.py game vs output = that one
instruction). stdlib only.
"""
import argparse, hashlib, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C

SHA256_IN = '725e3dd979c725cd26b00ed6d2762de091bf8a797f5e98ca8d7cb481edf868e6'  # build 25739797
# the OSX case's ldc.i4.2 (0x18) becomes ldc.i4.1 (0x17); the input and output hashes pin everything else
OFFSET, OLD, NEW = 0x893, '18', '17'
SHA256_OUT = 'af18497e27f3340cbabb27750bdd80884dbc8a119453c162f921857b17541ee6'


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--game', required=True, help='Windows game dir (contains PEAK_Data)')
    ap.add_argument('--out', required=True, help='output dir; gets Platforms.dll')
    a = ap.parse_args()
    src = C.check(os.path.join(C.game_data(a.game), 'Managed', 'Platforms.dll'), SHA256_IN, 'game Platforms.dll')
    b = bytearray(open(src, 'rb').read())
    n = len(OLD) // 2
    if b[OFFSET:OFFSET + n] != bytes.fromhex(OLD):
        C.die('unexpected bytes at the patch site')
    b[OFFSET:OFFSET + n] = bytes.fromhex(NEW)
    got = hashlib.sha256(b).hexdigest()
    if got != SHA256_OUT:
        C.die(f'patched sha256 {got} is not the verified {SHA256_OUT}')
    os.makedirs(a.out, exist_ok=True)
    with open(os.path.join(a.out, 'Platforms.dll'), 'wb') as f:
        f.write(b)
    print(f'wrote {os.path.join(a.out, "Platforms.dll")} ({got})')


if __name__ == '__main__':
    main()
