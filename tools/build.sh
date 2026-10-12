#!/bin/sh
# Build the shader tools into $1: lmc (with libxlat.dylib beside lmc.dll, so the object cache key covers the
# translator) and mtlcheck. Needs: .NET 10 SDK, Xcode Command Line Tools, git, python3.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=$(mkdir -p "$1" && cd "$1" && pwd)
OUT="$OUT/xlat" "$HERE/xlat/build.sh" >/dev/null
dotnet build "$HERE/lmc/lmc.csproj" -c Release -v q -nologo -o "$OUT/lmc" >/dev/null
cp "$OUT/xlat/libxlat.dylib" "$OUT/lmc/"
swiftc -O -o "$OUT/mtlcheck" "$HERE/xlat/mtlcheck.swift"
echo "built $OUT/lmc/lmc.dll, $OUT/mtlcheck"
