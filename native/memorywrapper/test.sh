#!/bin/sh
# SPEC §10 A1 (static checks) and A2 (fake-Unity trace) on the Mac build from build.sh.
# A2 runs the harness on the .dylib and the .bundle, natively and (if Rosetta is installed) on the x86_64
# slices, and every trace must equal trace-mac.txt, the reference the Windows run is compared against.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=${OUT:-$HERE/out}
N=MemoryWrapper-x64-Release
DYLIB=$OUT/$N.dylib BUNDLE=$OUT/$N.bundle/Contents/MacOS/$N
EXPECT="_GetAllocateFunctionPointer _GetReallocateFunctionPointer _GetReleaseFunctionPointer _Init _UnityPluginLoad _UnityPluginUnload"
fail() { echo "FAIL: $*"; exit 1; }

for f in "$DYLIB" "$BUNDLE"; do
  lipo -archs "$f" | grep -qw arm64 || fail "$f has no arm64 slice"
  for a in $(lipo -archs "$f"); do
    got=$(nm -gU -arch "$a" "$f" | awk '{print $3}' | sort | xargs)
    [ "$got" = "$EXPECT" ] || fail "$f ($a) exports: $got"
  done
  deps=$(otool -L "$f" | grep '^	' | grep -v "$N" | grep -v '^	/usr/lib/libSystem.B.dylib ' || true)
  [ -z "$deps" ] || fail "$f non-system deps: $deps"
  ! otool -l "$f" | grep -qE '__mod_init_func|__init_offsets|__mod_term_func' || fail "$f has static constructors"
done
echo "A1 pass: 6 exports, arm64 slice ($(lipo -archs "$DYLIB")), libSystem only, no static constructors"

T=$(mktemp -d "${TMPDIR:-/tmp}/mw-a2.XXXXXX"); trap 'rm -rf "$T"' EXIT INT TERM
runs="native"
arch -x86_64 /usr/bin/true 2>/dev/null && clang -arch x86_64 -O2 -o "$T/harness-x86_64" "$HERE/harness.c" && runs="native x86_64"
for r in $runs; do
  h=$OUT/harness; [ "$r" = native ] || h=$T/harness-x86_64
  for f in "$DYLIB" "$BUNDLE"; do
    "$h" "$f" "$T/trace" 2> "$T/err" || { cat "$T/err"; fail "harness ($r) on $f"; }
    grep -q "^MemoryWrapper: IUnityMemoryManager unavailable" "$T/err" || fail "S2 child didn't print the labelled abort line"
    cmp -s "$T/trace" "$HERE/trace-mac.txt" || { diff "$HERE/trace-mac.txt" "$T/trace" | head -20; fail "trace ($r, $f) != trace-mac.txt"; }
  done
done
echo "A2 pass ($runs; dylib + bundle): traces equal trace-mac.txt, all harness assertions held"
