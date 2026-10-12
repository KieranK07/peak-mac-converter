#!/bin/sh
# Builds the Mac stand-in for PEAK's MemoryWrapper-x64-Release.dll (SPEC.md), universal arm64+x86_64:
#   $OUT/MemoryWrapper-x64-Release.dylib     ship this in <App>.app/Contents/PlugIns/
#   $OUT/MemoryWrapper-x64-Release.bundle    same code as a CFBundle; ship instead only if A3 shows Unity
#                                            doesn't call UnityPluginLoad for the .dylib (SPEC §2)
#   $OUT/harness                             A2 fake-Unity harness (host arch), run by test.sh
#   $OUT/harness.exe                         same harness for Windows x64 (only if MinGW is installed)
# Needs only Xcode Command Line Tools. OUT defaults to ./out.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=${OUT:-$HERE/out}
N=MemoryWrapper-x64-Release
CF="-arch arm64 -arch x86_64 -mmacosx-version-min=11.0 -O2 -Wall -Wextra -Werror -fvisibility=hidden"
mkdir -p "$OUT/$N.bundle/Contents/MacOS"

clang $CF -dynamiclib -install_name "@rpath/$N.dylib" -o "$OUT/$N.dylib" "$HERE/memorywrapper.c"
clang $CF -bundle -o "$OUT/$N.bundle/Contents/MacOS/$N" "$HERE/memorywrapper.c"
cat > "$OUT/$N.bundle/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleExecutable</key><string>$N</string>
	<key>CFBundleIdentifier</key><string>local.peak-mac-converter.memorywrapper</string>
	<key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
	<key>CFBundleName</key><string>$N</string>
	<key>CFBundlePackageType</key><string>BNDL</string>
	<key>CFBundleVersion</key><string>1</string>
</dict>
</plist>
EOF
clang -O2 -Wall -Wextra -Werror -o "$OUT/harness" "$HERE/harness.c"
# Windows x64 harness for the verifier, if a MinGW cross compiler is installed (else: cl /O2 harness.c on the PC)
if command -v x86_64-w64-mingw32-gcc >/dev/null; then x86_64-w64-mingw32-gcc -O2 -Wall -Wextra -Werror -static -o "$OUT/harness.exe" "$HERE/harness.c"; fi
echo "built $OUT/$N.dylib, $OUT/$N.bundle, $OUT/harness"
