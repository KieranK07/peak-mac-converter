#!/bin/sh
# Builds out/opus_egpv.dylib (macOS arm64), the Mac stand-in for PEAK's Photon Voice opus_egpv.dll
# ("libopus v1.1.1-beta-39-g976c8ad"): xiph libopus fc0276fa (v1.1.1-beta-38), float, configured like the
# Windows build, plus opus_egpv_ctl.c (fixed-argument ctl exports). Exports exactly the DLL's 57 names
# (exports.txt). Needs only Xcode Command Line Tools and curl. Downloads are pinned by sha256 and cached in
# ${PMC_CACHE:-~/Library/Caches/peak-mac-converter}/dl; intermediates go to a temp dir deleted on exit.
# Parity check against the Windows DLL: test/ (see test/run-parity.sh).
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
DL=${PMC_CACHE:-$HOME/Library/Caches/peak-mac-converter}/dl
OUT=$HERE/out
B=$(mktemp -d "${TMPDIR:-/tmp}/pmc-opus.XXXXXX"); trap 'rm -rf "$B"' EXIT INT TERM
JOBS=$(sysctl -n hw.ncpu)
export MACOSX_DEPLOYMENT_TARGET=11.0
mkdir -p "$DL" "$OUT"

fetch() {  # fetch URL SHA256 FILE
  if [ ! -f "$DL/$3" ] || [ "$(shasum -a 256 "$DL/$3" | cut -c1-64)" != "$2" ]; then
    echo "fetching $3"
    curl -fsSL -o "$DL/$3.tmp" "$1"
    got=$(shasum -a 256 "$DL/$3.tmp" | cut -c1-64)
    [ "$got" = "$2" ] || { echo "$3: sha256 $got, expected $2" >&2; rm -f "$DL/$3.tmp"; exit 1; }
    mv "$DL/$3.tmp" "$DL/$3"
  fi
}
OPUS=fc0276fad4ca6aa4b83230329f9fe5ad8b60a621  # v1.1.1-beta-38, the Windows DLL's base
fetch https://github.com/xiph/opus/archive/$OPUS.tar.gz \
  9886925df8520c9232b813a071c154f1fb2cc1b9a21571c1323b0e4aec296876 opus-$OPUS.tar.gz
fetch https://raw.githubusercontent.com/DLTcollab/sse2neon/v1.9.1/sse2neon.h \
  78632498a57bf7e080e84cb8d74c47ce93b204cece77eab79de2908eb8bc92ed sse2neon-1.9.1.h
O=$B/opus; mkdir "$O" "$B/sse2neon" "$B/obj"
tar xzf "$DL/opus-$OPUS.tar.gz" -C "$O" --strip-components 1
cp "$DL/sse2neon-1.9.1.h" "$B/sse2neon/sse2neon.h"

# The Windows build's configuration (win32/config.h + VS project, MSVC x64):
#   float build, OPUS_BUILD, USE_ALLOCA, __SSE__ (selects the SSE float kernels in celt/x86/pitch_sse.h, built
#   here via x86compat/ + sse2neon), float2int = cvtss2si (round half to even, = lrintf: HAVE_LRINTF).
#   MSVC x64 never fuses multiply-adds: -ffp-contract=off.
# The DLL's version string is "libopus v1.1.1-beta-39-g976c8ad".
# The ctls are renamed to *_ctl_va so opus_egpv_ctl.c can export fixed-argument ones under the real names.
CF="-arch arm64 -O2 -fvisibility=hidden -ffp-contract=off -ffile-prefix-map=$B/= -ffile-prefix-map=$HERE/="
OF="$CF -w -DOPUS_BUILD -DUSE_ALLOCA -D__SSE__=1 -DHAVE_LRINTF -DHAVE_LRINT
    -DPACKAGE_VERSION=\"v1.1.1-beta-39-g976c8ad\" -Dopus_encoder_ctl=opus_encoder_ctl_va -Dopus_decoder_ctl=opus_decoder_ctl_va
    -I$O/include -I$O/celt -I$O/silk -I$O/silk/float -I$O/src -I$HERE/x86compat -I$B/sse2neon"
mk() { sed -n "/^$2 *=/,/^[[:space:]]*$/p" "$O/$1" | tr ' \\' '\n\n' | grep -E '\.c$'; }
SRCS="$(mk celt_sources.mk CELT_SOURCES) $(mk silk_sources.mk SILK_SOURCES) $(mk silk_sources.mk SILK_SOURCES_FLOAT)
      $(mk opus_sources.mk OPUS_SOURCES) $(mk opus_sources.mk OPUS_SOURCES_FLOAT)"

n=0
par() { { "$@" || touch "$B/failed"; } & n=$((n + 1)); [ $((n % JOBS)) -ne 0 ] || wait; }
for f in $SRCS; do par clang $OF -c "$O/$f" -o "$B/obj/$(echo "$f" | tr / _ | sed 's/\.c$/.o/')"; done
par clang $CF -Wall -DOPUS_BUILD -I$O/include -c "$HERE/opus_egpv_ctl.c" -o "$B/obj/opus_egpv_ctl.o"
wait; [ ! -e "$B/failed" ] || { echo "compile failed" >&2; exit 1; }

clang -arch arm64 -dynamiclib -o "$OUT/opus_egpv.dylib" "$B"/obj/*.o -install_name @rpath/opus_egpv.dylib \
  -Wl,-exported_symbols_list,"$HERE/exports.txt" -Wl,-dead_strip
codesign -f -s - "$OUT/opus_egpv.dylib" 2>/dev/null

nm -gU "$OUT/opus_egpv.dylib" | awk '$2 == "T" {print $3}' | sort > "$B/got.txt"
sort "$HERE/exports.txt" | cmp -s - "$B/got.txt" || { echo "exports differ from exports.txt" >&2; exit 1; }
echo "built $OUT/opus_egpv.dylib ($(wc -l < "$B/got.txt" | tr -d ' ') exports)"
