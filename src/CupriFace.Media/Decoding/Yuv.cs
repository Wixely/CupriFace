using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace CupriFace.Media.Decoding;

/// <summary>
/// I420 → RGBA conversion (BT.601 studio swing — what WebM recorders emit). The scalar loop was
/// the decode ceiling: 33.9 ms per 1080p frame, more than the whole 30 fps budget before libvpx
/// did any work. The <see cref="Vector128{T}"/> path (SSE2/NEON — everywhere .NET runs) processes
/// 8 pixels per iteration with the identical integer arithmetic, byte-exact against the scalar
/// reference (the test proves it), which stays as the fallback and the tail handler.
/// </summary>
public static unsafe class Yuv
{
    /// <summary>Convert one I420 frame. Plane pointers + strides as libvpx hands them out;
    /// <paramref name="dst"/> receives tightly-packed RGBA (<c>width * 4</c> per row).</summary>
    public static void I420ToRgba(byte* y, byte* u, byte* v, int strideY, int strideU, int strideV,
        byte* dst, int width, int height)
    {
        for (var row = 0; row < height; row++)
        {
            var yRow = y + row * strideY;
            var uRow = u + (row >> 1) * strideU;
            var vRow = v + (row >> 1) * strideV;
            var outRow = dst + row * width * 4;
            var col = 0;

            if (Vector128.IsHardwareAccelerated)
                for (; col + 8 <= width; col += 8)
                    Convert8(yRow + col, uRow + (col >> 1), vRow + (col >> 1), outRow + col * 4);

            for (; col < width; col++)   // tail (and the whole row on non-SIMD hardware)
            {
                var c = 298 * (yRow[col] - 16);
                var d = uRow[col >> 1] - 128;
                var e = vRow[col >> 1] - 128;
                var p = outRow + col * 4;
                p[0] = Clamp((c + 409 * e + 128) >> 8);
                p[1] = Clamp((c - 100 * d - 208 * e + 128) >> 8);
                p[2] = Clamp((c + 516 * d + 128) >> 8);
                p[3] = 255;
            }
        }
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    // Duplicate 4 chroma samples to 8 lanes (a,a,b,b,c,c,d,d) — each U/V sample covers 2 pixels.
    private static readonly Vector128<ushort> ChromaPairs = Vector128.Create((ushort)0, 0, 1, 1, 2, 2, 3, 3);
    // Interleave two 8-byte halves byte-wise: r0,g0,r1,g1,…
    private static readonly Vector128<byte> InterleaveBytes = Vector128.Create((byte)0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15);
    // Interleave two 4-ushort halves pair-wise: rg0,ba0,rg1,ba1,… (lane indices within ONE vector).
    private static readonly Vector128<ushort> InterleavePairs = Vector128.Create((ushort)0, 4, 1, 5, 2, 6, 3, 7);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Convert8(byte* yPtr, byte* uPtr, byte* vPtr, byte* outPtr)
    {
        // 8 luma bytes → 8 int32 lanes of 298*(Y-16); 4 chroma bytes each → 8 paired lanes − 128.
        var y16 = Vector128.WidenLower(Vector128.CreateScalar(*(ulong*)yPtr).AsByte()).AsInt16();
        var u16 = Vector128.Shuffle(Vector128.WidenLower(Vector128.CreateScalar(*(uint*)uPtr).AsByte()).AsUInt16(), ChromaPairs).AsInt16();
        var v16 = Vector128.Shuffle(Vector128.WidenLower(Vector128.CreateScalar(*(uint*)vPtr).AsByte()).AsUInt16(), ChromaPairs).AsInt16();

        var cLo = (Vector128.WidenLower(y16.AsUInt16()).AsInt32() - Vector128.Create(16)) * Vector128.Create(298);
        var cHi = (Vector128.WidenUpper(y16.AsUInt16()).AsInt32() - Vector128.Create(16)) * Vector128.Create(298);
        var dLo = Vector128.WidenLower(u16.AsUInt16()).AsInt32() - Vector128.Create(128);
        var dHi = Vector128.WidenUpper(u16.AsUInt16()).AsInt32() - Vector128.Create(128);
        var eLo = Vector128.WidenLower(v16.AsUInt16()).AsInt32() - Vector128.Create(128);
        var eHi = Vector128.WidenUpper(v16.AsUInt16()).AsInt32() - Vector128.Create(128);

        var half = Vector128.Create(128);
        var rLo = (cLo + eLo * Vector128.Create(409) + half) >> 8;
        var rHi = (cHi + eHi * Vector128.Create(409) + half) >> 8;
        var gLo = (cLo - dLo * Vector128.Create(100) - eLo * Vector128.Create(208) + half) >> 8;
        var gHi = (cHi - dHi * Vector128.Create(100) - eHi * Vector128.Create(208) + half) >> 8;
        var bLo = (cLo + dLo * Vector128.Create(516) + half) >> 8;
        var bHi = (cHi + dHi * Vector128.Create(516) + half) >> 8;

        var r8 = Pack(rLo, rHi);
        var g8 = Pack(gLo, gHi);
        var b8 = Pack(bLo, bHi);

        // Interleave r/g and b/255 byte-wise (rg = r0,g0,r1,g1,… as ushort pairs; ba likewise),
        // then pair-wise into px0..3 and px4..7, and store 32 bytes of RGBA.
        var rg = Vector128.Shuffle(Vector128.Create(r8, g8).AsByte(), InterleaveBytes).AsUInt16();
        var ba = Vector128.Shuffle(Vector128.Create(b8, ulong.MaxValue).AsByte(), InterleaveBytes).AsUInt16();
        Vector128.Shuffle(Vector128.Create(rg.GetLower(), ba.GetLower()), InterleavePairs).AsByte().Store(outPtr);
        Vector128.Shuffle(Vector128.Create(rg.GetUpper(), ba.GetUpper()), InterleavePairs).AsByte().Store(outPtr + 16);
        return;

        // Clamp two int32 quads to 0..255 and pack them into one 8-byte lane (as a ulong).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong Pack(Vector128<int> lo, Vector128<int> hi)
        {
            var zero = Vector128<int>.Zero;
            var max = Vector128.Create(255);
            var s = Vector128.Narrow(Vector128.Min(Vector128.Max(lo, zero), max),
                                     Vector128.Min(Vector128.Max(hi, zero), max));
            return Vector128.Narrow(s.AsUInt16(), s.AsUInt16()).AsUInt64().GetElement(0);
        }
    }
}
