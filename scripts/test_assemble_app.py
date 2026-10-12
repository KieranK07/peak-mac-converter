"""Self-check for the pkg stream extractor, the tree pin and the MacMicWatchdog JSON registration:
    python3 -I scripts/test_assemble_app.py (macOS, pkgbuild)."""
import hashlib, json, os, subprocess, sys, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import assemble_app as A

with tempfile.TemporaryDirectory() as t:
    root = os.path.join(t, 'root')
    for rel, body in {'a/keep.dll': os.urandom(3 << 20), 'a/sub/deep.dll': b'D', 'a/skip.pdb': b'P',
                      'b/other.dll': b'O', 'c.plist': b'C'}.items():
        os.makedirs(os.path.dirname(os.path.join(root, rel)), exist_ok=True)
        open(os.path.join(root, rel), 'wb').write(body)
    pkg = os.path.join(t, 'x.pkg')
    subprocess.run(['pkgbuild', '--quiet', '--root', root, '--identifier', 'test.x', '--version', '1', pkg], check=True)
    size, sha = os.path.getsize(pkg), hashlib.sha256(open(pkg, 'rb').read()).hexdigest()

    got = A.extract(pkg, size, ('a/',), os.path.join(t, 'o1'), stop_after='a/')  # stops early, no whole-pkg hash
    assert sorted(got) == ['a/keep.dll', 'a/sub/deep.dll'], got
    assert open(os.path.join(t, 'o1/a/keep.dll'), 'rb').read() == open(os.path.join(root, 'a/keep.dll'), 'rb').read()

    got = A.extract(pkg, size, ('a/sub/', 'c.plist'), os.path.join(t, 'o2'), sha=sha)  # whole pkg read and checked
    assert sorted(got) == ['a/sub/deep.dll', 'c.plist'], got
    assert A.tree_sha256(os.path.join(t, 'o2'), ('a/sub/', 'c.plist')) == A.tree_sha256(root, ('a/sub/', 'c.plist'))
    assert A.tree_sha256(os.path.join(t, 'o2'), ('a/sub/',)) != A.tree_sha256(root, ('a/sub/', 'c.plist'))
    for bad in (dict(sha='0' * 64), dict(size=size - 1)):
        try:
            A.extract(pkg, bad.get('size', size), ('a/',), os.path.join(t, 'o3'), sha=bad.get('sha', sha))
        except SystemExit:
            continue
        raise AssertionError(f'{bad} was accepted')

# MacMicWatchdog registration: one entry appended per file, line ending kept, anything unpinned refused
sa, riol = b'{"names":["A.dll"],"types":[16]}', b'{"root":[{"assemblyName":"A","methodName":"I"}]}\r\n'
sha = lambda b: hashlib.sha256(b).hexdigest()
with tempfile.TemporaryDirectory() as t:
    def run(files, pins):
        for f, b in files.items():
            open(os.path.join(t, f), 'wb').write(b)
        A.register_watchdog(t, pins)
        return {f: open(os.path.join(t, f), 'rb').read() for f in files}
    orig = {'ScriptingAssemblies.json': sa, 'RuntimeInitializeOnLoads.json': riol}
    got = run(orig, {f: (sha(b), None) for f, b in orig.items()})
    assert json.loads(got['ScriptingAssemblies.json']) == {'names': ['A.dll', 'MacMicWatchdog.dll'], 'types': [16, 16]}
    assert got['RuntimeInitializeOnLoads.json'].endswith(b'}]}\r\n')
    assert json.loads(got['RuntimeInitializeOnLoads.json'])['root'][1] == A.WATCHDOG_RIOL
    for files, pins in ((orig, {'ScriptingAssemblies.json': ('0' * 64, None)}),           # unpinned game file
                        (got, {f: (sha(b), None) for f, b in got.items()}),               # already registered
                        (orig, {'ScriptingAssemblies.json': (sha(sa), '0' * 64)}),        # result not the pin
                        ({'ScriptingAssemblies.json': sa.replace(b':', b': ')},           # not compact JSON
                         {'ScriptingAssemblies.json': (sha(sa.replace(b':', b': ')), None)})):
        try:
            run(files, pins)
        except SystemExit:
            continue
        raise AssertionError(f'{pins} was accepted')
print('ok')
