#!/usr/bin/env python3
"""assemble_app.py: build PEAK.app (native Apple Silicon) from Unity's Mac player, the user's game and our pieces.

    python3 -I scripts/assemble_app.py --game <Windows game dir> --data <patched data.unity3d> --out <dir>/PEAK.app \
        [--unity-pkg-cache <dir>] [--allow-unpatched-data]

Builds <dir>/PEAK.updating.app, checks it, signs it ad hoc and only then swaps it in for <dir>/PEAK.app.
Sources (docs/APP-LAYOUT.md has the file-by-file mapping and the evidence):
  * Unity's official MacStandaloneSupport pkg for 6000.3.15f1, read once with an HTTP Range stream and checked
    against its pinned sha256: the arm64 non-development Mono player (UnityPlayer.app/Contents), the macOS
    UnityEngine.* engine modules, Mono etc/, the Info.plist template, MainMenu.nib, PrivacyInfo. Only those
    files are kept (unity-<hash>/support in the cache, ~46 MB); the pkg is never stored.
  * Unity's official Mac editor pkg, Range-streamed: only the macOS Mono class libraries (unityjit-macos) are
    kept; the stream stops right after them.
  * The user's Windows game: every Data file and every other Managed DLL; app.info names; the .exe's icon.
  * Ours, built here by the other converter scripts (they pin their own outputs): build_inputsystem.py,
    build_steamworks.py, fetch_eos.py, patch_platforms.py, native/opus/build.sh, native/memorywrapper/build.sh,
    native/micwatchdog/build_micwatchdog.py (MacMicWatchdog.dll, registered in the two Unity JSON files).
  * --data: the shader/graphics-patched data.unity3d (the game's own is D3D-only; --allow-unpatched-data uses it
    anyway, for a dry assembly).
No steam_appid.txt: like the Lethal converter, Steam launches the app as the real game (app 3527290) and checks
ownership; SteamAPI.RestartAppIfNecessary sends a direct launch back through Steam. stdlib only.
"""
import argparse, filecmp, hashlib, json, os, plistlib, re, shutil, struct, subprocess, sys, tempfile
import urllib.request, zlib
import xml.etree.ElementTree as ET
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C
import build_inputsystem, build_steamworks, fetch_eos, patch_platforms
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO, 'native/micwatchdog'))
import build_micwatchdog

UNITY_VERSION, UNITY_HASH = '6000.3.15f1', 'c1aa84e375f6'
BASE = f'https://download.unity3d.com/download_unity/{UNITY_HASH}'
SUPPORT_URL = f'{BASE}/MacEditorTargetInstaller/UnitySetup-Mac-Mono-Support-for-Editor-{UNITY_VERSION}.pkg'
SUPPORT_SIZE, SUPPORT_SHA256 = 724929213, '4606aadbb6d5977a8355962a6c4708d3bd5e08d3789b5027da275ef1b2b47e1b'
EDITOR_URL = f'{BASE}/MacEditorInstallerArm64/Unity-{UNITY_VERSION}.pkg'
EDITOR_SIZE = 5112633639  # sha256 1811fce5…3a10; streamed only up to the class libraries, so each file is pinned

PLAYER = 'Variations/macos_arm64_player_nondevelopment_mono/UnityPlayer.app/Contents/'
ENTRY = 'Source/Player/MacPlayer/MacPlayerEntryPoint/'
MODULES = 'Variations/mono/Managed/'
SUPPORT_WANT = (PLAYER, MODULES, 'MonoBleedingEdge/etc/', ENTRY + 'Info.plist', ENTRY + 'Resources/MainMenu.nib/',
                'Tools/XCode/PrivacyInfo.xcprivacy')
BCL_DIR = 'Unity/Unity.app/Contents/Resources/Scripting/MonoBleedingEdge/lib/mono/unityjit-macos/'  # moved in Unity 6; ~1.08 GB into the pkg
# tree_sha256 of the files kept from each pkg (all of them; any change, extra or missing file stops the run)
SUPPORT_TREE = '17fb6cbdb1698f474c0555f862e49489f818ea6ad07264b047d8d2533fb70a76'  # 108 files
EDITOR_TREE = 'e6eaceae5e255b985275b194c1b068ad34efaf380a66dc4f29aca53a8cba6cd0'  # 283 files, 45 MB

# Ours: file -> pinned sha256 (the pins live in the scripts that build them; their proofs are in docs/APP-LAYOUT.md)
MANAGED_OURS = {'Unity.InputSystem.dll': build_inputsystem.OUT_SHA256['osx'],
                'com.rlabrecque.steamworks.net.dll': build_steamworks.OUT_SHA256['osx'],
                'Platforms.dll': patch_platforms.SHA256_OUT,
                'MacMicWatchdog.dll': build_micwatchdog.OUT_SHA256}
PLUGINS = {'libsteam_api.dylib': build_steamworks.DYLIB_SHA256, fetch_eos.OUT_NAME: fetch_eos.DYLIB_SHA256,
           'opus_egpv.dylib': None, 'MemoryWrapper-x64-Release.dylib': None}  # None: built here, not pinned
COPYRIGHT = '(c) 2005-2026 Unity Technologies. All rights reserved.'  # the game's UnityPlayer.dll LegalCopyright
MIC_TEXT = 'PEAK uses the microphone for voice chat.'
# MacMicWatchdog registration: the game's two Unity JSON files (build 25739797) before and after our one entry each
WATCHDOG = build_micwatchdog.NAME
WATCHDOG_RIOL = {'assemblyName': WATCHDOG, 'nameSpace': '', 'className': WATCHDOG, 'methodName': 'Init',
                 'loadTypes': 0, 'isUnityClass': False}  # 0 = RuntimeInitializeLoadType.AfterSceneLoad
JSON_PINS = {'ScriptingAssemblies.json': ('7cd8e8043949a2380c5de76c94f181c95c386d9b44231f0f3d8fdc22680ab332',
                                          'd958df69aa4a1fa7abe4302d846d8cd3b03adc68e60670250209d2ae5619aaf8'),
             'RuntimeInitializeOnLoads.json': ('953a59fd7feda9014df16100737cecc52d6e01c1b129e1de81b2749f2d2b18c4',
                                               'f53e109a039494f8d01af4e50df1e0b8903267713f437fab6b11e2fa58d57f0f')}


die = C.die


def chunks(src, start, length):
    """Bytes [start, start+length) of a local file or an https URL (Range request), 1 MB at a time."""
    if src.startswith('https://'):
        req = urllib.request.Request(src, headers={'Range': f'bytes={start}-{start + length - 1}'})
        f = urllib.request.urlopen(req, timeout=60)
        if f.status != 206:
            die(f'{src}: server ignored the Range request (HTTP {f.status})')
    else:
        f = open(src, 'rb')
        f.seek(start)
    with f:
        left = length
        while left:
            b = f.read(min(1 << 20, left))
            if not b:
                die(f'{src}: truncated')
            left -= len(b)
            yield b


class Stream:
    """Exact-size reads from an iterator of byte chunks (gunzipped with inflate); `pos` counts the bytes handed
    out and, with hashing, `h` hashes them."""
    def __init__(self, it, inflate=False, hashing=False):
        self.it, self.buf, self.pos = it, bytearray(), 0
        self.z, self.h = zlib.decompressobj(31) if inflate else None, hashlib.sha256() if hashing else None

    def read(self, n):
        while len(self.buf) < n:
            c = next(self.it, None)
            if c is None:
                die('pkg ended early')
            self.buf += self.z.decompress(c) if self.z else c
        out = bytes(self.buf[:n])
        del self.buf[:n]
        if self.h:
            self.h.update(out)
        self.pos += n
        return out

    def take(self, n):
        """The next n bytes as an iterator of chunks."""
        while n:
            b = self.read(min(1 << 20, n))
            n -= len(b)
            yield b


def extract(src, size, prefixes, dest, stop_after=None, sha=None):
    """Read the .pkg `src` (local path or https URL, `size` bytes) front to back, once, and write the regular files of
    its Payload (gzip'd cpio odc) under any of `prefixes` to dest/<pkg path>. stop_after: stop at the first entry
    outside that directory once inside it. sha: read to the end and require the whole pkg's sha256.
    Returns the extracted pkg paths."""
    if not src.startswith('https://') and os.path.getsize(src) != size:
        die(f'{src}: {os.path.getsize(src)} bytes, expected {size}')
    s = Stream(chunks(src, 0, size), hashing=bool(sha))
    magic, hsize, _, toc_len, _, _ = struct.unpack('>4sHHQQI', s.read(28))
    if magic != b'xar!':
        die(f'{src}: not a .pkg')
    s.read(hsize - 28)
    toc = ET.fromstring(zlib.decompress(s.read(toc_len)))
    d = next((f.find('data') for f in toc.iter('file') if f.findtext('name') == 'Payload'), None)
    if d is None:
        die(f'{src}: no Payload')
    off, length = hsize + toc_len + int(d.findtext('offset')), int(d.findtext('length'))
    if off < s.pos:
        die(f'{src}: Payload overlaps the table of contents')
    for _ in s.take(off - s.pos):
        pass
    p, got, inside = Stream(s.take(length), inflate=True), [], False
    while True:
        h = p.read(76)
        if h[:6] != b'070707':
            die(f'{src}: Payload is not a cpio odc archive')
        mode, namesize, fsize = int(h[18:24], 8), int(h[59:65], 8), int(h[65:76], 8)
        name = p.read(namesize)[:-1].decode()
        if name == 'TRAILER!!!':
            break
        path = name[2:] if name.startswith('./') else name
        if stop_after:
            if path.startswith(stop_after):
                inside = True
            elif inside:
                break
        if (mode & 0o170000 == 0o100000 and path.startswith(prefixes) and not path.endswith('.pdb')
                and '..' not in path.split('/')):
            out = os.path.join(dest, path)
            os.makedirs(os.path.dirname(out), exist_ok=True)
            with open(out, 'wb') as f:
                for b in p.take(fsize):
                    f.write(b)
            os.chmod(out, 0o755 if mode & 0o111 else 0o644)
            got.append(path)
        else:
            for _ in p.take(fsize):
                pass
    if sha:
        for _ in s.take(size - s.pos):
            pass
        if s.h.hexdigest() != sha:
            die(f'{src}: sha256 {s.h.hexdigest()} is not the pinned {sha}')
    return got


def tree_sha256(root, prefixes):
    """One hash for a set of files: sha256 of the sorted 'sha256  path' lines of every file under root/<prefix>."""
    lines = []
    for p in prefixes:
        top = os.path.join(root, p)
        files = [top] if os.path.isfile(top) else [os.path.join(dp, f) for dp, _, fn in os.walk(top) for f in fn]
        lines += [f'{C.sha256(f)}  {os.path.relpath(f, root)}\n' for f in files]
    return hashlib.sha256(''.join(sorted(lines)).encode()).hexdigest()


def pkg_files(dest, url, size, want, tree, **kw):
    """dest holds the `want` files of the pkg at url (streamed once into dest; delete dest to fetch again)."""
    if not os.path.exists(dest):
        print(f'streaming {url} ({size / 1e9:.1f} GB, keeping {", ".join(w.rstrip("/").split("/")[-1] for w in want)})')
        part = dest + '.part'
        shutil.rmtree(part, ignore_errors=True)
        n = len(extract(url, size, want, part, **kw))
        os.rename(part, dest)
        print(f'kept {n} files in {dest}')
    got = tree_sha256(dest, want)
    if got != tree:
        die(f'{dest}: not the pinned Unity {UNITY_VERSION} files (tree {got}); delete it to fetch them again')
    return dest


def unity_files(cache):
    """Unity's Mac player pieces (support pkg) and macOS class libraries (editor pkg), from cache/unity-<hash>/."""
    root = os.path.join(cache, f'unity-{UNITY_HASH}')
    os.makedirs(root, exist_ok=True)
    support = pkg_files(os.path.join(root, 'support'), SUPPORT_URL, SUPPORT_SIZE, SUPPORT_WANT, SUPPORT_TREE,
                        sha=SUPPORT_SHA256)
    editor = pkg_files(os.path.join(root, 'editor'), EDITOR_URL, EDITOR_SIZE, (BCL_DIR,), EDITOR_TREE,
                       stop_after=BCL_DIR)
    return support, os.path.join(editor, BCL_DIR)


def exe_icon(exe):
    """The largest image of the first icon group in a Windows .exe's resources: (bytes, is_png)."""
    b = open(exe, 'rb').read()
    pe = struct.unpack_from('<I', b, 0x3c)[0]
    if b[pe:pe + 4] != b'PE\0\0':
        die(f'{exe}: not a PE file')
    nsec, optsz = struct.unpack_from('<H', b, pe + 6)[0], struct.unpack_from('<H', b, pe + 20)[0]
    opt = pe + 24
    rsrc = struct.unpack_from('<I', b, opt + (112 if struct.unpack_from('<H', b, opt)[0] == 0x20b else 96) + 16)[0]
    def off(rva):
        for i in range(nsec):
            vsz, va, rawsz, raw = struct.unpack_from('<IIII', b, opt + optsz + 40 * i + 8)
            if va <= rva < va + max(vsz, rawsz):
                return rva - va + raw
        die(f'{exe}: bad resource address')
    base = off(rsrc)
    def entries(d):  # resource directory at base+d: [(id, offset, is_dir)]
        named, ids = struct.unpack_from('<HH', b, base + d + 12)
        return [(n, p & 0x7fffffff, bool(p & 0x80000000))
                for n, p in (struct.unpack_from('<II', b, base + d + 16 + 8 * i) for i in range(named + ids))]
    def leaf(d):  # first language's data under a directory
        _, o, is_dir = entries(d)[0]
        while is_dir:
            _, o, is_dir = entries(o)[0]
        rva, size = struct.unpack_from('<II', b, base + o)
        return b[off(rva):off(rva) + size]
    types = {i: o for i, o, _ in entries(0)}
    if 3 not in types or 14 not in types:
        die(f'{exe}: no icon resources')
    group = leaf(entries(types[14])[0][1])  # GRPICONDIR: 6-byte header, 14-byte entries (w, h, ..., bpp, size, id)
    best = max((struct.unpack_from('<BBBBHHIH', group, 6 + 14 * k) for k in range(struct.unpack_from('<H', group, 4)[0])),
               key=lambda e: (e[0] or 256, e[5]))
    img = leaf({i: o for i, o, _ in entries(types[3])}[best[7]])
    if img[:8] == b'\x89PNG\r\n\x1a\n':
        return img, True
    # a bare DIB: wrap it as a one-image .ico, which sips can read
    return struct.pack('<HHHBBBBHHII', 0, 1, 1, best[0], best[1], 0, 0, 1, best[5], len(img), 22) + img, False


def write_icns(exe, icns):
    """macOS icon from the game's own .exe icon (all sizes up to the source's, as Finder expects)."""
    with tempfile.TemporaryDirectory() as t:
        img, is_png = exe_icon(exe)
        src = os.path.join(t, 'icon.png' if is_png else 'icon.ico')
        open(src, 'wb').write(img)
        png = os.path.join(t, 'big.png')
        subprocess.run(['sips', '-s', 'format', 'png', src, '--out', png], check=True, capture_output=True)
        size = int(re.search(r'pixelWidth: (\d+)', subprocess.run(['sips', '-g', 'pixelWidth', png], check=True,
                                                                  capture_output=True, text=True).stdout).group(1))
        iconset = os.path.join(t, 'PlayerIcon.iconset')
        os.makedirs(iconset)
        for pt in (16, 32, 128, 256, 512):
            for scale in (1, 2):
                if pt * scale <= size:
                    name = f'icon_{pt}x{pt}{"@2x" if scale == 2 else ""}.png'
                    subprocess.run(['sips', '-z', str(pt * scale), str(pt * scale), png, '--out', os.path.join(iconset, name)],
                                   check=True, capture_output=True)
        subprocess.run(['iconutil', '-c', 'icns', iconset, '-o', icns], check=True)


def build_ours(game, t, modules):
    """Run the converter's build scripts into t/; returns {file name: path} of everything they made."""
    here = os.path.dirname(os.path.abspath(__file__))
    repo = os.path.dirname(here)
    for s in ('build_inputsystem', 'build_steamworks', 'fetch_eos', 'patch_platforms'):
        subprocess.run([sys.executable, '-I', os.path.join(here, s + '.py'), '--game', game, '--out', os.path.join(t, s)],
                       check=True)
    subprocess.run(['sh', os.path.join(repo, 'native/opus/build.sh')], check=True)
    subprocess.run(['sh', os.path.join(repo, 'native/memorywrapper/build.sh')], check=True,
                   env=dict(os.environ, OUT=os.path.join(t, 'memorywrapper')))
    subprocess.run([sys.executable, '-I', os.path.join(repo, 'native/micwatchdog/build_micwatchdog.py'), '--game', game,
                    '--modules', modules, '--out', os.path.join(t, 'micwatchdog')], check=True)
    made = {f: os.path.join(t, s, f) for s in os.listdir(t) for f in os.listdir(os.path.join(t, s))}
    made['opus_egpv.dylib'] = os.path.join(repo, 'native/opus/out/opus_egpv.dylib')
    return made


def register_watchdog(data, pins=None):
    """Add MacMicWatchdog.dll to data/ScriptingAssemblies.json (type 16, like the game's own assemblies) and its Init to
    data/RuntimeInitializeOnLoads.json. Each file must be the pinned game original and must re-serialize to its exact
    bytes (compact JSON plus its original line ending), so the appended entry is the only change; the results are
    pinned too. Fails loudly on anything else."""
    names = lambda o: o['names'] if 'names' in o else [e['assemblyName'] + '.dll' for e in o['root']]
    edits = {'ScriptingAssemblies.json': lambda o: (o['names'].append(WATCHDOG + '.dll'), o['types'].append(16)),
             'RuntimeInitializeOnLoads.json': lambda o: o['root'].append(dict(WATCHDOG_RIOL))}
    for f, (pin_in, pin_out) in (pins or JSON_PINS).items():
        path = os.path.join(data, f)
        raw = open(path, 'rb').read()
        if hashlib.sha256(raw).hexdigest() != pin_in:
            die(f'{path}: not the pinned game file {pin_in} (game update?); re-verify the MacMicWatchdog entries')
        body = raw.rstrip()
        obj = json.loads(body)
        if json.dumps(obj, separators=(',', ':')).encode() != body:
            die(f'{path}: does not re-serialize to its own bytes')
        before = len(names(obj))
        edits[f](obj)
        if len(names(obj)) != before + 1 or names(obj).count(WATCHDOG + '.dll') != 1:
            die(f'{path}: MacMicWatchdog must be added exactly once')
        new = json.dumps(obj, separators=(',', ':')).encode() + raw[len(body):]
        if pin_out and hashlib.sha256(new).hexdigest() != pin_out:
            die(f'{path}: patched sha256 {hashlib.sha256(new).hexdigest()} is not the pinned {pin_out}')
        open(path, 'wb').write(new)


def macho(path):
    with open(path, 'rb') as f:
        return f.read(4) in (b'\xcf\xfa\xed\xfe', b'\xca\xfe\xba\xbe')


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--game', required=True, help='Windows game dir (contains PEAK_Data)')
    ap.add_argument('--data', help='the shader/graphics-patched data.unity3d')
    ap.add_argument('--allow-unpatched-data', action='store_true',
                    help='without --data, use the game\'s D3D-only data.unity3d (dry assembly; renders nothing on Metal)')
    ap.add_argument('--unity-pkg-cache', default=C.CACHE, help='holds unity-<hash>/{support,editor}; may already be filled')
    ap.add_argument('--out', required=True, help='output .app (built as <name>.updating.app, then swapped in)')
    a = ap.parse_args()
    out = os.path.abspath(a.out)
    if not out.endswith('.app'):
        die('--out must end in .app')
    gdata = C.game_data(a.game)
    company, product = open(os.path.join(gdata, 'app.info')).read().splitlines()[:2]
    gdu = os.path.join(gdata, 'data.unity3d')
    if a.data:
        if open(a.data, 'rb').read(8) != b'UnityFS\0':
            die(f'--data {a.data}: not a UnityFS bundle')
        if filecmp.cmp(a.data, gdu, shallow=False):
            die(f'--data {a.data} is the game\'s own (unpatched) data.unity3d')
    elif not a.allow_unpatched_data:
        die('--data <patched data.unity3d> is required (or --allow-unpatched-data for a dry assembly)')
    support, bcl = unity_files(a.unity_pkg_cache)
    player = os.path.join(support, PLAYER)

    with tempfile.TemporaryDirectory(prefix='peak-assemble.') as t:
        ours = build_ours(a.game, t, os.path.join(support, MODULES))
        stage = out[:-4] + '.updating.app'
        shutil.rmtree(stage, ignore_errors=True)
        Ct = os.path.join(stage, 'Contents')
        R = os.path.join(Ct, 'Resources')
        D, P = os.path.join(R, 'Data'), os.path.join(Ct, 'PlugIns')
        M = os.path.join(D, 'Managed')

        # Data: the game's, minus its Windows native plugins (the Mac ones go to Contents/PlugIns)
        C.clone(gdata, D)
        shutil.rmtree(os.path.join(D, 'Plugins'))
        if a.data:
            C.clone(a.data, os.path.join(D, 'data.unity3d'))
        # Unity's Mac player on top: MacOS/UnityPlayer (renamed), Frameworks/*.dylib and, new in 6.3,
        # Resources/Data/Resources/unity default resources, which replaces the game's Windows one
        for dp, _, fn in os.walk(player):
            for f in fn:
                C.clone(os.path.join(dp, f), os.path.join(Ct, os.path.relpath(os.path.join(dp, f), player)))
        os.rename(os.path.join(Ct, 'MacOS/UnityPlayer'), os.path.join(Ct, 'MacOS', product))
        C.clone(os.path.join(support, ENTRY, 'Resources/MainMenu.nib'), os.path.join(R, 'MainMenu.nib'))
        C.clone(os.path.join(support, 'Tools/XCode/PrivacyInfo.xcprivacy'), os.path.join(R, 'PrivacyInfo.xcprivacy'))
        C.clone(os.path.join(support, 'MonoBleedingEdge/etc'), os.path.join(Ct, 'MonoBleedingEdge/etc'))

        # Managed: the game's, with Unity's macOS engine modules and class libraries, and ours
        for f in os.listdir(os.path.join(support, MODULES)):
            C.clone(os.path.join(support, MODULES, f), os.path.join(M, f))
        for f in os.listdir(M):
            src = next((p for p in (os.path.join(bcl, f), os.path.join(bcl, 'Facades', f)) if os.path.isfile(p)), None)
            if src:
                C.clone(src, os.path.join(M, f))
        for f in MANAGED_OURS:
            C.clone(ours[f], os.path.join(M, f))
        for f in PLUGINS:
            C.clone(ours[f], os.path.join(P, f))
        register_watchdog(D)

        # Info.plist: Unity's template with the values Unity's Mac build writes for this game (docs/APP-LAYOUT.md §4)
        info = plistlib.load(open(os.path.join(support, ENTRY, 'Info.plist'), 'rb'))
        info.update({
            'CFBundleExecutable': product,
            'CFBundleGetInfoString': f'Unity Player version {UNITY_VERSION} ({UNITY_HASH}). {COPYRIGHT}',
            'CFBundleIdentifier': re.sub(r'[^A-Za-z0-9.]', '-', f'com.{company}.{product}'),
            'CFBundleName': product,
            'CFBundleShortVersionString': open(os.path.join(a.game, 'version.txt')).readline().strip(),  # = bundleVersion
            'CFBundleVersion': '0',
            'LSApplicationCategoryType': 'public.app-category.games',
            'UnityBuildNumber': UNITY_HASH,
            'NSMicrophoneUsageDescription': MIC_TEXT,  # macOS refuses the microphone without it (Photon Voice)
        })
        plistlib.dump(info, open(os.path.join(Ct, 'Info.plist'), 'wb'))
        plistlib.dump({'Screenmanager Is Fullscreen mode': 'True'}, open(os.path.join(R, 'DefaultPreferences.plist'), 'wb'))
        write_icns(os.path.join(a.game, product + '.exe'), os.path.join(R, info['CFBundleIconFile']))

        # Fail loudly: everything the game lists is there, ours are the pinned files, nothing Windows-only came along
        names = json.load(open(os.path.join(D, 'ScriptingAssemblies.json')))['names']
        riol = {e['assemblyName'] + '.dll' for e in json.load(
            open(os.path.join(D, 'RuntimeInitializeOnLoads.json')))['root']}
        missing = sorted(n for n in set(names) | riol if not os.path.isfile(os.path.join(M, n)))
        if missing:
            die(f'assemblies listed by the game but missing: {missing}')
        for f, pin in list(MANAGED_OURS.items()) + list(PLUGINS.items()):
            path = os.path.join(M if f.endswith('.dll') else P, f)
            if pin and C.sha256(path) != pin:
                die(f'{path}: not the pinned build ({pin})')
        if sorted(os.listdir(P)) != sorted(PLUGINS):
            die(f'Contents/PlugIns: {sorted(os.listdir(P))}, expected {sorted(PLUGINS)}')
        for f in PLUGINS:
            archs = subprocess.run(['lipo', '-archs', os.path.join(P, f)], check=True, capture_output=True,
                                   text=True).stdout.split()
            if 'arm64' not in archs:
                die(f'PlugIns/{f}: no arm64 slice ({archs})')
        bad = [os.path.relpath(os.path.join(dp, f), stage) for dp, _, fn in os.walk(stage) for f in fn
               if f.lower().endswith('.exe') or f == 'steam_appid.txt' or 'D3D12' in dp or 'EmbedRuntime' in dp
               or (f.lower().endswith('.dll') and dp != M)]
        if bad or os.path.exists(os.path.join(D, 'Plugins')):
            die(f'Windows-only files in the app: {bad or "Data/Plugins"}')

        for dp, dn, fn in os.walk(stage):
            for d in dn:
                os.chmod(os.path.join(dp, d), 0o755)
            for f in fn:
                os.chmod(os.path.join(dp, f), 0o755 if macho(os.path.join(dp, f)) else 0o644)
        subprocess.run(['codesign', '--force', '--deep', '-s', '-', stage], check=True)
        subprocess.run(['codesign', '--verify', '--deep', '--strict', stage], check=True)

        if os.path.exists(out):
            old = out[:-4] + '.old.app'
            shutil.rmtree(old, ignore_errors=True)
            os.rename(out, old)
            os.rename(stage, out)
            shutil.rmtree(old)
        else:
            os.rename(stage, out)
    print(f'built {out}' + ('' if a.data else ' (UNPATCHED data.unity3d: dry assembly, will not render on Metal)'))


if __name__ == '__main__':
    main()
