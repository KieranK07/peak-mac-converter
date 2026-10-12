#!/usr/bin/env python3
"""il_diff.py: structural IL diff of two .NET assemblies (dev-side proof tool, not used at convert time).

    python3 -I scripts/il_diff.py <a.dll> <b.dll> [--lines N] [--subst OLD=NEW ...]

Dumps both with ildump.cs (`dotnet run`, ICSharpCode.Decompiler 11.1.0.9782 from nuget.org), drops what only
reflects file layout (method RVAs, data-blob addresses, MVID), splits the listing into one chunk per manifest
entry, type header, field, method, property and event, and compares the chunks by name, so a moved type is
not a difference. Prints every chunk that is only in a, only in b, or changed (with a unified diff), then
whether the common chunks are in the same order. --subst rewrites a's listing first (e.g. a renamed DllImport
library), so the rest of a renamed member is still compared. Exit status 0 = identical.
"""
import argparse, difflib, os, re, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
FLAGS = set('public private nested family assembly famandassem famorassem auto sequential explicit ansi unicode '
            'autochar abstract sealed beforefieldinit interface serializable specialname rtspecialname import '
            'windowsruntime'.split())
HEADS = ('.class ', '.method ', '.field ', '.property ', '.event ', '.assembly ', '.module ', '.mresource ')
NOISE = re.compile(r'^\s*(// (Methods|Fields|Properties|Events|Nested Types)|// MVID: .*|} // end of class .*)?\s*$')


def dump(dll, out, subst=()):
    r = subprocess.run(['dotnet', 'run', os.path.join(HERE, 'ildump.cs'), '--', os.path.abspath(dll), out],
                       capture_output=True, text=True)
    if r.returncode:
        sys.exit(f'il_diff: ildump failed on {dll}:\n{r.stdout}{r.stderr}')
    text = open(out, encoding='utf-8').read()
    for old, new in subst:
        text = text.replace(old, new)
    return text.splitlines()


def normalize(line):
    if '// Method begins at RVA' in line:
        return None
    line = re.sub(r' at I_[0-9A-F]{8}\b', ' at I_', line)
    return re.sub(r'\.data (cil |tls )?I_[0-9A-F]{8}', r'.data \1I_', line)


def chunks(lines):
    """[(key, [lines])] in file order. key = enclosing type path + the member's header."""
    out, stack, cur = [], [], None
    for raw in lines:
        if raw.strip().startswith('} // end of class'):
            stack.pop()
            cur = None
            continue
        line = normalize(raw)
        if line is None or NOISE.match(line):
            continue
        s = line.strip()
        if s.startswith(HEADS) and not (cur and cur[0].startswith('.method') and cur[2]):
            if s.startswith('.class '):
                words = s.split()[1:]
                while words and words[0] in FLAGS:
                    words.pop(0)
                stack.append(' '.join(words))
            head = s
            cur = [head, [line], s.startswith('.method ')]  # [header, lines, still reading a .method header]
            out.append(('/'.join(stack) + ' :: ' + head, cur))
            continue
        if cur is None:  # stray line outside any chunk
            cur = ['(top)', [], False]
            out.append(('/'.join(stack) + ' :: (top)', cur))
        if cur[2]:  # a .method header runs until its '{': make the signature part of the key
            if s == '{':
                cur[2] = False
            else:
                old = out[-1][0]
                out[-1] = (old + ' ' + s, cur)
        cur[1].append(line)
    seen, res = {}, []
    for k, c in out:  # number repeated keys only once the full signature is in them
        n = seen[k] = seen.get(k, 0) + 1
        res.append((k if n == 1 else f'{k} #{n}', c[1]))
    return res


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('a')
    ap.add_argument('b')
    ap.add_argument('--lines', type=int, default=60, help='max diff lines shown per changed chunk (0 = all)')
    ap.add_argument('--subst', action='append', default=[], metavar='OLD=NEW', help="text replaced in a's listing")
    a = ap.parse_args()
    with tempfile.TemporaryDirectory() as t:
        subst = [x.split('=', 1) for x in a.subst]
        A = dict(chunks(dump(a.a, os.path.join(t, 'a.il'), subst)))
        B = dict(chunks(dump(a.b, os.path.join(t, 'b.il'))))
    order_a, order_b = [k for k in A if k in B], [k for k in B if k in A]
    only_a, only_b = [k for k in A if k not in B], [k for k in B if k not in A]
    changed = [k for k in A if k in B and A[k] != B[k]]
    for k in only_a:
        print(f'- only in a: {k}')
    for k in only_b:
        print(f'+ only in b: {k}')
    for k in changed:
        print(f'~ changed:   {k}')
        d = list(difflib.unified_diff(A[k], B[k], 'a', 'b', n=2, lineterm=''))[2:]
        for l in d[:a.lines or None]:
            print('    ' + l)
        if a.lines and len(d) > a.lines:
            print(f'    ... {len(d) - a.lines} more lines')
    same_order = order_a == order_b
    print(f'chunks: a {len(A)}, b {len(B)}; only in a {len(only_a)}, only in b {len(only_b)}, changed {len(changed)}; '
          f'common order {"same" if same_order else "DIFFERENT"}')
    sys.exit(0 if not (only_a or only_b or changed) and same_order else 1)


if __name__ == '__main__':
    main()
