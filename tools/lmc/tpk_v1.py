"""Convert an AssetRipper type tree package (TPK v2, as shipped in UnityPy) to TPK v1, the version AssetsTools.NET
reads. Unity 6000.3's Shader/ComputeShader layouts are only in the newer packages.

usage: python3 -I tpk_v1.py <in.tpk> <out.tpk> <unity version, e.g. 6000.3.15f1>

v1 and v2 differ only in the common-string table (Unity's built-in type tree strings): v1 stores, per Unity
version, how many strings of one list apply; v2 stores (offset, string) pairs per version. Unity 2023.1 and 6000.x
removed some, which v1 can't express, so the v1 list is the one for the given Unity version (exact for it, and only
used when a file carries its own type trees). The class and node tables are copied unchanged. Output is
uncompressed. Standard library only (LZMA or uncompressed input).
"""
import io
import lzma
import struct
import sys


def lzma_raw(data):
    props, dict_size = struct.unpack_from("<BI", data)
    lc, rem = props % 9, props // 9
    f = {"id": lzma.FILTER_LZMA1, "dict_size": dict_size, "lc": lc, "lp": rem % 5, "pb": rem // 5}
    return lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[f]).decompress(data[5:])


def version(text):
    import re
    m = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)([abcfpx])(\d+)", text)
    a, b, c, t, n = m.groups()
    return int(a) << 48 | int(b) << 32 | int(c) << 16 | "abcfpx".index(t) << 8 | int(n)


def convert(src, target):
    magic, ver, comp, dtype, _, _, csize, usize = struct.unpack_from("<IBBBBIII", src)
    if magic != 0x2A4B5054 or dtype != 0:
        sys.exit("not a type tree TPK")
    body = src[20:20 + csize]
    blob = body if comp == 0 else lzma_raw(body) if comp == 2 else sys.exit(f"compression {comp} not supported")
    if len(blob) != usize:
        sys.exit("bad uncompressed size")
    if ver == 1:
        return struct.pack("<IBBBBIII", magic, 1, 0, 0, 0, 0, len(blob), len(blob)) + blob
    if ver != 2:
        sys.exit(f"TPK version {ver}")
    s = io.BytesIO(blob)
    rd = lambda fmt: struct.unpack(fmt, s.read(struct.calcsize(fmt)))
    rd("<q")
    (nv,) = rd("<i")
    s.read(8 * nv)
    (nc,) = rd("<i")
    for _ in range(nc):
        rd("<i")
        (n,) = rd("<i")
        for _ in range(n):
            s.read(8)
            if s.read(1)[0]:
                _, _, flags = rd("<HHB")
                s.read(2 * bool(flags & 0x40) + 2 * bool(flags & 0x80))
    cs_at = s.tell()
    (n,) = rd("<i")
    per = []
    for _ in range(n):
        (v,) = rd("<Q")
        (k,) = rd("<i")
        e = rd(f"<{2 * k}H")
        per.append((v, sorted(zip(e[::2], e[1::2]))))
    rest = blob[s.tell():]
    applicable = [e for v, e in per if v <= target]
    if not applicable:
        sys.exit("no common strings for that version")
    latest = [i for _, i in applicable[-1]]
    cs = struct.pack("<i", len(per)) + b"".join(struct.pack("<QB", v, min(len(e), len(latest))) for v, e in per)
    cs += struct.pack(f"<i{len(latest)}H", len(latest), *latest)
    out = blob[:cs_at] + cs + rest
    return struct.pack("<IBBBBIII", magic, 1, 0, 0, 0, 0, len(out), len(out)) + out


if __name__ == "__main__":
    data = open(sys.argv[1], "rb").read()
    open(sys.argv[2], "wb").write(convert(data, version(sys.argv[3])))
    print(f"wrote {sys.argv[2]}")
