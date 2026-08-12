using System.Runtime.InteropServices;
using SkiaSharp;

namespace CupriFace.Media.Decoding;

/// <summary>
/// VP9/VP8 decode via libvpx (inside <c>cupricodecs</c>): packets in, I420 planes out,
/// converted to RGBA <see cref="SKImage"/>s (BT.601 studio swing — what WebM recorders emit).
/// Player-thread only, like every <see cref="IVideoFrameDecoder"/>.
/// </summary>
internal sealed unsafe class VpxVideoDecoder : IVideoFrameDecoder
{
    // vpx_decoder.h: VPX_DECODER_ABI_VERSION = 3 + VPX_CODEC_ABI_VERSION(4 + VPX_IMAGE_ABI_VERSION(5)).
    private const int DecoderAbiVersion = 12;
    private const int VPX_IMG_FMT_I420 = 0x102;
    private const int VPX_CODEC_OK = 0;

    private nint _ctx;      // vpx_codec_ctx_t, opaque: oversized + zeroed, only ever passed back in
    private byte[] _rgba = [];

    public VpxVideoDecoder(bool vp9)
    {
        _ctx = Marshal.AllocHGlobal(256);
        new Span<byte>((void*)_ctx, 256).Clear();
        var cfg = new Vpx.VpxDecCfg { Threads = (uint)Math.Clamp(Environment.ProcessorCount, 1, 8) };
        var err = Vpx.vpx_codec_dec_init_ver(_ctx, vp9 ? Vpx.vpx_codec_vp9_dx() : Vpx.vpx_codec_vp8_dx(), &cfg, 0, DecoderAbiVersion);
        if (err != VPX_CODEC_OK)
        {
            Marshal.FreeHGlobal(_ctx);
            _ctx = 0;
            throw new InvalidOperationException($"libvpx decoder init failed (vpx_codec_err_t {err}).");
        }
    }

    public SKImage? Decode(ReadOnlySpan<byte> packet, bool keyframe)
    {
        if (_ctx == 0) return null;
        fixed (byte* data = packet)
        {
            if (Vpx.vpx_codec_decode(_ctx, data, (uint)packet.Length, 0, 0) != VPX_CODEC_OK)
                return null;   // a corrupt packet must not kill playback; the next keyframe recovers
        }

        // Take the LAST image this packet produced (normally exactly one).
        nint iter = 0, image = 0;
        for (nint next; (next = Vpx.vpx_codec_get_frame(_ctx, ref iter)) != 0;) image = next;
        if (image == 0) return null;   // no displayable frame (alt-ref)

        var img = Marshal.PtrToStructure<VpxImagePrefix>(image);
        if (img.Fmt != VPX_IMG_FMT_I420 || img.DW == 0 || img.DH == 0) return null;
        return ToImage(in img);
    }

    private SKImage ToImage(in VpxImagePrefix img)
    {
        int w = (int)img.DW, h = (int)img.DH;
        if (_rgba.Length < w * h * 4) _rgba = new byte[w * h * 4];

        var y = (byte*)img.Plane0;
        var u = (byte*)img.Plane1;
        var v = (byte*)img.Plane2;
        fixed (byte* dst0 = _rgba)
        {
            for (var row = 0; row < h; row++)
            {
                var yRow = y + row * img.Stride0;
                var uRow = u + (row >> 1) * img.Stride1;
                var vRow = v + (row >> 1) * img.Stride2;
                var dst = dst0 + row * w * 4;
                for (var col = 0; col < w; col++)
                {
                    // BT.601 studio swing (16..235 luma), the WebM default.
                    var c = 298 * (yRow[col] - 16);
                    var d = uRow[col >> 1] - 128;
                    var e = vRow[col >> 1] - 128;
                    var r = (c + 409 * e + 128) >> 8;
                    var g = (c - 100 * d - 208 * e + 128) >> 8;
                    var b = (c + 516 * d + 128) >> 8;
                    dst[0] = (byte)Math.Clamp(r, 0, 255);
                    dst[1] = (byte)Math.Clamp(g, 0, 255);
                    dst[2] = (byte)Math.Clamp(b, 0, 255);
                    dst[3] = 255;
                    dst += 4;
                }
            }
            return SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque), (nint)dst0, w * 4);
        }
    }

    public void Dispose()
    {
        if (_ctx == 0) return;
        Vpx.vpx_codec_destroy(_ctx);
        Marshal.FreeHGlobal(_ctx);
        _ctx = 0;
    }

    /// <summary>The leading fields of <c>vpx_image_t</c> (vpx_image.h) — read-only view; the
    /// remainder of the struct is never touched. Sequential layout matches the C compiler's
    /// (all-int prefix, 8-aligned pointer block).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VpxImagePrefix
    {
        public int Fmt, ColorSpace, Range;
        public uint W, H, BitDepth;
        public uint DW, DH;            // display size — what we render
        public uint RW, RH;
        public uint XChromaShift, YChromaShift;
        public nint Plane0, Plane1, Plane2, Plane3;
        public int Stride0, Stride1, Stride2, Stride3;
        public int Bps;
    }
}
