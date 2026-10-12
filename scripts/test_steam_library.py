#!/usr/bin/env python3
"""Self-check for steam_library.py on a synthetic Steam folder (no real Steam files): python3 -I scripts/test_steam_library.py"""
import hashlib, os, struct, subprocess, sys, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import steam_library as S

HERE = os.path.dirname(os.path.abspath(__file__))
LOCALCONFIG = ('"UserLocalConfigStore"\n{\n\t"Software"\n\t{\n\t\t"Valve"\n\t\t{\n\t\t\t"Steam"\n\t\t\t{\n\t\t\t\t"apps"\n'
               '\t\t\t\t{\n\t\t\t\t\t"3527290"\n\t\t\t\t\t{\n\t\t\t\t\t\t"Playtime"\t\t"168"\n\t\t\t\t\t}\n\t\t\t\t}\n'
               '\t\t\t}\n\t\t}\n\t}\n}\n')


def appinfo(apps):
    """A version-29 appinfo.vdf holding {appid: KeyValues items}."""
    names = sorted({k for items in apps.values() for k in keys(items)})
    body = b''
    for appid, items in apps.items():
        blob = S.encode_kv(items, names)
        body += S.HEADER.pack(appid, S.HEADER.size - 8 + len(blob), 2, 0, 0, b'\0' * 20, 1, hashlib.sha1(blob).digest()) + blob
    body += struct.pack('<I', 0)
    table = struct.pack('<I', len(names)) + b''.join(n.encode() + b'\0' for n in names)
    return struct.pack('<IIq', 0x07564429, 1, 16 + len(body)) + body + table


def keys(items):
    for t, k, v in items:
        yield k
        if t == 0:
            yield from keys(v)


def run(steam, *args):
    return subprocess.run([sys.executable, '-I', os.path.join(HERE, 'steam_library.py'), *args],
                          env=dict(os.environ, PMC_STEAM_DIR=steam), capture_output=True, text=True).returncode


def main():
    peak = [(0, 'appinfo', [(2, 'appid', 3527290), (0, 'common', [(1, 'name', b'PEAK'), (1, 'oslist', b'windows')]),
            (0, 'config', [(1, 'installdir', b'PEAK'), (0, 'launch', [
                (0, '9', [(1, 'executable', b'PEAK.exe'), (0, 'config', [(1, 'oslist', b'linux'), (1, 'BetaKey', b'x')])]),
                (0, '5', [(1, 'executable', b'PEAK.exe'), (0, 'config', [(1, 'oslist', b'windows'), (2, 'osarch', 64)])]),
                (0, '6', [(1, 'executable', b'PEAK.exe'), (0, 'config', [(1, 'oslist', b'windows')])])])]),
            (0, 'depots', [(0, '3527291', [(0, 'config', [(1, 'oslist', b'windows')])])])])]
    other = [(0, 'appinfo', [(2, 'appid', 10), (0, 'common', [(1, 'oslist', b'windows')])])]
    with tempfile.TemporaryDirectory() as t:
        steam, app = os.path.join(t, 'Steam'), os.path.join(t, 'Apps/PEAK.app')
        cfg = os.path.join(steam, 'userdata/42/config')
        os.makedirs(cfg)
        os.makedirs(os.path.join(steam, 'userdata/anonymous/config'))
        os.makedirs(os.path.join(steam, 'appcache/librarycache/3527290/abc'))
        for f in ('abc/library_600x900.jpg', 'abc/library_header.jpg', 'abc/logo.png', 'abc/library_hero.jpg', 'f00d.jpg'):
            open(os.path.join(steam, 'appcache/librarycache/3527290', f), 'w').write(f)
        open(os.path.join(steam, 'appcache/appinfo.vdf'), 'wb').write(appinfo({10: other, 3527290: peak}))
        before = open(os.path.join(steam, 'appcache/appinfo.vdf'), 'rb').read()
        open(os.path.join(cfg, 'localconfig.vdf'), 'w').write(LOCALCONFIG)
        lethal = {'appid': -5, 'AppName': 'Lethal Company', 'Exe': '"/x/Lethal Company"', 'sortas': ''}
        open(os.path.join(cfg, 'shortcuts.vdf'), 'wb').write(S.write_vdf({'shortcuts': {'0': lethal}}))
        copied = os.path.join(t, 'copied')
        os.makedirs(copied)

        # a copied install: a shortcut with PEAK's artwork; Lethal's shortcut is kept
        assert run(steam, '--check-play') == 1
        assert run(steam, '--app', app, '--game', copied, '--check') == 1
        assert run(steam, '--app', app, '--game', copied) == 0
        assert run(steam, '--app', app, '--game', copied, '--check') == 0
        assert run(steam, '--app', app, '--game', copied) == 0  # idempotent
        s = S.read_vdf(open(os.path.join(cfg, 'shortcuts.vdf'), 'rb').read())['shortcuts']
        assert len(s) == 2 and s['0'] == lethal, s
        assert s['1']['Exe'] == f'"{app}/Contents/MacOS/PEAK"' and s['1']['LaunchOptions'] == 'SteamAppId=3527290 %command%'
        uid = s['1']['appid'] & 0xffffffff
        assert sorted(os.listdir(os.path.join(cfg, 'grid'))) == sorted(
            f'{uid}{x}' for x in ('p.jpg', '.jpg', '_hero.jpg', '_logo.png', '_icon.jpg'))
        assert open(os.path.join(cfg, f'grid/{uid}.jpg')).read() == 'abc/library_header.jpg'
        assert open(os.path.join(cfg, f'grid/{uid}_icon.jpg')).read() == 'f00d.jpg'
        assert open(os.path.join(steam, 'appcache/appinfo.vdf'), 'rb').read() == before

        # Steam's own install: the real PEAK entry plays the app, Steam is asked to install it; the shortcut goes
        assert run(steam, '--app', app, '--check') == 1
        assert run(steam, '--app', app) == 0
        assert run(steam, '--app', app, '--check') == 0
        assert run(steam, '--check-play') == 0
        b = open(os.path.join(steam, 'appcache/appinfo.vdf'), 'rb').read()
        n = 16 + 8 + struct.unpack_from('<I', before, 20)[0]  # the other app's entry is untouched
        assert b[16:n] == before[16:n] and b[:4] == before[:4]
        S.APPINFO = os.path.join(steam, 'appcache/appinfo.vdf')
        items = S.load_appinfo()[3]  # re-parses and re-checks the recomputed sha1
        launch = S.find(items, 'appinfo', 'config', 'launch')[1][2]
        assert S.find(items, 'appinfo', 'common', 'oslist')[1][2] == b'windows,macos'
        assert [S.find(e, 'config', 'oslist')[1][2] for _, _, e in launch] == [b'linux', b'windows,macos', b'windows']
        assert S.find(items, 'appinfo', 'depots', '3527291', 'config', 'oslist')[1][2] == b'windows,macos'
        acf = os.path.join(steam, 'steamapps/appmanifest_3527290.acf')
        assert S.parse(open(acf).read()) == [('AppState', [('appid', '3527290'), ('Universe', '1'), ('name', 'PEAK'),
                                                           ('StateFlags', '1026'), ('installdir', 'PEAK')])]
        lc = open(os.path.join(cfg, 'localconfig.vdf')).read()
        assert f'"LaunchOptions"\t\t"\\"{app}/Contents/MacOS/PEAK\\" %command%"' in lc and '"Playtime"\t\t"168"' in lc
        s = S.read_vdf(open(os.path.join(cfg, 'shortcuts.vdf'), 'rb').read())['shortcuts']
        assert list(s.values()) == [lethal] and os.listdir(os.path.join(cfg, 'grid')) == []
        # Steam's own install record is never rewritten
        open(acf, 'w').write('"AppState"\n{\n\t"StateFlags"\t\t"4"\n}\n')
        open(os.path.join(steam, 'appcache/appinfo.vdf'), 'wb').write(before)  # Steam replaced the app info
        assert run(steam, '--check-play') == 1 and run(steam, '--app', app, '--check') == 1
        assert run(steam, '--app', app) == 0 and run(steam, '--app', app, '--check') == 0
        assert open(acf).read() == '"AppState"\n{\n\t"StateFlags"\t\t"4"\n}\n'
        assert not os.path.exists(os.path.join(steam, 'userdata/anonymous/config/shortcuts.vdf'))
    print('steam_library: all checks passed')


if __name__ == '__main__':
    main()
