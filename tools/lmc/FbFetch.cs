using System.Text;
using System.Text.RegularExpressions;

namespace Lmc;

// Render-graph input attachments (FRAMEBUFFER_INPUT, e.g. Core RP's Hidden/CoreSRP/CoreCopy that RenderGraph.AddCopyPass
// and URP's full-screen passes use). D3D has no framebuffer fetch, so its programs read the attachment as the texture
// _UnityFBInput<N>. With native render passes on Metal the player hands the attachment over through framebuffer fetch
// and binds no texture, so that read returns black. Unity's own Metal compile (6000.1 and 6000.3, CoreCopy) declares
// both and lets the player pick with function constants:
//   constant uint32_t rp_input_remap_mask [[ function_constant(0) ]];   4 bits per input, like rp_output_remap_mask
//   constant bool has_framebuffer_fetch [[ function_constant(2) ]];
//   float4 hlslcc_fbinput_N [[ color(rp_input_remap_N), function_constant(has_framebuffer_fetch) ]]
//   texture2d<...> _UnityFBInputN [[ texture(k), function_constant(has_no_framebuffer_fetch) ]]
// This rewrites HLSLcc's MSL into that form: each _UnityFBInputN.read(...) becomes
// (has_framebuffer_fetch ? hlslcc_fbinput_N : _UnityFBInputN.read(...)). The parameter entry is unchanged (Unity's
// lists the texture too).
static class FbFetch
{
    static readonly Regex Arg = new(@"(\w+)<(float|half|uint|int|ushort|short)(, access::\w+ )?> _UnityFBInput(\d+) \[\[ texture\((\d+)\) \]\]");

    public static string Apply(string msl)
    {
        var args = Arg.Matches(msl).ToList();
        if (args.Count == 0)
            return msl.Contains("_UnityFBInput") ? throw new Exception("_UnityFBInput used in a form FbFetch does not know") : msl;
        var decl = new StringBuilder("constant uint32_t rp_input_remap_mask [[ function_constant(0) ]];\n");
        foreach (var a in args) decl.Append($"constant const uint rp_input_remap_{a.Groups[4].Value} = (rp_input_remap_mask >> {4 * int.Parse(a.Groups[4].Value)}) & 0xF;\n");
        decl.Append("constant bool has_framebuffer_fetch [[ function_constant(2) ]];\nconstant bool has_no_framebuffer_fetch = !has_framebuffer_fetch;\n");
        var at = msl.IndexOf("\nfragment ", StringComparison.Ordinal);
        if (at < 0) throw new Exception("_UnityFBInput outside a fragment program");
        msl = msl[..(at + 1)] + decl + msl[(at + 1)..];
        foreach (var a in args)
        {
            var n = a.Groups[4].Value;
            msl = msl.Replace(a.Value, $"{a.Groups[2].Value}4 hlslcc_fbinput_{n} [[ color(rp_input_remap_{n}), function_constant(has_framebuffer_fetch) ]] ,\n    "
                + $"{a.Groups[1].Value}<{a.Groups[2].Value}{a.Groups[3].Value}> _UnityFBInput{n} [[ texture({a.Groups[5].Value}), function_constant(has_no_framebuffer_fetch) ]]");
            var call = $"_UnityFBInput{n}.read(";
            var prefix = $"(has_framebuffer_fetch ? hlslcc_fbinput_{n} : ";
            for (var i = msl.IndexOf(call, StringComparison.Ordinal); i >= 0; i = msl.IndexOf(call, i + prefix.Length + call.Length, StringComparison.Ordinal))
            {
                var end = i + call.Length;
                for (var depth = 1; depth > 0; end++) depth += msl[end] == '(' ? 1 : msl[end] == ')' ? -1 : 0;
                msl = msl[..i] + prefix + msl[i..end] + ")" + msl[end..];
            }
            // every other use of the texture would read nothing under framebuffer fetch
            var rest = Regex.Replace(msl, $@"\(has_framebuffer_fetch \? hlslcc_fbinput_{n} : _UnityFBInput{n}\.read\(", "");
            if (Regex.Matches(rest, $@"\b_UnityFBInput{n}\b").Count != 1) throw new Exception($"_UnityFBInput{n} used other than by read()");
        }
        return msl;
    }
}
