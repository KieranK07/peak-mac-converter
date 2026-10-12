#!/bin/sh
# Turn your own Windows copy of PEAK into a native Apple Silicon app.
#
#   double-click "Convert PEAK.command", or ./convert.sh with no options: the Steam app you're signed into
#     installs PEAK like any game, with your own session. It never asks for your password.
#   ./convert.sh --game "/path/to/PEAK"            # use a copied Windows install instead (gets a Steam shortcut)
#   options: --out "<path>.app" (default ~/Applications/PEAK.app), --no-steam-entry (with --game),
#            --force (rebuild even if the app is up to date), --no-update-check
#
# Updating: run it again after PEAK updates. Steam gets the new build, and the app is rebuilt only when
# the game or this converter changed; otherwise it says the app is up to date. Saves and settings live outside the
# app (~/Library/Application Support/com.LandCrab.PEAK, ~/Library/Preferences/com.LandCrab.PEAK.plist).
#
# Nothing from the game, Unity or Valve is in this repository. Everything is downloaded from its official source
# or built from source here, on your Mac, into ~/Library/Caches/peak-mac-converter.
# Tests: PMC_CACHE=<dir> moves the cache; PMC_STEAM_DIR=<fake Steam folder> edits that instead of Steam's own,
# and Steam is then never closed.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
export PMC_CACHE="${PMC_CACHE:-$HOME/Library/Caches/peak-mac-converter}"
export LMC_CACHE="$PMC_CACHE"  # tools/xlat/build.sh keeps the HLSLcc source there
STEAM_DIR="${PMC_STEAM_DIR:-$HOME/Library/Application Support/Steam}"
APPID=3527290 DEPOT_ID=3527291
UNITY_VERSION=6000.3.15f1
STAMP_ATTR=com.peak-mac-converter.stamp  # extended attribute on the app: outside the code signature
OUT_APP="$HOME/Applications/PEAK.app"
GAME= STEAM_INSTALL= STEAM_ENTRY=1 FORCE= UPDATE_CHECK=1
while [ $# -gt 0 ]; do
  case $1 in
    --game) GAME=$2; shift 2 ;;
    --out) OUT_APP=$2; shift 2 ;;
    --no-steam-entry) STEAM_ENTRY=; shift ;;
    --force) FORCE=1; shift ;;
    --no-update-check) UPDATE_CHECK=; shift ;;
    *) echo "unknown option $1 (the options are listed at the top of convert.sh)"; exit 2 ;;
  esac
done
case $OUT_APP in /*) ;; *) OUT_APP="$(pwd)/$OUT_APP" ;; esac
case $OUT_APP in *.app) ;; *) echo "error: --out must end in .app" >&2; exit 2 ;; esac
say() { printf '\n==> %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }
mkdir -p "$PMC_CACHE"
# Hash of the files under these paths (build outputs and tests left out): decides what needs rebuilding.
srchash() {
  (cd "$HERE" && find "$@" -type f ! -path '*/out/*' ! -path '*/bin/*' ! -path '*/obj/*' ! -path '*/test/*' \
    ! -name '*.pyc' ! -name '.DS_Store' -print0 | sort -z | xargs -0 shasum) | shasum | cut -c1-16
}
# Steam keeps its library files in memory and writes them back on exit, so it must be closed while we edit them.
# Close it once; reopen it on exit. Never with PMC_STEAM_DIR.
close_steam() {  # $1: why
  [ -z "${PMC_STEAM_DIR:-}" ] && pgrep -x steam_osx >/dev/null || return 0
  say "Closing Steam for a moment ($1); it opens again by itself"
  osascript -e 'quit app "Steam"' >/dev/null 2>&1 || true
  for _ in $(seq 60); do pgrep -x steam_osx >/dev/null || break; sleep 1; done
  pgrep -x steam_osx >/dev/null && die "Steam didn't quit; quit it yourself and run this again"
  trap 'open -a Steam' EXIT
}
# Steam's own PEAK entry: installable and playable on macOS, Play starts this app (scripts/steam_library.py says how
# and why). A copied install (--game) gets a separate shortcut instead. $1: what to do when that fails (die, skip).
steam_entry() {
  WHAT=$1
  [ -d "$STEAM_DIR/userdata" ] || { $WHAT "no Steam on this Mac: install Steam and sign in once"; return 0; }
  set -- --app "$OUT_APP"
  [ -n "$STEAM_INSTALL" ] || set -- "$@" --game "$GAME"
  rc=0; python3 -I "$HERE/scripts/steam_library.py" "$@" --check || rc=$?
  case $rc in 0) return 0 ;; 1) ;; *) $WHAT "couldn't set up PEAK in Steam (see the message above)"; return 0 ;; esac
  close_steam "to set up PEAK for macOS in your Steam library"
  python3 -I "$HERE/scripts/steam_library.py" "$@" || $WHAT "couldn't set up PEAK in Steam (see the message above)"
}
skip() { echo "(skipped the Steam library entry: $*)"; }
add_steam_entry() { [ -z "$STEAM_ENTRY" ] || steam_entry skip; }
# A LaunchAgent that checks Steam once a day (and at login) and notifies when the game has updated.
install_update_check() {
  [ -n "$UPDATE_CHECK" ] || return 0
  cp "$HERE/scripts/update_check.sh" "$HERE/scripts/steam_library.py" "$PMC_CACHE/"
  PL="$HOME/Library/LaunchAgents/com.peak-mac-converter.update-check.plist"
  mkdir -p "$HOME/Library/LaunchAgents"
  python3 -I -c 'import plistlib, sys; plistlib.dump({"Label": "com.peak-mac-converter.update-check",
    "ProgramArguments": ["/bin/sh", sys.argv[1], sys.argv[2]], "RunAtLoad": True,
    "StartCalendarInterval": {"Hour": 12, "Minute": 0}, "ProcessType": "Background"}, open(sys.argv[3], "wb"))' \
    "$PMC_CACHE/update_check.sh" "$OUT_APP" "$PL"
  launchctl bootout "gui/$(id -u)" "$PL" 2>/dev/null || true
  launchctl bootstrap "gui/$(id -u)" "$PL"
  echo "Daily update check on (turn off: sh \"$PMC_CACHE/update_check.sh\" uninstall)"
}

# --- 0. requirements -----------------------------------------------------------------------------------
[ "$(uname -m)" = arm64 ] || die "this converter builds for Apple Silicon Macs only"
xcode-select -p >/dev/null 2>&1 || die "install Xcode Command Line Tools first: xcode-select --install"
command -v python3 >/dev/null || die "python3 not found (it comes with the Command Line Tools)"
if ! command -v dotnet >/dev/null && [ ! -x "$PMC_CACHE/dotnet/dotnet" ]; then
  say "Installing the .NET SDK (Microsoft's official installer) into the cache"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$PMC_CACHE/dotnet-install.sh"
  sh "$PMC_CACHE/dotnet-install.sh" --channel 10.0 --install-dir "$PMC_CACHE/dotnet" >/dev/null
fi
[ -x "$PMC_CACHE/dotnet/dotnet" ] && export PATH="$PMC_CACHE/dotnet:$PATH"
export DOTNET_ROLL_FORWARD=Major DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# --- 1. Unity's terms ----------------------------------------------------------------------------------
if [ ! -f "$PMC_CACHE/unity-terms-accepted" ]; then
  cat <<TERMS

This converter downloads Unity's official macOS player runtime (Unity $UNITY_VERSION, the engine version
PEAK uses) from Unity's servers. Its use is governed by Unity's terms:
  https://unity.com/legal/terms-of-service   https://unity.com/legal/editor-terms-of-service
TERMS
  printf 'Type "yes" to accept Unity'"'"'s terms and continue: '
  read -r answer || answer=
  [ "$answer" = yes ] || die "Unity's terms were not accepted"
  date > "$PMC_CACHE/unity-terms-accepted"
fi

# --- 2. your Windows game files ------------------------------------------------------------------------
# Which converter built the app: only the files that shape it count, so changes to e.g. the Steam entry or the
# update check never force a rebuild.
CONV_ID=$(srchash tools native scripts/assemble_app.py scripts/common.py scripts/build_inputsystem.py \
  scripts/build_steamworks.py scripts/fetch_eos.py scripts/patch_platforms.py)
up_to_date() {
  say "$OUT_APP is up to date ($1). Nothing to do."
  add_steam_entry
  install_update_check
  exit 0
}
STEAM_GAME="$STEAM_DIR/steamapps/common/PEAK" ACF="$STEAM_DIR/steamapps/appmanifest_$APPID.acf"
acf() { sed -n 's/.*"'"$1"'"[[:space:]]*"\([0-9]*\)".*/\1/p' "$ACF" 2>/dev/null | head -1; }
installed() { [ "$(acf StateFlags)" = 4 ] && grep -q "\"$DEPOT_ID\"" "$ACF" && [ -f "$STEAM_GAME/PEAK_Data/data.unity3d" ]; }
since() { awk -v t="$START" -v p="AppID $APPID $1" 'index($0, p) && substr($0, 2, 19) >= t' "$STEAM_DIR/logs/content_log.txt" 2>/dev/null | tail -1; }
# Steam downloads the Windows depot itself, as for any game it installs, so the converter never sees your login.
# Likely limit (not seen yet): a game update resets the app info edits, Steam then drops the files, and each
# update is a full download (about 1.6 GB).
steam_install() {
  START=$(date '+%Y-%m-%d %H:%M:%S') WAITED=0
  pgrep -x steam_osx >/dev/null || open -a Steam
  say "Steam is installing PEAK (about 1.6 GB; sign in to Steam if it asks). This window waits."
  until [ -n "$(since 'finished update')" ] && installed; do
    # after each try Steam logs "scheduler finished : removed from schedule (result No Error, state 0xc)"
    R=$(since 'scheduler finished' | sed -n 's/.*(result \(.*\), state.*/\1/p')
    [ -z "$R" ] || [ "$R" = "No Error" ] || die "Steam couldn't install PEAK: $R. Is PEAK on the Steam account that's signed in?"
    # start it; again every 30 s until Steam is up and signed in and takes it
    [ -n "$(since 'App update changed : Running')" ] || [ $((WAITED % 30)) -ne 0 ] || open "steam://validate/$APPID"
    [ "$WAITED" -lt 3600 ] || die "Steam hasn't finished installing PEAK after an hour; check its Downloads page, then run this again"
    sleep 3; WAITED=$((WAITED + 3))
  done
}
if [ -z "$GAME" ]; then
  GAME=$STEAM_GAME STEAM_INSTALL=1
  steam_entry die
  installed || steam_install
fi
[ -d "$GAME" ] || die "no folder $GAME"
GAME=$(cd "$GAME" && pwd)
[ -f "$GAME/PEAK_Data/data.unity3d" ] || die "no PEAK_Data/data.unity3d in $GAME"
UNITY=$(head -c 256 "$GAME/PEAK_Data/data.unity3d" | LC_ALL=C grep -a -o -m1 '[0-9]\{4\}\.[0-9]*\.[0-9]*f[0-9]*' || true)
[ "$UNITY" = "$UNITY_VERSION" ] || die "this game build uses Unity ${UNITY:-?}; the converter supports $UNITY_VERSION. Get a newer converter."

# Up to date? The stamp names the game build (Steam build id, or a fingerprint of the files) and this converter.
if [ -n "$STEAM_INSTALL" ]; then GAME_ID="steam-$(acf buildid)"
else GAME_ID="files-$(cd "$GAME" && find . -type f -print0 | sort -z | xargs -0 stat -f '%N %z %m' | shasum | cut -c1-16)"; fi
STAMP="game=$GAME_ID converter=$CONV_ID"
[ -z "$FORCE" ] && [ "$(xattr -p $STAMP_ATTR "$OUT_APP" 2>/dev/null)" = "$STAMP" ] && up_to_date "$GAME_ID"

# --- 3. shaders: translate the game's Direct3D shaders to Metal -----------------------------------------
TOOLS="$PMC_CACHE/tools"
ID=$(srchash tools)
if [ "$(cat "$TOOLS/.built-from" 2>/dev/null)" != "$ID" ] || [ ! -f "$TOOLS/lmc/lmc.dll" ]; then
  say "Building the shader translator (HLSLcc, MIT, from Unity's GitHub)"
  rm -rf "$TOOLS"
  sh "$HERE/tools/build.sh" "$TOOLS"
  echo "$ID" > "$TOOLS/.built-from"
fi
# Unity 6.3's class layouts (AssetRipper's type trees as shipped in UnityPy 1.25.4, MIT), converted for lmc
export LMC_TPK="$PMC_CACHE/classdata.tpk"
if [ "$(shasum -a 256 "$LMC_TPK" 2>/dev/null | cut -c1-64)" != 642a151952e78aeec48f564ea5223f67ccf7819ec1aa6f5af3d9a0b42cf885af ]; then
  U="$PMC_CACHE/unitypy-1.25.4.tar.gz"
  curl -fsSL https://files.pythonhosted.org/packages/10/a4/5b1c786cae645d30c882eac7933ab7c8916e13f1a571bf7d0f2cdc942a52/unitypy-1.25.4.tar.gz -o "$U"
  echo "91d6169d73db8eb5f11fd6531b4e5e980f53dae00d5e3cbab827bbf4765de6d3  $U" | shasum -a 256 -c - >/dev/null || die "UnityPy download checksum mismatch"
  tar -xzOf "$U" unitypy-1.25.4/UnityPy/resources/lzma.tpk > "$PMC_CACHE/unitypy-lzma.tpk"
  rm "$U"
  python3 -I "$HERE/tools/lmc/tpk_v1.py" "$PMC_CACHE/unitypy-lzma.tpk" "$LMC_TPK" $UNITY_VERSION >/dev/null
  rm "$PMC_CACHE/unitypy-lzma.tpk"
  echo "642a151952e78aeec48f564ea5223f67ccf7819ec1aa6f5af3d9a0b42cf885af  $LMC_TPK" | shasum -a 256 -c - >/dev/null || die "classdata.tpk is not the verified one"
fi
say "Translating shaders (about 3 min; afterwards only new or changed shaders are translated again)"
DATA="$PMC_CACHE/data.unity3d"
rm -f "$DATA"
dotnet "$TOOLS/lmc/lmc.dll" metalize "$GAME/PEAK_Data" "$DATA" "$PMC_CACHE/metal-cache"

# --- 4. assemble and sign the app ----------------------------------------------------------------------
# assemble_app.py builds PEAK.updating.app next to the old app, checks and signs it, then swaps it in, so a failed
# update leaves the old one working. Its icon comes from your PEAK.exe.
say "Assembling $OUT_APP (the first time it streams about 1.8 GB of Unity's installers and keeps 94 MB)"
mkdir -p "$(dirname "$OUT_APP")"
python3 -I "$HERE/scripts/assemble_app.py" --game "$GAME" --data "$DATA" --out "$OUT_APP"
rm -f "$DATA"  # the app holds an APFS clone of it
xattr -w $STAMP_ATTR "$STAMP" "$OUT_APP"

# --- 5. Steam library entry and daily update check -----------------------------------------------------
add_steam_entry
install_update_check
say "Done: $OUT_APP. Play PEAK from your Steam library."
