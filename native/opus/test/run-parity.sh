#!/bin/sh
# Parity check: out/opus_egpv.dylib (this Mac) vs PEAK's opus_egpv.dll (Windows PC over `ssh pc`), same input,
# same cases (Program.cs). Passes when both traces are byte-identical. Test audio is made here with `say` in
# $WORK (outside the repo) and never committed. Needs: ../build.sh done, dotnet on both machines.
#   PC_DIR   scratch folder on the PC (default C:/peak-opus-parity)
#   PC_DLL   the game's DLL on the PC (default: Steam's PEAK install)
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
WORK=${WORK:-${TMPDIR:-/tmp}/pmc-opus-parity}
PC_DIR=${PC_DIR:-C:/peak-opus-parity}
PC_DLL=${PC_DLL:-C:/Program Files (x86)/Steam/steamapps/common/PEAK/PEAK_Data/Plugins/x86_64/opus_egpv.dll}
DLL_SHA=4F74F4A21512F4CC13BEE08C043CF98E0A4C92C02D265283058A19F7C70D7EE7  # PEAK build 25739797
mkdir -p "$WORK"
ps() {  # run a PowerShell script on the PC (encoded, so cmd.exe quoting doesn't apply)
  enc=$({ printf '%s' '$ProgressPreference="SilentlyContinue"; $ErrorActionPreference="Stop"; '; printf '%s' "$1"; } |
        iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n')
  ssh pc "powershell -NoProfile -NonInteractive -EncodedCommand $enc" 2>&1 | grep -v '^#< CLIXML\|^<Objs' | tr -d '\r'
}

[ -f "$WORK/input.f32" ] || {
  say -o "$WORK/speech.wav" --file-format=WAVE --data-format=LEF32@48000 "Hey, can you hear me? I'm climbing the cliff \
right now, grab the rope and pass me a ration. Watch out for the jellyfish on the beach. Okay, the campfire is lit, \
everyone come over here before the night gets cold. Three, two, one, jump."
  dotnet build "$HERE/test.csproj" -c Release -o "$WORK/mac" -v q -nologo >/dev/null
  dotnet "$WORK/mac/test.dll" gen "$WORK/speech.wav" "$WORK/input.f32"
}
dotnet build "$HERE/test.csproj" -c Release -o "$WORK/mac" -v q -nologo >/dev/null  # picks up a rebuilt dylib

ps "New-Item -ItemType Directory -Force '$PC_DIR/src' | Out-Null"
scp -q "$HERE/Program.cs" "$HERE/test.csproj" "pc:$PC_DIR/src/"
scp -q "$WORK/input.f32" "pc:$PC_DIR/input.f32"
ps "if ((Get-FileHash '$PC_DLL').Hash -ne '$DLL_SHA') { throw 'unexpected opus_egpv.dll' }
    & 'C:/Program Files/dotnet/dotnet.exe' build '$PC_DIR/src/test.csproj' -c Release -r win-x64 -o '$PC_DIR/bin' -v q -nologo | Out-Null
    Copy-Item '$PC_DLL' '$PC_DIR/bin/'
    cd '$PC_DIR'; & 'C:/Program Files/dotnet/dotnet.exe' bin/test.dll run input.f32 trace-windows.txt | Out-Null
    (Get-FileHash trace-windows.txt).Hash" > "$WORK/windows.sha"

(cd "$WORK" && dotnet mac/test.dll run input.f32 trace-macos.txt) | tee "$WORK/summary-macos.txt"
mac=$(shasum -a 256 "$WORK/trace-macos.txt" | cut -c1-64 | tr a-f A-F); win=$(tail -1 "$WORK/windows.sha")
if [ "$mac" = "$win" ]; then echo "PARITY OK: traces byte-identical (sha256 $mac)"; exit 0; fi
echo "PARITY FAIL: macOS $mac, Windows $win; fetching the Windows trace for a per-case diff" >&2
scp -q "pc:$PC_DIR/trace-windows.txt" "$WORK/"
dotnet "$WORK/mac/test.dll" compare "$WORK/trace-macos.txt" "$WORK/trace-windows.txt"; exit 1
