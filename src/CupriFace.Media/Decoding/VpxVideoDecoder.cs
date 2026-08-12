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

    // Frees a frame's pixel buffer when its SKImage is disposed (the player's retire ring).
    private static readonly SKImageRasterReleaseDelegate FreePixels = (address, _) => NativeMemory.Free((void*)address);

    private SKImage? ToImage(in VpxImagePrefix img)
    {
        int w = (int)img.DW, h = (int)img.DH;
        // Convert straight into a buffer the SKImage will OWN (freed by its release proc): the old
        // FromPixelCopy path copied every frame a second time — 8 MB per 1080p frame, ~500 MB/s of
        // pure memcpy + GC churn at 60 fps.
        var pixels = (byte*)NativeMemory.Alloc((nuint)(w * h * 4));
        Yuv.I420ToRgba((byte*)img.Plane0, (byte*)img.Plane1, (byte*)img.Plane2,
            img.Stride0, img.Stride1, img.Stride2, pixels, w, h);

        using var pixmap = new SKPixmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque), (nint)pixels, w * 4);
        var image = SKImage.FromPixels(pixmap, FreePixels);
        if (image is null) NativeMemory.Free(pixels);   // refused (bad info) — don't leak
        return image;
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
