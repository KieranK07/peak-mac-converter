"""Shared helpers for the converter scripts: pinned downloads, hashes, game paths. stdlib only."""
import hashlib, os, shutil, subprocess, sys, tarfile, zipfile

CACHE = os.environ.get('PMC_CACHE') or os.path.expanduser('~/Library/Caches/peak-mac-converter')


def die(msg):
    sys.exit(f'{os.path.basename(sys.argv[0])}: {msg}')


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()


def check(path, want, what):
    if not os.path.isfile(path):
        die(f'{what}: {path} not found')
    got = sha256(path)
    if got != want:
        die(f'{what}: sha256 {got} is not the verified {want} (game or upstream update?); re-verify and update the pin')
    return path


def game_data(game):
    """<game>/<Name>_Data of a Windows Unity game."""
    datas = [d for d in os.listdir(game) if d.endswith('_Data') and os.path.isdir(os.path.join(game, d))]
    if len(datas) != 1:
        die(f'--game: expected one *_Data folder in {game}')
    return os.path.join(game, datas[0])


def fetch(url, sha, name, keep):
    """Download url once (sha256-checked) into CACHE/name, keeping only the archive members keep(member) selects.
    .tgz/.tar.gz, .zip/.nupkg and .unitypackage (a tgz) are unpacked; the archive itself is deleted."""
    root = os.path.join(CACHE, name)
    if os.path.exists(os.path.join(root, '.complete')):
        return root
    shutil.rmtree(root, ignore_errors=True)
    os.makedirs(root)
    arc = root + '.download'
    print(f'downloading {url}')
    subprocess.run(['curl', '-fsSL', '--retry', '3', '-o', arc, url], check=True)
    got = sha256(arc)
    if got != sha:
        os.remove(arc)
        die(f'{url}: sha256 {got} is not the pinned {sha}')
    if zipfile.is_zipfile(arc):
        with zipfile.ZipFile(arc) as z:
            members = [(n, lambda n=n: z.read(n)) for n in z.namelist()]
            write_members(root, members, keep)
    else:
        with tarfile.open(arc) as t:
            members = [(m.name, lambda m=m: t.extractfile(m).read()) for m in t if m.isfile()]
            write_members(root, members, keep)
    os.remove(arc)
    open(os.path.join(root, '.complete'), 'w').close()
    return root


def write_members(root, members, keep):
    for name, read in members:
        name = name[2:] if name.startswith('./') else name
        if keep(name) and '..' not in name.split('/') and not name.startswith('/'):
            out = os.path.join(root, name)
            os.makedirs(os.path.dirname(out), exist_ok=True)
            with open(out, 'wb') as f:
                f.write(read())


def clone(src, dst):
    """APFS clone (cp -c falls back to a normal copy across volumes)."""
    os.makedirs(os.path.dirname(os.path.abspath(dst)), exist_ok=True)
    if os.path.lexists(dst):
        shutil.rmtree(dst) if os.path.isdir(dst) and not os.path.islink(dst) else os.remove(dst)
    subprocess.run(['cp', '-c', '-R', src, dst], check=True)


def csc(rsp_args, build_dir, roslyn_dir):
    """Run Roslyn's csc.dll (any .NET 6+ runtime) on an argument list; die with its output on failure."""
    rsp = os.path.join(build_dir, 'csc.rsp')
    with open(rsp, 'w') as f:
        f.write('\n'.join(f'"{x}"' for x in rsp_args) + '\n')
    env = dict(os.environ, DOTNET_ROLL_FORWARD='Major', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    r = subprocess.run(['dotnet', 'exec', os.path.join(roslyn_dir, 'csc.dll'), f'@{rsp}'], env=env,
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if r.returncode:
        die('compile failed:\n' + r.stdout[-4000:])


# Roslyn 4.3.1 (MIT), the compiler Unity 6000.3.15f1 uses: with it, --platform win rebuilds of InputSystem and
# Steamworks.NET are IL-identical to the game's Unity-built DLLs. netstandard.dll 2.1 (MIT) is byte-identical to
# Unity's Editor/Data/NetStandard/ref/2.1.0/netstandard.dll (API compatibility level .NET Standard 2.1).
ROSLYN = ('https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.3.1/'
          'microsoft.net.compilers.toolset.4.3.1.nupkg',
          'ccbb7f75ba7271f5fad020e8b1b4eeffe5c56da5dd3a17797c2943aacf29ef78')
NETSTD = ('https://api.nuget.org/v3-flatcontainer/netstandard.library.ref/2.1.0/netstandard.library.ref.2.1.0.nupkg',
          '46ea2fcbd10a817685b85af7ce0c397d12944bdc81209e272de1e05efd33c78a')
_CSC = 'tasks/net6.0/bincore/'
_CSC_FILES = {'csc.dll', 'csc.deps.json', 'csc.runtimeconfig.json',
              'Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll'}


def roslyn():
    return os.path.join(fetch(*ROSLYN, 'roslyn-4.3.1', lambda n: n.startswith(_CSC) and n[len(_CSC):] in _CSC_FILES),
                        _CSC)


def netstandard():
    return os.path.join(fetch(*NETSTD, 'netstandard-2.1.0', lambda n: n == 'ref/netstandard2.1/netstandard.dll'),
                        'ref/netstandard2.1/netstandard.dll')


# Unity 6000.3.15f1 scripting defines for a non-development Mono standalone player, API level .NET Standard 2.1,
# new Input System. Built from Unity's own Bee .rsp for a 2022.3 macOS player (Lethal port) with the 6000.3 version
# symbols, and that rsp's Windows counterpart for the Windows group. Only the symbols a source tests matter; the
# --platform win rebuilds prove every one the Windows build reads (IL-identical to the shipped DLLs).
UNITY = '6000.3.15f1'
DEFINES_VERSION = ('UNITY_6000_3_15 UNITY_6000_3 UNITY_6000 UNITY_5_3_OR_NEWER UNITY_5_4_OR_NEWER UNITY_5_5_OR_NEWER '
                   'UNITY_5_6_OR_NEWER ' + ' '.join(
                       f'UNITY_{y}_{m}_OR_NEWER' for y in range(2017, 2024) for m in (1, 2, 3, 4)
                       if m < 4 or y < 2020) +
                   ' UNITY_6000_0_OR_NEWER UNITY_6000_1_OR_NEWER UNITY_6000_2_OR_NEWER UNITY_6000_3_OR_NEWER').split()
DEFINES_COMMON = '''
PLATFORM_ARCH_64 UNITY_64 ENABLE_AUDIO ENABLE_CACHING ENABLE_CLOTH ENABLE_MICROPHONE ENABLE_MULTIPLE_DISPLAYS
ENABLE_PHYSICS ENABLE_TEXTURE_STREAMING ENABLE_VIRTUALTEXTURING ENABLE_LZMA ENABLE_UNITYEVENTS ENABLE_VR ENABLE_WEBCAM
ENABLE_UNITYWEBREQUEST ENABLE_WWW ENABLE_CLOUD_SERVICES ENABLE_CLOUD_SERVICES_ADS ENABLE_CLOUD_SERVICES_USE_WEBREQUEST
ENABLE_CLOUD_SERVICES_CRASH_REPORTING ENABLE_CLOUD_SERVICES_NATIVE_CRASH_REPORTING ENABLE_CLOUD_SERVICES_PURCHASING
ENABLE_CLOUD_SERVICES_ANALYTICS ENABLE_CLOUD_SERVICES_BUILD ENABLE_EDITOR_GAME_SERVICES
ENABLE_UNITY_GAME_SERVICES_ANALYTICS_SUPPORT ENABLE_CLOUD_LICENSE ENABLE_EDITOR_HUB_LICENSE ENABLE_WEBSOCKET_CLIENT
ENABLE_GENERATE_NATIVE_PLUGINS_FOR_ASSEMBLIES_API ENABLE_DIRECTOR_AUDIO ENABLE_DIRECTOR_TEXTURE ENABLE_MANAGED_JOBS
ENABLE_MANAGED_TRANSFORM_JOBS ENABLE_MANAGED_ANIMATION_JOBS ENABLE_MANAGED_AUDIO_JOBS ENABLE_MANAGED_UNITYTLS
INCLUDE_DYNAMIC_GI ENABLE_SCRIPTING_GC_WBARRIERS PLATFORM_SUPPORTS_MONO RENDER_SOFTWARE_CURSOR ENABLE_VIDEO
ENABLE_NAVIGATION_PACKAGE_DEBUG_VISUALIZATION ENABLE_NAVIGATION_HEIGHTMESH_RUNTIME_SUPPORT
ENABLE_NAVIGATION_UI_REQUIRES_PACKAGE PLATFORM_STANDALONE TEXTCORE_1_0_OR_NEWER
'''.split()
DEFINES_PLATFORM = {
    'osx': '''PLATFORM_STANDALONE_OSX UNITY_STANDALONE_OSX UNITY_STANDALONE ENABLE_GAMECENTER ENABLE_RUNTIME_GI ENABLE_MOVIES
              ENABLE_NETWORK ENABLE_CRUNCH_TEXTURE_COMPRESSION ENABLE_CLUSTER_SYNC ENABLE_CLUSTERINPUT ENABLE_SPATIALTRACKING
              PLATFORM_UPDATES_TIME_OUTSIDE_OF_PLAYER_LOOP ENABLE_MONO'''.split(),
    'win': '''PLATFORM_STANDALONE_WIN UNITY_STANDALONE_WIN UNITY_STANDALONE ENABLE_RUNTIME_GI ENABLE_MOVIES ENABLE_NETWORK
              ENABLE_NVIDIA ENABLE_AMD ENABLE_CRUNCH_TEXTURE_COMPRESSION ENABLE_OUT_OF_PROCESS_CRASH_HANDLER
              ENABLE_CLUSTER_SYNC ENABLE_CLUSTERINPUT PLATFORM_UPDATES_TIME_OUTSIDE_OF_PLAYER_LOOP
              GFXDEVICE_WAITFOREVENT_MESSAGEPUMP PLATFORM_USES_EXPLICIT_MEMORY_MANAGER_INITIALIZER ENABLE_WEBSOCKET_HOST
              ENABLE_MONO'''.split(),
}
DEFINES_TAIL = '''
NET_STANDARD_2_0 NET_STANDARD NET_STANDARD_2_1 NETSTANDARD NETSTANDARD2_1 ENABLE_CUSTOM_RENDER_TEXTURE ENABLE_DIRECTOR
ENABLE_LOCALIZATION ENABLE_SPRITES ENABLE_TERRAIN ENABLE_TILEMAP ENABLE_TIMELINE ENABLE_INPUT_SYSTEM
TEXTCORE_FONT_ENGINE_1_5_OR_NEWER CSHARP_7_OR_LATER CSHARP_7_3_OR_NEWER
'''.split()


def unity_defines(platform):
    return DEFINES_VERSION + DEFINES_COMMON + DEFINES_PLATFORM[platform] + DEFINES_TAIL


def unity_csc_args(out, build_dir, pathmap, unsafe):
    """Unity's Bee compiler options for a player script assembly (non-development); unsafe = asmdef allowUnsafeCode."""
    return ['-target:library', f'-out:{out}', '-noconfig', '-nostdlib+', '-langversion:9.0'] + ['-unsafe+'] * unsafe + [
            '-deterministic', '-optimize+', '-debug:portable', '-nologo', '-RuntimeMetadataVersion:v4.0.30319',
            '-nowarn:0169,0649,0282,1701,1702', '-warn:0', '-utf8output', '-preferreduilang:en-US',
            f'-pathmap:{pathmap},{build_dir}=Library/Bee/artifacts']
