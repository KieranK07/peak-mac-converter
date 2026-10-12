#!/bin/sh
# Daily check, run by a LaunchAgent that convert.sh installs (it copies this script and steam_library.py into its
# cache, next to SteamCMD): has PEAK updated on Steam since the Mac app was built? If so, show a notification.
# Asks Steam anonymously (no login) for the public build id.
#
#   sh update_check.sh <path to PEAK.app>   # the check
#   sh update_check.sh uninstall            # remove the daily check
set -u
CACHE=$(cd "$(dirname "$0")" && pwd)
STEAM="${PMC_STEAM_DIR:-$HOME/Library/Application Support/Steam}"
LABEL=com.peak-mac-converter.update-check
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
if [ "${1:-}" = uninstall ]; then
  launchctl bootout "gui/$(id -u)" "$PLIST" 2>/dev/null
  rm -f "$PLIST"
  echo "Daily PEAK update check removed."
  exit 0
fi
APP=${1:?usage: update_check.sh <app> | uninstall}
notify() {  # $1 title, $2 text
  osascript -e "display notification \"$2\" with title \"$1\" sound name \"default\""
}

# SteamCMD with its own home and a no-op launchctl, as in convert.sh (keeps it off the Steam app's files and jobs)
mkdir -p "$CACHE/steamcmd-home" "$CACHE/steamcmd-bin"
printf '#!/bin/sh\nexit 0\n' > "$CACHE/steamcmd-bin/launchctl" && chmod +x "$CACHE/steamcmd-bin/launchctl"
LATEST=$(PATH="$CACHE/steamcmd-bin:$PATH" HOME="$CACHE/steamcmd-home" "$CACHE/steamcmd/steamcmd.sh" +login anonymous +app_info_update 1 +app_info_print 3527290 +quit 2>/dev/null |
  awk '/"branches"/ {b=1} b && /"public"/ {p=1} p && /"buildid"/ {gsub(/"/, "", $2); print $2; exit}')
# Steam replaces its cached app info for the game now and then, which takes the Play button away again
if [ -f "$STEAM/steamapps/common/PEAK/.peak-mac-converter" ] && ! python3 -I "$CACHE/steam_library.py" --check-play >/dev/null 2>&1; then
  notify "Steam reset PEAK's Play button" "Double-click Convert PEAK.command to bring it back (under a minute)."
fi
[ -n "$LATEST" ] || exit 0  # offline or Steam down: try again next time

# The app's stamp has the build it was made from when SteamCMD downloaded the game. For a copied install it
# doesn't, so remember the last build seen and tell the user once per new build.
BUILT=$(xattr -p com.peak-mac-converter.stamp "$APP" 2>/dev/null | sed -n 's/^game=steam-\([0-9]*\) .*/\1/p')
SEEN=$(cat "$CACHE/seen-buildid" 2>/dev/null)
echo "$LATEST" > "$CACHE/seen-buildid"
if [ -n "$BUILT" ]; then
  [ "$LATEST" = "$BUILT" ] && exit 0
elif [ -z "$SEEN" ] || [ "$LATEST" = "$SEEN" ]; then
  exit 0
fi
notify "PEAK updated on Steam" "Double-click Convert PEAK.command to update your Mac app (about 5 min)."
