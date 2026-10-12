# PEAK for Mac

Native Apple Silicon version of PEAK, built from your own Steam copy. It launches from PEAK's normal Play button in
Steam, with the overlay working.

You need to own PEAK on Steam. There are no game files in this repo, everything gets downloaded and built on your Mac.
Not affiliated with Landfall or Aggro Crab.

## Setup

1. Get an Apple Silicon Mac with about 8 GB free, plus:
   - Xcode Command Line Tools: `xcode-select --install`
   - Rosetta (for Valve's SteamCMD, which checks for updates): `softwareupdate --install-rosetta --agree-to-license`
2. Double click `Convert PEAK.command`. If macOS blocks it, right click it and hit Open.
3. Type `yes` for Unity's terms. Steam's console then opens, paste the line it put on your clipboard and hit
   Return. Steam downloads the game with your normal login, so it never asks for your password.
4. Wait about 15 min. Steam closes for a second near the end and opens back up.
5. Hit Play on PEAK in Steam.

When PEAK updates, run it again.

## Known issues

- Not tested with Windows players yet
- Steam Cloud saves don't sync
- Mouse sensitivity might feel a little off
- macOS asks for mic access again after each rebuild

## How it works

Steam downloads your Windows copy. The converter then grabs Unity's official Mac player for the same version,
translates the shaders to Metal, rebuilds the few Windows-only plugins and points Steam's Play button at the Mac app.
The full file list is in [docs/APP-LAYOUT.md](docs/APP-LAYOUT.md).
