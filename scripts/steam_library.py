#!/usr/bin/env python3
"""steam_library.py: make Steam install PEAK (app 3527290) on this Mac and start the converted app from its Play button.

    python3 -I scripts/steam_library.py --app <PEAK.app> [--game <copied Windows install>] [--check]
    python3 -I scripts/steam_library.py --check-play    # exit 0 if Steam's app cache still offers Play on macOS

PEAK calls SteamAPI.RestartAppIfNecessary(3527290) at startup: a process Steam didn't start as app 3527290 quits
and asks Steam to run 3527290. So the Mac app is started by Steam's own PEAK entry, as in the Lethal converter
(its steam_appinfo.py + steam_launch.py; see its README):
1. appcache/appinfo.vdf: add `macos` to PEAK's common/oslist, to the oslist of its first Windows launch entry
   ("PEAK using DX12") and to its depot's (3527291). Without the first two Steam shows "not available". Without the
   depot's, Steam on macOS "reconfigures" an install ("removed depots 3527291") and deletes the game's files.
2. steamapps/appmanifest_3527290.acf: if PEAK has no install record, a minimal one asking for an install, as Steam
   writes when you hit Install. Steam then downloads the Windows depot into steamapps/common/PEAK with your own
   session (convert.sh starts it with steam://validate/3527290 and waits).
3. userdata/<id>/config/localconfig.vdf: PEAK's launch options become `"<app binary>" %command%`, so Steam runs the
   bundle's binary (Unity ignores the PEAK.exe path %command% expands to).
Steam checks the licence, starts the binary as 3527290 (SteamAppId in its environment) and injects the overlay:
RestartAppIfNecessary returns false, SteamAPI_Init connects as PEAK, playtime/presence/invites are the real game's.
A launch from Finder is handed to Steam by RestartAppIfNecessary and ends up the same way.

A copied install (--game) gets a non-Steam shortcut instead: Exe = the bundle's binary, launch options
`SteamAppId=3527290 %command%` (so RestartAppIfNecessary returns false; SteamAPI_Init still needs the account to own
PEAK) and PEAK's artwork from appcache/librarycache/3527290. The overlay may not survive there (Lethal: Steam
re-associates the process).

Steam must be closed: it rewrites these files from memory on exit. Edited files are backed up as .bak (appinfo.vdf
once, as appinfo.vdf.pmc-bak). PMC_STEAM_DIR points it at another Steam folder (tests); the "Steam is running"
guard is only for the real one. Exit codes: 0 done/ok, 1 --check: not set up yet, 3 error. stdlib only.
"""
import argparse, binascii, glob, hashlib, os, re, shutil, struct, subprocess, sys

APPID, DEPOT, NAME = 3527290, 3527291, 'PEAK'
REAL = os.path.expanduser('~/Library/Application Support/Steam')
STEAM = os.environ.get('PMC_STEAM_DIR') or REAL
SHORTCUT_OPTIONS = f'SteamAppId={APPID} %command%'


def die(msg):
    print(f'steam_library: {msg}', file=sys.stderr)
    sys.exit(3)


def steam_running():
    return STEAM == REAL and subprocess.run(['pgrep', '-x', 'steam_osx'], capture_output=True).returncode == 0


def executable(app):
    return os.path.join(app, 'Contents/MacOS', os.path.basename(app)[:-len('.app')])


def backup_write(path, data, mode='w'):
    if os.path.exists(path):
        shutil.copy2(path, path + '.bak')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, mode, **({} if 'b' in mode else {'encoding': 'utf-8'})) as f:
        f.write(data)


# --- 1. appcache/appinfo.vdf (binary, version 29) ---------------------------------------------------------------
# Header magic, universe, int64 offset of the key-name table. Then apps: appid, size, info state, last updated, PICS
# token (u64), sha1 of the text form, change number, sha1 of the binary form, binary KeyValues whose keys index the
# name table. The binary sha1 is recomputed (and first reproduced for the untouched entry, as a self-test). The text
# sha1 is of Valve's server text, which the cache doesn't keep: it stays, so Steam still sees the entry as current.
# Steam rewrites the entry on store changes (new change number); update_check.sh notices and convert.sh re-applies.
APPINFO = os.path.join(STEAM, 'appcache/appinfo.vdf')
HEADER = struct.Struct('<4IQ20sI20s')


def read_names(b):
    off = struct.unpack_from('<q', b, 8)[0]
    names, pos = [], off + 4
    for _ in range(struct.unpack_from('<I', b, off)[0]):
        end = b.index(b'\0', pos)
        names.append(b[pos:end].decode('utf-8', 'surrogateescape'))
        pos = end + 1
    return names, off


def parse_kv(b, pos, names):
    """Binary KeyValues -> [(type, key, value)] in file order; returns (items, end)."""
    items = []
    while True:
        t = b[pos]; pos += 1
        if t == 8:
            return items, pos
        key = names[struct.unpack_from('<I', b, pos)[0]]; pos += 4
        if t == 0:
            value, pos = parse_kv(b, pos, names)
        elif t == 1:
            end = b.index(b'\0', pos); value = bytes(b[pos:end]); pos = end + 1
        elif t == 2:
            value = struct.unpack_from('<i', b, pos)[0]; pos += 4
        elif t == 7:
            value = struct.unpack_from('<Q', b, pos)[0]; pos += 8
        else:
            die(f'appinfo.vdf: unsupported value type {t}')
        items.append((t, key, value))


def encode_kv(items, names):
    out = bytearray()
    for t, key, value in items:
        out += bytes([t]) + struct.pack('<I', names.index(key))
        if t == 0: out += encode_kv(value, names)
        elif t == 1: out += value + b'\0'
        elif t == 2: out += struct.pack('<i', value)
        elif t == 7: out += struct.pack('<Q', value)
    out.append(8)
    return bytes(out)


def find(items, *path):
    """(parent list, (type, key, value)) of the item at path, or None."""
    for t, key, value in items:
        if key == path[0]:
            return (items, (t, key, value)) if len(path) == 1 else find(value, *path[1:]) if t == 0 else None
    return None


def load_appinfo():
    b = bytearray(open(APPINFO, 'rb').read())
    if struct.unpack_from('<I', b, 0)[0] != 0x07564429:
        die('unknown appinfo.vdf version (expected 29); Steam changed its format')
    names, names_off = read_names(b)
    pos = 16
    while True:
        appid, size = struct.unpack_from('<II', b, pos)
        if appid in (0, APPID):
            break
        pos += 8 + size
    if appid != APPID:
        die("PEAK is not in Steam's app cache yet: open PEAK's page in your Steam library once, then run this again")
    head = list(HEADER.unpack_from(b, pos))
    start = pos + HEADER.size
    items, end = parse_kv(b, start, names)
    if end != pos + 8 + size:
        die('appinfo.vdf: PEAK entry length mismatch')
    if encode_kv(items, names) != bytes(b[start:end]) or hashlib.sha1(b[start:end]).digest() != head[7]:
        die("could not reproduce PEAK's appinfo entry or its checksum; not editing")
    return b, pos, head, items, names, names_off, start, end


def play_oslists(items):
    """The oslist values that decide Play and installing on macOS: common/oslist, the first Windows launch entry's
    and the depot's (a Windows-only depot is removed from a Mac install, files and all)."""
    common, launch = find(items, 'appinfo', 'common', 'oslist'), find(items, 'appinfo', 'config', 'launch')
    if not common or not launch:
        die("no common/oslist or config/launch in PEAK's app info")
    depot = find(items, 'appinfo', 'depots', str(DEPOT), 'config', 'oslist')
    for t, _, entry in launch[1][2]:
        hit = find(entry, 'config', 'oslist') if t == 0 else None
        if hit and 'windows' in hit[1][2].decode().split(',') and not find(entry, 'config', 'BetaKey'):
            return [common, hit] + ([depot] if depot else [])
    die('PEAK has no Windows launch entry')


def has_macos(hit):
    return 'macos' in hit[1][2].decode().split(',')


def play_ok():
    return all(map(has_macos, play_oslists(load_appinfo()[3])))


def enable_play():
    b, pos, head, items, names, names_off, start, end = load_appinfo()
    hits = play_oslists(items)
    if all(map(has_macos, hits)):
        return
    for parent, (t, key, value) in hits:
        if not has_macos((parent, (t, key, value))):
            parent[parent.index((t, key, value))] = (t, key, ','.join(filter(None, value.decode().split(',') + ['macos'])).encode())
    new = encode_kv(items, names)
    head[1] = HEADER.size - 8 + len(new)
    head[7] = hashlib.sha1(new).digest()
    b[pos:end] = HEADER.pack(*head) + new
    struct.pack_into('<q', b, 8, names_off + len(new) - (end - start))  # the name table moved
    if not os.path.exists(APPINFO + '.pmc-bak'):
        shutil.copy2(APPINFO, APPINFO + '.pmc-bak')
    open(APPINFO + '.pmc-tmp', 'wb').write(b)
    os.replace(APPINFO + '.pmc-tmp', APPINFO)
    print('Steam now offers Play on PEAK on macOS')


# --- 2. steamapps/appmanifest_3527290.acf: Steam's install record ---------------------------------------------
MANIFEST = os.path.join(STEAM, f'steamapps/appmanifest_{APPID}.acf')


def request_install():
    """Install requested (StateFlags 1026: update required + started); Steam fills in the rest as it downloads."""
    if not os.path.exists(MANIFEST):
        backup_write(MANIFEST, dump([('AppState', [('appid', str(APPID)), ('Universe', '1'), ('name', NAME),
                                                   ('StateFlags', '1026'), ('installdir', NAME)])]))
        print('Steam will install PEAK when it opens')


# --- 3. localconfig.vdf (text VDF): PEAK's launch options --------------------------------------------------------
# "key" "value" pairs and "key" { ... } blocks, parsed into ordered lists and written back in Steam's tab layout.
def parse(text):
    tokens = re.findall(r'"((?:[^"\\]|\\.)*)"|([{}])', text)
    pos = 0
    def block():
        nonlocal pos
        out = []
        while pos < len(tokens):
            s, brace = tokens[pos]
            pos += 1
            if brace == '}':
                return out
            nxt_s, nxt_brace = tokens[pos]
            pos += 1
            out.append((s, block()) if nxt_brace == '{' else (s, nxt_s))
        return out
    return block()


def dump(items, depth=0):
    tabs = '\t' * depth
    return ''.join(f'{tabs}"{k}"\n{tabs}{{\n{dump(v, depth + 1)}{tabs}}}\n' if isinstance(v, list)
                   else f'{tabs}"{k}"\t\t"{v}"\n' for k, v in items)


def child(items, key, create=True):
    for k, v in items:
        if k.lower() == key.lower() and isinstance(v, list):
            return v
    if create:
        items.append((key, []))
        return items[-1][1]


def peak_config(tree, create=True):
    apps = tree
    for k in ('UserLocalConfigStore', 'Software', 'Valve', 'Steam', 'apps'):
        apps = child(apps, k, create)
        if apps is None:
            return None
    return child(apps, str(APPID), create)


def launch_options(app):
    return '\\"' + executable(app) + '\\" %command%'  # VDF-escaped quotes around the path


def launch_set(path, want):
    cfg = peak_config(parse(open(path, encoding='utf-8').read()), create=False)
    return cfg is not None and dict(kv for kv in cfg if not isinstance(kv[1], list)).get('LaunchOptions') == want


def set_launch(path, want):
    text = open(path, encoding='utf-8').read()
    tree = parse(text)
    if dump(tree) != text:
        die(f'{path}: unexpected format; not editing it')
    cfg = peak_config(tree)
    i = next((i for i, (k, v) in enumerate(cfg) if k == 'LaunchOptions'), None)
    if i is None:
        cfg.append(('LaunchOptions', want))
    else:
        cfg[i] = ('LaunchOptions', want)
    backup_write(path, dump(tree))
    print(f'{path}: PEAK launch options set')


# --- fallback: a non-Steam shortcut (shortcuts.vdf, binary VDF) --------------------------------------------------
# 0x00 map start, 0x01 string, 0x02 int32, 0x08 map end; keys and strings NUL-terminated UTF-8.
ART = os.path.join(STEAM, f'appcache/librarycache/{APPID}')


def read_vdf(b):
    pos = 0
    def cstr():
        nonlocal pos
        end = b.index(b'\0', pos)
        s = b[pos:end].decode('utf-8', 'surrogateescape')
        pos = end + 1
        return s
    def node():
        nonlocal pos
        d = {}
        while True:
            t = b[pos]; pos += 1
            if t == 8:
                return d
            k = cstr()
            if t == 0: d[k] = node()
            elif t == 1: d[k] = cstr()
            elif t == 2: d[k] = struct.unpack_from('<i', b, pos)[0]; pos += 4
            else: die(f'shortcuts.vdf: unsupported value type {t}')
    return node()


def write_vdf(d):
    out = bytearray()
    def node(d):
        for k, v in d.items():
            key = k.encode('utf-8', 'surrogateescape') + b'\0'
            if isinstance(v, dict): out.extend(b'\0' + key); node(v); out.append(8)
            elif isinstance(v, int): out.extend(b'\2' + key + struct.pack('<i', v))
            else: out.extend(b'\1' + key + v.encode('utf-8', 'surrogateescape') + b'\0')
    node(d)
    out.append(8)
    return bytes(out)


def shortcuts(vdf):
    return read_vdf(open(vdf, 'rb').read()).get('shortcuts', {}) if os.path.exists(vdf) else {}


def ours(entry, app):
    return entry.get('Exe') == f'"{executable(app)}"'


def has_shortcut(vdf, app):
    return any(ours(v, app) and v.get('AppName') == NAME and v.get('LaunchOptions') == SHORTCUT_OPTIONS
               for v in shortcuts(vdf).values())


def shortcut_appid(exe, name):
    """Steam's id for a shortcut: crc32(exe + name) with the top bit set, stored as a signed int32."""
    return struct.unpack('<i', struct.pack('<I', (binascii.crc32((exe + name).encode()) & 0xffffffff) | 0x80000000))[0]


def copy_art(grid, appid):
    """PEAK's library artwork (Steam keeps it in hashed subfolders) under Steam's custom-artwork names:
    <id>p cover, <id> banner, <id>_hero, <id>_logo, <id>_icon. Returns the icon path ('' if none)."""
    os.makedirs(grid, exist_ok=True)
    uid = appid & 0xffffffff
    for srcs, dst in ((('library_600x900.jpg',), f'{uid}p.jpg'), (('library_header.jpg', 'header.jpg'), f'{uid}.jpg'),
                      (('library_hero.jpg',), f'{uid}_hero.jpg'), (('logo.png',), f'{uid}_logo.png')):
        src = next((f for s in srcs for f in sorted(glob.glob(os.path.join(ART, '**', s), recursive=True))), None)
        if src:
            shutil.copyfile(src, os.path.join(grid, dst))
    # the community icon: the one loose <hash>.jpg at the top
    icons = [f for f in sorted(glob.glob(os.path.join(ART, '*.jpg'))) if not os.path.basename(f).startswith(('header', 'library'))]
    if not icons:
        return ''
    shutil.copyfile(icons[0], os.path.join(grid, f'{uid}_icon.jpg'))
    return os.path.join(grid, f'{uid}_icon.jpg')


def add_shortcut(vdf, app):
    s = shortcuts(vdf)
    exe = f'"{executable(app)}"'
    appid = shortcut_appid(exe, NAME)
    entry = {'appid': appid, 'AppName': NAME, 'Exe': exe, 'StartDir': f'"{os.path.dirname(executable(app))}"',
             'icon': copy_art(os.path.join(os.path.dirname(vdf), 'grid'), appid), 'ShortcutPath': '',
             'LaunchOptions': SHORTCUT_OPTIONS, 'IsHidden': 0, 'AllowDesktopConfig': 1, 'AllowOverlay': 1, 'OpenVR': 0,
             'Devkit': 0, 'DevkitGameID': '', 'DevkitOverrideAppID': 0, 'LastPlayTime': 0, 'FlatpakAppID': '', 'tags': {}}
    key = next((k for k, v in s.items() if ours(v, app) or v.get('AppName') == NAME), str(len(s)))
    s[key] = {**s.get(key, {}), **entry}
    backup_write(vdf, write_vdf({'shortcuts': s}), 'wb')
    print(f'{vdf}: shortcut "{NAME}" -> {app}')


def remove_shortcut(vdf, app):
    s = shortcuts(vdf)
    gone = [k for k, v in s.items() if ours(v, app)]
    if not gone:
        return
    grid = os.path.join(os.path.dirname(vdf), 'grid')
    for k in gone:
        for f in glob.glob(os.path.join(grid, f'{s[k]["appid"] & 0xffffffff}*')):
            os.remove(f)
    kept = [v for k, v in s.items() if k not in gone]
    backup_write(vdf, write_vdf({'shortcuts': {str(i): v for i, v in enumerate(kept)}}), 'wb')
    print(f'{vdf}: old "{NAME}" shortcut removed')


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--app', help='the converted PEAK.app')
    ap.add_argument('--game', help="a copied Windows install: gets a non-Steam shortcut (default: Steam's own install)")
    ap.add_argument('--check', action='store_true', help='exit 0 if already set up, 1 if not (changes nothing)')
    ap.add_argument('--check-play', action='store_true', help="exit 0 if Steam's app cache offers Play on macOS")
    a = ap.parse_args()
    if a.check_play:
        sys.exit(0 if play_ok() else 1)
    if not a.app:
        ap.error('--app is required')
    app = os.path.abspath(a.app.rstrip('/'))
    if not app.endswith('.app') or '"' in app or '\\' in app:
        die(f'{app}: need a .app path without quotes or backslashes')
    # numeric folders only: an old SteamCMD anonymous login leaves a userdata/anonymous behind
    accounts = [c for c in glob.glob(os.path.join(STEAM, 'userdata/*/config')) if os.path.basename(os.path.dirname(c)).isdigit()]
    if not accounts:
        die('no Steam account on this Mac: sign in to Steam once first')
    configs = [c for c in (os.path.join(d, 'localconfig.vdf') for d in accounts) if os.path.isfile(c)]
    vdfs = [os.path.join(d, 'shortcuts.vdf') for d in accounts]
    real, want = not a.game, launch_options(app)
    if real:
        done = (play_ok() and os.path.isfile(MANIFEST) and configs and all(launch_set(c, want) for c in configs)
                and not any(ours(v, app) for f in vdfs for v in shortcuts(f).values()))
    else:
        done = all(has_shortcut(f, app) for f in vdfs)
    if a.check or done:
        sys.exit(0 if done else 1)
    if steam_running():
        die('quit Steam first (it rewrites its library files when it exits)')
    if real:
        enable_play()
        request_install()
        for c in configs:
            set_launch(c, want)
        for f in vdfs:
            remove_shortcut(f, app)
        print(f'Play on PEAK in Steam now starts {app}')
    else:
        for f in vdfs:
            add_shortcut(f, app)
        print(f'PEAK shortcut in Steam now starts {app}')


if __name__ == '__main__':
    main()
