#:package ICSharpCode.Decompiler@11.1.0.9782
// ildump.cs: ildasm-style listing of a whole assembly (manifest, references, resources, every type and IL body).
// Dev-side proof tool for il_diff.py (not used at convert time):  dotnet run scripts/ildump.cs -- <in.dll> <out.il>
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;

var pe = new PEFile(args[0]);
using var w = new StreamWriter(args[1]);
var d = new ReflectionDisassembler(new PlainTextOutput(w), CancellationToken.None);
d.WriteAssemblyReferences(pe.Metadata);
d.WriteAssemblyHeader(pe);
d.WriteModuleHeader(pe);
d.WriteModuleContents(pe);
