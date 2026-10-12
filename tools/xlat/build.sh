#!/bin/sh
# Build libxlat.dylib: Unity's open-source HLSLcc (MIT, pinned), patched for Unity 2022.3 Metal conventions
# (patch_hlslcc.py), plus our RDEF rebuild (rdef.cpp) and C ABI (xlat.cpp). Output: $OUT (default ./out).
# Needs: Xcode Command Line Tools (clang), git, python3.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
CACHE=${LMC_CACHE:-$HOME/Library/Caches/peak-mac-converter}
OUT=${OUT:-$HERE/out}
HLSLCC_COMMIT=3ea1fcd6bd0ac445bc078de0bf32f0950188577b  # 2020-09-23, Unity-Technologies/HLSLcc master
SRC=$CACHE/src/hlslcc
if [ ! -d "$SRC/.git" ]; then
  git clone -q https://github.com/Unity-Technologies/HLSLcc.git "$SRC"
fi
git -C "$SRC" -c advice.detachedHead=false checkout -q "$HLSLCC_COMMIT"
W=$(mktemp -d "${TMPDIR:-/tmp}/lmc-hlslcc.XXXXXX")  # private build dir: concurrent builds don't collide
trap 'rm -rf "$W"' EXIT
git -C "$SRC" archive "$HLSLCC_COMMIT" | tar -x -C "$W"
python3 -I "$HERE/patch_hlslcc.py" "$W"
mkdir -p "$W/obj" "$OUT"
cd "$W/obj"
for f in ../src/cbstring/*.c; do clang -c -O2 -w -arch arm64 -I../src/cbstring "$f"; done
for f in ../src/*.cpp; do clang++ -c -O2 -w -std=c++14 -arch arm64 -I.. -I../include -I../src/internal_includes -I../src -I../src/cbstring "$f" & done; wait
clang++ -std=c++14 -O2 -w -arch arm64 -shared -fvisibility=hidden -I.. -I../include -I../src/internal_includes -I../src \
  "$HERE/xlat.cpp" ./*.o -o "$OUT/libxlat.dylib"
codesign -f -s - "$OUT/libxlat.dylib" 2>/dev/null
echo "built $OUT/libxlat.dylib"
