using System.Runtime.InteropServices;
using CupriFace.Media.Webm;

namespace CupriFace.Media.Decoding;

/// <summary>
/// The production decoder factory over the <c>cupricodecs</c> native library (libvpx VP8/VP9
/// decode + libopus, one per-RID binary built by codecs.yml, shipped in this package's
/// <c>runtimes/&lt;rid&gt;/native</c>). This is the portable-native class of interop — the same
/// library on every OS, resolved by name like Skia — never an OS API. When the library is
/// absent (<see cref="Available"/> false), hosts simply don't register a backend and the
/// element stays on its poster with disabled controls.
/// </summary>
public sealed class NativeDecoders : IMediaDecoderFactory
{
    /// <summary>True when the native library loads on this machine — probe before wiring:
    /// <c>if (NativeDecoders.Available) doc.UseVideo(new WebmVideoBackend(new NativeDecoders(), …))</c>.</summary>
    public static bool Available { get; } = Probe();

    private static bool Probe()
    {
        try { _ = Vpx.vpx_codec_version(); return true; }
        catch { return false; }
    }

    public IVideoFrameDecoder? CreateVideo(WebmTrack track) => track.CodecId switch
    {
        "V_VP9" => new VpxVideoDecoder(vp9: true),
        "V_VP8" => new VpxVideoDecoder(vp9: false),
        _ => null,
    };

    public IAudioDecoder? CreateAudio(WebmTrack track) => track.CodecId switch
    {
        "A_OPUS" => new OpusAudioDecoder(track),
        _ => null,
    };
}

/// <summary>The flat imports. One library name everywhere; .NET resolves the per-OS file
/// (cupricodecs.dll / libcupricodecs.so / libcupricodecs.dylib) from runtimes/ or beside the app.</summary>
internal static unsafe partial class Vpx
{
    private const string Lib = "cupricodecs";

    // libvpx. vpx_codec_flags_t / the decode deadline are C `long` (4 bytes on Windows, 8 on
    // unix) — always passed as zero here, and x64/arm64 pass them in registers, so a 64-bit
    // slot is safe for both ABIs.
    [DllImport(Lib)] public static extern int vpx_codec_version();
    [DllImport(Lib)] public static extern nint vpx_codec_vp9_dx();
    [DllImport(Lib)] public static extern nint vpx_codec_vp8_dx();
    [DllImport(Lib)] public static extern int vpx_codec_dec_init_ver(nint ctx, nint iface, VpxDecCfg* cfg, long flags, int abiVersion);
    [DllImport(Lib)] public static extern int vpx_codec_decode(nint ctx, byte* data, uint dataSize, nint userPriv, long deadline);
    [DllImport(Lib)] public static extern nint vpx_codec_get_frame(nint ctx, ref nint iter);
    [DllImport(Lib)] public static extern int vpx_codec_destroy(nint ctx);

    // libopus
    [DllImport(Lib)] public static extern nint opus_decoder_create(int sampleRate, int channels, out int error);
    [DllImport(Lib)] public static extern int opus_decode_float(nint decoder, byte* data, int length, float* pcm, int frameSizePerChannel, int decodeFec);
    [DllImport(Lib)] public static extern void opus_decoder_destroy(nint decoder);

    [StructLayout(LayoutKind.Sequential)]
    public struct VpxDecCfg
    {
        public uint Threads, Width, Height;
    }
}
