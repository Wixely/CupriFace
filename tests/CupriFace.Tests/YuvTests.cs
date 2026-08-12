using CupriFace.Media.Decoding;
using Xunit;

namespace CupriFace.Tests;

/// <summary>The SIMD I420→RGBA kernel must be BYTE-EXACT against the scalar reference on every
/// input — including the clamp extremes and odd widths that exercise the scalar tail.</summary>
public unsafe class YuvTests
{
    private static byte[] Scalar(byte[] y, byte[] u, byte[] v, int w, int h, int sy, int sc)
    {
        var outp = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            for (var col = 0; col < w; col++)
            {
                var c = 298 * (y[row * sy + col] - 16);
                var d = u[(row >> 1) * sc + (col >> 1)] - 128;
                var e = v[(row >> 1) * sc + (col >> 1)] - 128;
                var o = (row * w + col) * 4;
                outp[o] = (byte)Math.Clamp((c + 409 * e + 128) >> 8, 0, 255);
                outp[o + 1] = (byte)Math.Clamp((c - 100 * d - 208 * e + 128) >> 8, 0, 255);
                outp[o + 2] = (byte)Math.Clamp((c + 516 * d + 128) >> 8, 0, 255);
                outp[o + 3] = 255;
            }
        return outp;
    }

    [Theory]
    [InlineData(160, 90)]    // the fixture's size (multiple of 8: pure SIMD)
    [InlineData(37, 11)]     // odd width + odd height: SIMD blocks + scalar tail + chroma edges
    [InlineData(8, 2)]       // exactly one SIMD block
    [InlineData(7, 3)]       // below the SIMD width: scalar only
    public void Simd_matches_the_scalar_reference_byte_for_byte(int w, int h)
    {
        int sy = w + 5, sc = (w + 1) / 2 + 3;               // non-tight strides, like libvpx uses
        var rng = new Random(w * 1000 + h);
        var y = new byte[sy * h]; rng.NextBytes(y);
        var u = new byte[sc * ((h + 1) / 2)]; rng.NextBytes(u);
        var v = new byte[sc * ((h + 1) / 2)]; rng.NextBytes(v);
        // Force the clamp extremes into the first samples: full-swing luma and chroma.
        y[0] = 0; y[1] = 255; u[0] = 0; v[0] = 255;

        var expected = Scalar(y, u, v, w, h, sy, sc);
        var actual = new byte[w * h * 4];
        fixed (byte* yp = y, up = u, vp = v, op = actual)
            Yuv.I420ToRgba(yp, up, vp, sy, sc, sc, op, w, h);

        Assert.Equal(expected, actual);
    }
}
