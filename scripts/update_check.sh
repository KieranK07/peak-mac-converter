#!/bin/sh
# Daily check, run by a LaunchAgent that convert.sh installs (it copies this script and steam_library.py into its
# cache): has Steam got a new build of PEAK since the Mac app was built? If so, show a notification.
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

# The app's stamp has the Steam build it was made from; an app built from a copied install has none.
BUILT=$(xattr -p com.peak-mac-converter.stamp "$APP" 2>/dev/null | sed -n 's/^game=steam-\([0-9]*\) .*/\1/p')
[ -n "$BUILT" ] || exit 0
HAVE=$(sed -n 's/.*"buildid"[[:space:]]*"\([0-9]*\)".*/\1/p' "$STEAM/steamapps/appmanifest_3527290.acf" 2>/dev/null | head -1)
# A game update also replaces Steam's cached app info, which takes the Play button away again
if [ "$HAVE" != "$BUILT" ] || ! python3 -I "$CACHE/steam_library.py" --check-play >/dev/null 2>&1; then
  notify "PEAK updated on Steam" "Double-click Convert PEAK.command to update your Mac app."
fi
