// Parity harness for opus_egpv. Runs the same on macOS (out/opus_egpv.dylib) and Windows (the game's
// opus_egpv.dll), so the two traces can be diffed line for line.
//
//   test gen SPEECH.wav INPUT.f32   build the 48 kHz mono test signal (run once, on the Mac; the file is copied)
//   test run INPUT.f32 TRACE.txt    run every case, write the per-frame trace and print one hash per case
//   test compare A.txt B.txt        per case: identical, or the first differing line
//
// P/Invoke declarations mirror PhotonVoice.API's POpusCodec.Wrapper: ctls through the plain "opus_encoder_ctl"/
// "opus_decoder_ctl" entry points with 3 fixed arguments, state from AllocHGlobal(get_size) + init, and the
// decode-side loss handling of OpusDecoder.DecodePacket (FEC from the next packet, else PLC).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

static class O {
    const string L = "opus_egpv";
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_encoder_get_size(int channels);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_encoder_init(IntPtr st, int fs, int channels, int application);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_decoder_get_size(int channels);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_decoder_init(IntPtr st, int fs, int channels);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_encode(IntPtr st, short[] pcm, int frame_size, byte[] data, int max_data_bytes);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_encode_float(IntPtr st, float[] pcm, int frame_size, byte[] data, int max_data_bytes);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_decode(IntPtr st, byte[] data, int len, short[] pcm, int frame_size, int decode_fec);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_decode_float(IntPtr st, byte[] data, int len, float[] pcm, int frame_size, int decode_fec);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encoder_ctl")] public static extern int enc_set(IntPtr st, int request, int value);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encoder_ctl")] public static extern int enc_get(IntPtr st, int request, ref int value);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decoder_ctl")] public static extern int dec_set(IntPtr st, int request, int value);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decoder_ctl")] public static extern int dec_get(IntPtr st, int request, ref int value);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_encoder_ctl_set(IntPtr st, int request, int value);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_encoder_ctl_get(IntPtr st, int request, ref int value);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_decoder_ctl_set(IntPtr st, int request, int value);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_decoder_ctl_get(IntPtr st, int request, ref int value);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_packet_get_bandwidth(byte[] data);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_packet_get_nb_channels(byte[] data);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr opus_strerror(int error);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr opus_get_version_string();
}

record Case(string Name, int Rate = 24000, int Ch = 1, int App = 2048, int FrameUs = 20000, int Bitrate = 30000,
            int Complexity = -1, int Fec = 1, int LossPct = 30, int Dtx = -1, int Vbr = -1, int VbrConstraint = -1,
            int Signal = -1, int MaxBw = -1, bool Int16 = false, int DropPct = 10, int DecRate = 0, bool HelperCtl = false);

static class Program {
    const int InRate = 48000;
    // ctl requests (opus_defines.h)
    const int SET_APPLICATION = 4000, GET_APPLICATION = 4001, SET_BITRATE = 4002, GET_BITRATE = 4003, SET_MAX_BANDWIDTH = 4004,
        GET_MAX_BANDWIDTH = 4005, SET_VBR = 4006, GET_VBR = 4007, GET_BANDWIDTH = 4009, SET_COMPLEXITY = 4010, GET_COMPLEXITY = 4011,
        SET_INBAND_FEC = 4012, GET_INBAND_FEC = 4013, SET_PACKET_LOSS_PERC = 4014, GET_PACKET_LOSS_PERC = 4015, SET_DTX = 4016,
        GET_DTX = 4017, SET_VBR_CONSTRAINT = 4020, GET_VBR_CONSTRAINT = 4021, SET_SIGNAL = 4024, GET_SIGNAL = 4025,
        GET_LOOKAHEAD = 4027, GET_SAMPLE_RATE = 4029, GET_FINAL_RANGE = 4031, GET_PITCH = 4033, GET_GAIN = 4045,
        GET_LAST_PACKET_DURATION = 4039;

    static string Os => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "macos";
    static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA256.HashData(b), 0, 8).ToLowerInvariant();

    static float[] ReadWavF32(string path) {
        var b = File.ReadAllBytes(path);
        for (int i = 12; i + 8 <= b.Length; ) {
            int len = BitConverter.ToInt32(b, i + 4);
            if (b[i] == 'd' && b[i + 1] == 'a' && b[i + 2] == 't' && b[i + 3] == 'a') {
                var f = new float[len / 4]; Buffer.BlockCopy(b, i + 8, f, 0, f.Length * 4); return f;
            }
            i += 8 + len + (len & 1);
        }
        throw new InvalidDataException("no data chunk (expected 32-bit float WAV)");
    }

    // 48 kHz mono: silence, speech, quiet speech, speech over noise, loud (clipping) speech, noise, digital
    // silence, a log sweep. Made once on the Mac and copied, so platform maths (Math.Sin) can't differ.
    static void Gen(string wav, string output) {
        var speech = ReadWavF32(wav);
        uint s = 12345; float Noise() { s = s * 1664525 + 1013904223; return ((int)(s >> 8) - (1 << 23)) / (float)(1 << 23) * 0.05f; }
        var sweep = new float[4 * InRate];
        for (int i = 0; i < sweep.Length; i++) { double t = i / (double)InRate; sweep[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 50 * (Math.Pow(400, t / 4) - 1) / Math.Log(400) * 4)); }
        var parts = new[] {
            new float[InRate / 2], speech, speech.Select(x => x * 0.05f).ToArray(), speech.Select(x => x + Noise()).ToArray(),
            speech.Select(x => x * 3f).ToArray(), Enumerable.Range(0, 2 * InRate).Select(_ => Noise()).ToArray(), new float[InRate], sweep,
        };
        var all = parts.SelectMany(p => p).ToArray();
        var bytes = new byte[all.Length * 4]; Buffer.BlockCopy(all, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(output, bytes);
        Console.WriteLine($"wrote {output}: {all.Length / (double)InRate:F1} s at {InRate} Hz, sha256 {Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}");
    }

    static List<Case> Cases() {
        var peak = new Case("peak");  // PEAK's Recorder: 24 kHz mono VOIP, 20 ms, 30 kbps; OpusEncoder ctor: FEC on, 30 % loss hint
        var c = new List<Case> { peak, peak with { Name = "peak-helperctl", HelperCtl = true }, peak with { Name = "peak-nodrop", DropPct = 0 },
            peak with { Name = "peak-drop30", DropPct = 30 }, peak with { Name = "peak-dec48k", DecRate = 48000 }, peak with { Name = "peak-int16", Int16 = true } };
        foreach (var us in new[] { 2500, 5000, 10000, 40000, 60000 }) c.Add(peak with { Name = $"frame{us / 1000.0}ms", FrameUs = us });
        foreach (var br in new[] { 6000, 8000, 10000, 12000, 16000, 20000, 24000, 40000, 64000, 96000 }) c.Add(peak with { Name = $"br{br}", Bitrate = br });
        for (int cx = 0; cx <= 10; cx++) c.Add(peak with { Name = $"cx{cx}", Complexity = cx });
        c.Add(peak with { Name = "fec0", Fec = 0 });
        c.Add(peak with { Name = "dtx1", Dtx = 1 });
        c.Add(peak with { Name = "dtx1-fec0", Dtx = 1, Fec = 0 });
        foreach (var lp in new[] { 0, 10, 50 }) c.Add(peak with { Name = $"loss{lp}", LossPct = lp });
        c.Add(peak with { Name = "cbr", Vbr = 0 });
        c.Add(peak with { Name = "cvbr", VbrConstraint = 1 });
        c.Add(peak with { Name = "app-audio", App = 2049 });
        c.Add(peak with { Name = "app-lowdelay", App = 2051 });
        foreach (var r in new[] { 8000, 12000, 16000, 48000 }) c.Add(peak with { Name = $"rate{r}", Rate = r });
        c.Add(peak with { Name = "stereo", Ch = 2 });
        c.Add(peak with { Name = "stereo48k-audio-64k-cx10", Rate = 48000, Ch = 2, App = 2049, Bitrate = 64000, Complexity = 10 });
        c.Add(peak with { Name = "rate48k-audio-cx10", Rate = 48000, App = 2049, Complexity = 10 });
        c.Add(peak with { Name = "rate48k-60ms-fec0", Rate = 48000, FrameUs = 60000, Fec = 0 });
        c.Add(peak with { Name = "signal-voice", Signal = 3001 });
        c.Add(peak with { Name = "signal-music", Signal = 3002 });
        c.Add(peak with { Name = "maxbw-nb", MaxBw = 1101 });
        c.Add(peak with { Name = "maxbw-wb", MaxBw = 1103 });
        return c;
    }

    static void Run(string inputPath, string tracePath) {
        var raw = File.ReadAllBytes(inputPath);
        var input48 = new float[raw.Length / 4]; Buffer.BlockCopy(raw, 0, input48, 0, raw.Length);
        var w = new StreamWriter(tracePath, false, new UTF8Encoding(false)) { NewLine = "\n" };
        var summary = new StringBuilder();

        // Static behaviour.
        w.WriteLine($"version {Marshal.PtrToStringAnsi(O.opus_get_version_string())}");
        for (int e = 1; e >= -8; e--) w.WriteLine($"strerror {e} {Marshal.PtrToStringAnsi(O.opus_strerror(e))}");
        w.WriteLine($"sizes enc {O.opus_encoder_get_size(1)} {O.opus_encoder_get_size(2)} {O.opus_encoder_get_size(3)} dec {O.opus_decoder_get_size(1)} {O.opus_decoder_get_size(2)} {O.opus_decoder_get_size(0)}");
        {
            var st = Marshal.AllocHGlobal(O.opus_encoder_get_size(2));
            w.WriteLine($"enc_init bad rate {O.opus_encoder_init(st, 44100, 1, 2048)} bad app {O.opus_encoder_init(st, 24000, 1, 1234)} ok {O.opus_encoder_init(st, 24000, 1, 2048)}");
            int v = -77;
            w.WriteLine($"enc ctl unknown {O.enc_set(st, 4999, 1)} bad bitrate {O.enc_set(st, SET_BITRATE, -5)} bad cx {O.enc_set(st, SET_COMPLEXITY, 11)} get {O.enc_get(st, GET_BITRATE, ref v)} {v}");
            w.WriteLine($"enc bad frame {O.opus_encode_float(st, new float[1000], 100, new byte[4000], 4000)} tiny buf {O.opus_encode_float(st, new float[480], 480, new byte[1], 1)}");
            Marshal.FreeHGlobal(st);
            var ds = Marshal.AllocHGlobal(O.opus_decoder_get_size(2));
            w.WriteLine($"dec_init bad rate {O.opus_decoder_init(ds, 44100, 1)} ok {O.opus_decoder_init(ds, 24000, 1)}");
            var pcm = new float[5760];
            w.WriteLine($"dec corrupt {O.opus_decode_float(ds, new byte[] { 0xff, 0xff, 0xff, 0xff }, 4, pcm, 480, 0)} short frame {O.opus_decode_float(ds, new byte[] { 0x08 }, 1, pcm, 10, 0)} unknown ctl {O.dec_set(ds, 4999, 0)}");
            Marshal.FreeHGlobal(ds);
            foreach (var toc in new byte[] { 0x00, 0x08, 0x48, 0x68, 0x78, 0x80, 0xa0, 0xc4, 0xe0, 0xfc })
                w.WriteLine($"packet toc {toc:x2} bw {O.opus_packet_get_bandwidth(new[] { toc, (byte)0 })} ch {O.opus_packet_get_nb_channels(new[] { toc, (byte)0 })}");
        }

        foreach (var c in Cases()) {
            var sb = new StringBuilder();
            void L(string line) { sb.Append(c.Name).Append(' ').Append(line).Append('\n'); }
            int decRate = c.DecRate == 0 ? c.Rate : c.DecRate;
            int frame = (int)((long)c.Rate * c.FrameUs / 1000000), decFrame = (int)((long)decRate * c.FrameUs / 1000000);
            // Input at the case's rate: every n-th sample (deterministic; aliasing doesn't matter here). Stereo: R = 0.8 x delayed 7.
            int step = InRate / c.Rate, n = input48.Length / step / frame * frame;
            var mono = new float[n]; for (int i = 0; i < n; i++) mono[i] = input48[i * step];
            var pcmIn = new float[n * c.Ch];
            for (int i = 0; i < n; i++) { pcmIn[i * c.Ch] = mono[i]; if (c.Ch == 2) pcmIn[i * 2 + 1] = i >= 7 ? mono[i - 7] * 0.8f : 0; }

            var enc = Marshal.AllocHGlobal(O.opus_encoder_get_size(c.Ch));
            var dec = Marshal.AllocHGlobal(O.opus_decoder_get_size(c.Ch));
            L($"init {O.opus_encoder_init(enc, c.Rate, c.Ch, c.App)} {O.opus_decoder_init(dec, decRate, c.Ch)}");
            Func<IntPtr, int, int, int> set = c.HelperCtl ? O.opus_encoder_ctl_set : O.enc_set;
            int Get(int req) { int v = -77; int r = c.HelperCtl ? O.opus_encoder_ctl_get(enc, req, ref v) : O.enc_get(enc, req, ref v); return r == 0 ? v : 1000000 + r; }
            int DGet(int req) { int v = -77; int r = c.HelperCtl ? O.opus_decoder_ctl_get(dec, req, ref v) : O.dec_get(dec, req, ref v); return r == 0 ? v : 1000000 + r; }
            // Same order as POpusCodec.OpusEncoder's ctor, then the sweep's extras.
            var sets = new List<int> { set(enc, SET_BITRATE, c.Bitrate), set(enc, SET_INBAND_FEC, c.Fec), set(enc, SET_PACKET_LOSS_PERC, c.LossPct) };
            if (c.Complexity >= 0) sets.Add(set(enc, SET_COMPLEXITY, c.Complexity));
            if (c.Dtx >= 0) sets.Add(set(enc, SET_DTX, c.Dtx));
            if (c.Vbr >= 0) sets.Add(set(enc, SET_VBR, c.Vbr));
            if (c.VbrConstraint >= 0) sets.Add(set(enc, SET_VBR_CONSTRAINT, c.VbrConstraint));
            if (c.Signal >= 0) sets.Add(set(enc, SET_SIGNAL, c.Signal));
            if (c.MaxBw >= 0) sets.Add(set(enc, SET_MAX_BANDWIDTH, c.MaxBw));
            L($"sets {string.Join(",", sets)}");
            L($"gets app {Get(GET_APPLICATION)} br {Get(GET_BITRATE)} maxbw {Get(GET_MAX_BANDWIDTH)} vbr {Get(GET_VBR)} cx {Get(GET_COMPLEXITY)} fec {Get(GET_INBAND_FEC)} " +
              $"loss {Get(GET_PACKET_LOSS_PERC)} dtx {Get(GET_DTX)} cvbr {Get(GET_VBR_CONSTRAINT)} sig {Get(GET_SIGNAL)} look {Get(GET_LOOKAHEAD)} fs {Get(GET_SAMPLE_RATE)} dfs {DGet(GET_SAMPLE_RATE)}");

            var buf = new byte[4000]; var fin = new float[frame * c.Ch]; var sin = new short[frame * c.Ch];
            var fout = new float[decFrame * c.Ch]; var sout = new short[decFrame * c.Ch];
            uint lcg = 777; bool prevLost = false; int frames = n / frame;
            string Decode(byte[] pkt, int fec) {
                int r = c.Int16 ? O.opus_decode(dec, pkt, pkt?.Length ?? 0, sout, decFrame, fec) : O.opus_decode_float(dec, pkt, pkt?.Length ?? 0, fout, decFrame, fec);
                var bytes = new byte[Math.Max(r, 0) * c.Ch * (c.Int16 ? 2 : 4)];
                if (r > 0) Buffer.BlockCopy(c.Int16 ? sout : fout, 0, bytes, 0, bytes.Length);
                return $"{(fec == 1 ? "F" : pkt == null ? "P" : "D")} {r} {(uint)DGet(GET_FINAL_RANGE):x8} {DGet(GET_LAST_PACKET_DURATION)} {DGet(GET_PITCH)} {Hex(bytes)}";
            }
            for (int f = 0; f < frames; f++) {
                int len;
                if (c.Int16) {
                    for (int i = 0; i < sin.Length; i++) sin[i] = (short)Math.Clamp((int)MathF.Round(pcmIn[f * frame * c.Ch + i] * 32767f), -32768, 32767);
                    len = O.opus_encode(enc, sin, frame, buf, buf.Length);
                } else {
                    Array.Copy(pcmIn, f * frame * c.Ch, fin, 0, fin.Length);
                    len = O.opus_encode_float(enc, fin, frame, buf, buf.Length);
                }
                var pkt = len > 0 ? buf.AsSpan(0, len).ToArray() : null;
                var line = new StringBuilder($"{f} e {len} {(uint)Get(GET_FINAL_RANGE):x8} {Get(GET_BANDWIDTH)} {(pkt != null ? Convert.ToHexString(pkt).ToLowerInvariant() : "-")}");
                if (pkt != null) line.Append($" pb {O.opus_packet_get_bandwidth(pkt)} pc {O.opus_packet_get_nb_channels(pkt)}");
                // Network: drop DropPct % (deterministic LCG) plus a 3-frame burst every 97 frames. Photon doesn't send <= 1-byte (DTX) packets.
                lcg = lcg * 1103515245 + 12345;
                bool lost = pkt == null || len <= 1 || (int)(lcg >> 16 & 0x7fff) % 100 < c.DropPct || f % 97 is 50 or 51 or 52;
                // POpusCodec.OpusDecoder.DecodePacket
                if (prevLost) line.Append(" | ").Append(lost ? Decode(null, 0) : Decode(pkt, 1));
                if (!lost) line.Append(" | ").Append(Decode(pkt, 0));
                prevLost = lost;
                L(line.ToString());
            }
            Marshal.FreeHGlobal(enc); Marshal.FreeHGlobal(dec);
            var text = sb.ToString();
            w.Write(text);
            var h = Hex(Encoding.UTF8.GetBytes(text));
            summary.Append($"{c.Name,-28} {h}  {frames} frames\n");
        }
        w.Close();
        Console.Write(summary);
        Console.WriteLine($"{Cases().Count} cases, trace {tracePath} ({Os} {RuntimeInformation.ProcessArchitecture}, {Marshal.PtrToStringAnsi(O.opus_get_version_string())})");
    }

    static int Compare(string a, string b) {
        string Key(string l) => l[..l.IndexOf(' ')];
        var x = File.ReadAllLines(a).ToLookup(Key); var y = File.ReadAllLines(b).ToLookup(Key);
        var names = new[] { "version", "strerror", "sizes", "enc_init", "enc", "dec_init", "dec", "packet" }.Concat(Cases().Select(c => c.Name)).ToList();
        int same = 0, total = 0;
        foreach (var name in names) {
            var xa = x[name].ToArray(); var ya = y[name].ToArray();
            total++;
            int i = 0; while (i < Math.Min(xa.Length, ya.Length) && xa[i] == ya[i]) i++;
            if (i == xa.Length && i == ya.Length) { same++; continue; }
            Console.WriteLine($"DIFF {name}: line {i} of {xa.Length}/{ya.Length}");
            if (i < xa.Length) Console.WriteLine($"  A: {Trim(xa[i])}");
            if (i < ya.Length) Console.WriteLine($"  B: {Trim(ya[i])}");
            int d = xa.Zip(ya).Count(t => t.First != t.Second);
            Console.WriteLine($"  {d} differing lines");
        }
        Console.WriteLine($"{same}/{total} sections identical ({Cases().Count} codec cases + 8 static checks)");
        return same == total ? 0 : 1;
    }
    static string Trim(string s) => s.Length > 300 ? s[..300] + "..." : s;

    static int Main(string[] args) {
        if (args.Length == 3 && args[0] == "gen") { Gen(args[1], args[2]); return 0; }
        if (args.Length == 3 && args[0] == "run") { Run(args[1], args[2]); return 0; }
        if (args.Length == 3 && args[0] == "compare") return Compare(args[1], args[2]);
        Console.Error.WriteLine("usage: test gen SPEECH.wav INPUT.f32 | run INPUT.f32 TRACE.txt | compare A.txt B.txt");
        return 2;
    }
}
