using System.Buffers.Binary;

namespace CupriFace.Woff2;

/// <summary>
/// Runs the WOFF 2 <c>glyf</c> transform backwards, rebuilding both <c>glyf</c> and the <c>loca</c>
/// table that never travelled.
///
/// <para>The transform splits what is normally one interleaved table into seven streams, each
/// holding one KIND of value for every glyph in turn — all the contour counts, then all the point
/// counts, then all the flags, and so on. That groups similar bytes together, which is what gives
/// Brotli something to compress. Undoing it means walking all seven at once, one glyph at a time,
/// and the streams only stay in step if every glyph consumes exactly what it should — so a
/// malformed font shows up as a stream running short rather than as a wrong-looking glyph.</para>
///
/// <para>Coordinates are the interesting part. Instead of the SFNT's flag byte plus one or two
/// bytes per axis, each point is one of 128 cases chosen by its flag, packing the x and y deltas
/// and their signs into one to four bytes. Small movements — which is most of them in a typeface —
/// cost a single byte for both axes.</para>
/// </summary>
internal static class Woff2Glyf
{
    /// <summary>Rebuilds (glyf, loca) from the transformed glyf table.</summary>
    internal static (byte[] Glyf, byte[] Loca) Reconstruct(byte[] transformed, byte[] arg)
    {
        var d = transformed.AsSpan();
        if (d.Length < 36)
            throw new ArgumentException("Transformed 'glyf' is shorter than its own header.", nameof(arg));

        var optionFlags = BinaryPrimitives.ReadUInt16BigEndian(d[2..]);
        var numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(d[4..]);
        var indexFormat = BinaryPrimitives.ReadUInt16BigEndian(d[6..]);

        var sizes = new int[7];
        for (var i = 0; i < 7; i++)
        {
            var v = BinaryPrimitives.ReadUInt32BigEndian(d[(8 + i * 4)..]);
            if (v > int.MaxValue) throw new ArgumentException("Transformed 'glyf' declares an impossible stream size.", nameof(arg));
            sizes[i] = (int)v;
        }

        // The seven streams follow the header in this order. bboxStream is really two: a bitmap
        // saying which glyphs carry an explicit box, then the boxes themselves.
        var at = 36;
        var nContour = Slice(d, ref at, sizes[0], "nContour", arg);
        var nPoints = Slice(d, ref at, sizes[1], "nPoints", arg);
        var flagStream = Slice(d, ref at, sizes[2], "flag", arg);
        var glyphStream = Slice(d, ref at, sizes[3], "glyph", arg);
        var composite = Slice(d, ref at, sizes[4], "composite", arg);
        var bbox = Slice(d, ref at, sizes[5], "bbox", arg);
        var instructions = Slice(d, ref at, sizes[6], "instruction", arg);

        // OVERLAP_SIMPLE, added to the format later: one bit per glyph, present only when the
        // option flag says so, and it sets a flag on the first point of a simple glyph.
        var overlapBitmap = (optionFlags & 1) != 0
            ? Slice(d, ref at, (numGlyphs + 7) / 8, "overlapSimpleBitmap", arg)
            : default;

        var bboxBitmapLen = 4 * ((numGlyphs + 31) / 32);
        if (bbox.Length < bboxBitmapLen)
            throw new ArgumentException("Transformed 'glyf' bbox stream is too short for its bitmap.", nameof(arg));
        var bboxBitmap = bbox[..bboxBitmapLen];
        var bboxValues = bbox[bboxBitmapLen..];

        var nContourAt = 0;
        var nPointsAt = 0;
        var flagAt = 0;
        var glyphAt = 0;
        var compositeAt = 0;
        var bboxAt = 0;
        var instrAt = 0;

        var glyf = new System.IO.MemoryStream(Math.Max(1024, transformed.Length * 2));
        var offsets = new uint[numGlyphs + 1];

        for (var g = 0; g < numGlyphs; g++)
        {
            offsets[g] = (uint)glyf.Length;

            if (nContourAt + 2 > nContour.Length)
                throw new ArgumentException($"Transformed 'glyf' nContour stream ran out at glyph {g}.", nameof(arg));
            var numberOfContours = BinaryPrimitives.ReadInt16BigEndian(nContour[nContourAt..]);
            nContourAt += 2;

            if (numberOfContours == 0)
                continue;                                  // an empty glyph occupies no bytes at all

            var hasExplicitBbox = (bboxBitmap[g >> 3] & (0x80 >> (g & 7))) != 0;

            if (numberOfContours < 0)
            {
                // ---- composite ----------------------------------------------------------------
                // A composite's own box is NOT derivable from its parts here, so the format
                // requires it to be present.
                if (!hasExplicitBbox)
                    throw new ArgumentException(
                        $"Transformed 'glyf' glyph {g} is composite but carries no bounding box, " +
                        "which the format requires for composites.", nameof(arg));

                var start = compositeAt;
                var haveInstructions = ScanComposite(composite, ref compositeAt, g, arg);
                var componentBytes = composite[start..compositeAt];

                ReadOnlySpan<byte> instr = default;
                if (haveInstructions)
                {
                    var (io, il) = TakeInstructions(glyphStream, ref glyphAt, instructions.Length, ref instrAt, g, arg);
                    instr = instructions.Slice(io, il);
                }

                WriteBboxAndBody(glyf, numberOfContours, bboxValues, ref bboxAt, componentBytes, instr, g, arg);
            }
            else
            {
                // ---- simple -------------------------------------------------------------------
                var endPts = new ushort[numberOfContours];
                var total = 0;
                for (var c = 0; c < numberOfContours; c++)
                {
                    var pts = Read255UInt16(nPoints, ref nPointsAt, g, arg);
                    total += pts;
                    if (total is 0 or > 0xFFFF)
                        throw new ArgumentException($"Transformed 'glyf' glyph {g} has an impossible point count.", nameof(arg));
                    endPts[c] = (ushort)(total - 1);
                }

                var flags = new byte[total];
                var xs = new short[total];
                var ys = new short[total];
                DecodeTriplets(flagStream, ref flagAt, glyphStream, ref glyphAt, total, flags, xs, ys, g, arg);

                if (!overlapBitmap.IsEmpty && (overlapBitmap[g >> 3] & (0x80 >> (g & 7))) != 0)
                    flags[0] |= 0x40;                      // OVERLAP_SIMPLE rides bit 6 of point 0

                var (so, sl) = TakeInstructions(glyphStream, ref glyphAt, instructions.Length, ref instrAt, g, arg);
                var instr = instructions.Slice(so, sl);

                WriteSimpleGlyph(glyf, hasExplicitBbox, bboxValues, ref bboxAt, endPts, flags, xs, ys, instr);
            }

            Pad4(glyf);
        }
        offsets[numGlyphs] = (uint)glyf.Length;

        return (glyf.ToArray(), BuildLoca(offsets, indexFormat, arg));
    }

    // ---- glyph bodies -------------------------------------------------------------------------

    private static void WriteSimpleGlyph(System.IO.MemoryStream glyf, bool hasExplicitBbox,
        ReadOnlySpan<byte> bboxValues, ref int bboxAt, ushort[] endPts, byte[] flags,
        short[] xs, short[] ys, ReadOnlySpan<byte> instr)
    {
        // A simple glyph's box is the extent of its points unless one was transmitted. Computing it
        // is not an optimisation — the transform DROPS the box precisely because it is derivable.
        short xMin, yMin, xMax, yMax;
        if (hasExplicitBbox)
        {
            xMin = BinaryPrimitives.ReadInt16BigEndian(bboxValues[bboxAt..]);
            yMin = BinaryPrimitives.ReadInt16BigEndian(bboxValues[(bboxAt + 2)..]);
            xMax = BinaryPrimitives.ReadInt16BigEndian(bboxValues[(bboxAt + 4)..]);
            yMax = BinaryPrimitives.ReadInt16BigEndian(bboxValues[(bboxAt + 6)..]);
            bboxAt += 8;
        }
        else
        {
            xMin = yMin = short.MaxValue;
            xMax = yMax = short.MinValue;
            for (var i = 0; i < xs.Length; i++)
            {
                if (xs[i] < xMin) xMin = xs[i];
                if (xs[i] > xMax) xMax = xs[i];
                if (ys[i] < yMin) yMin = ys[i];
                if (ys[i] > yMax) yMax = ys[i];
            }
        }

        Span<byte> head = stackalloc byte[10];
        BinaryPrimitives.WriteInt16BigEndian(head, (short)endPts.Length);
        BinaryPrimitives.WriteInt16BigEndian(head[2..], xMin);
        BinaryPrimitives.WriteInt16BigEndian(head[4..], yMin);
        BinaryPrimitives.WriteInt16BigEndian(head[6..], xMax);
        BinaryPrimitives.WriteInt16BigEndian(head[8..], yMax);
        glyf.Write(head);

        Span<byte> two = stackalloc byte[2];
        foreach (var e in endPts) { BinaryPrimitives.WriteUInt16BigEndian(two, e); glyf.Write(two); }
        BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)instr.Length); glyf.Write(two);
        glyf.Write(instr);

        // Flags and coordinates go out in the SFNT's own shape: one flag byte per point (no run
        // compression — legal, and a byte or two larger than an encoder would manage), then the x
        // deltas, then the y deltas, each as an int16 with its SHORT/SAME bits left clear.
        for (var i = 0; i < flags.Length; i++)
            glyf.WriteByte((byte)(flags[i] & ~0x02 & ~0x04 & ~0x10 & ~0x20));

        short prev = 0;
        for (var i = 0; i < xs.Length; i++) { BinaryPrimitives.WriteInt16BigEndian(two, (short)(xs[i] - prev)); glyf.Write(two); prev = xs[i]; }
        prev = 0;
        for (var i = 0; i < ys.Length; i++) { BinaryPrimitives.WriteInt16BigEndian(two, (short)(ys[i] - prev)); glyf.Write(two); prev = ys[i]; }
    }

    private static void WriteBboxAndBody(System.IO.MemoryStream glyf, short numberOfContours,
        ReadOnlySpan<byte> bboxValues, ref int bboxAt, ReadOnlySpan<byte> components,
        ReadOnlySpan<byte> instr, int g, byte[] arg)
    {
        if (bboxAt + 8 > bboxValues.Length)
            throw new ArgumentException($"Transformed 'glyf' bbox stream ran out at glyph {g}.", nameof(arg));

        Span<byte> head = stackalloc byte[10];
        BinaryPrimitives.WriteInt16BigEndian(head, numberOfContours);
        bboxValues.Slice(bboxAt, 8).CopyTo(head[2..]);
        bboxAt += 8;
        glyf.Write(head);
        glyf.Write(components);

        if (!instr.IsEmpty)
        {
            Span<byte> two = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)instr.Length);
            glyf.Write(two);
            glyf.Write(instr);
        }
    }

    /// <summary>Walks a composite's component records to find where they end, reporting whether any
    /// asked for instructions. The records are copied through verbatim — the transform does not
    /// touch them — so this only needs their LENGTH, which depends on each record's own flags.</summary>
    private static bool ScanComposite(ReadOnlySpan<byte> composite, ref int at, int g, byte[] arg)
    {
        var haveInstructions = false;
        while (true)
        {
            if (at + 4 > composite.Length)
                throw new ArgumentException($"Transformed 'glyf' composite stream ran out at glyph {g}.", nameof(arg));
            var flags = BinaryPrimitives.ReadUInt16BigEndian(composite[at..]);
            at += 4;                                        // flags + glyphIndex

            at += (flags & 0x0001) != 0 ? 4 : 2;            // ARG_1_AND_2_ARE_WORDS
            if ((flags & 0x0008) != 0) at += 2;             // WE_HAVE_A_SCALE
            else if ((flags & 0x0040) != 0) at += 4;        // WE_HAVE_AN_X_AND_Y_SCALE
            else if ((flags & 0x0080) != 0) at += 8;        // WE_HAVE_A_TWO_BY_TWO
            if ((flags & 0x0100) != 0) haveInstructions = true;

            if (at > composite.Length)
                throw new ArgumentException($"Transformed 'glyf' composite record overruns at glyph {g}.", nameof(arg));
            if ((flags & 0x0020) == 0) break;               // MORE_COMPONENTS
        }
        return haveInstructions;
    }

    /// <summary>Instruction length travels in the glyph stream, the bytes in the instruction
    /// stream — two places, because the lengths compress better next to the other small numbers.</summary>
    // Returns where the instructions are rather than a slice of them: a returned span would tie its
    // lifetime to the instruction stream's, and the caller already holds that.
    private static (int Offset, int Length) TakeInstructions(ReadOnlySpan<byte> glyphStream, ref int glyphAt,
        int instructionsLength, ref int instrAt, int g, byte[] arg)
    {
        var len = Read255UInt16(glyphStream, ref glyphAt, g, arg);
        if (instrAt + len > instructionsLength)
            throw new ArgumentException($"Transformed 'glyf' instruction stream ran out at glyph {g}.", nameof(arg));
        var offset = instrAt;
        instrAt += len;
        return (offset, len);
    }

    // ---- coordinates --------------------------------------------------------------------------

    /// <summary>
    /// The 128-case coordinate decoding. One flag byte per point picks how many bytes the point's
    /// x and y deltas occupy and how the bits divide between them; the flag's low bit is x's sign
    /// and the next bit up is y's. The high bit of the flag is the on-curve marker and is the one
    /// piece that survives into the SFNT unchanged.
    /// </summary>
    private static void DecodeTriplets(ReadOnlySpan<byte> flagStream, ref int flagAt,
        ReadOnlySpan<byte> glyphStream, ref int glyphAt, int nPoints,
        byte[] flags, short[] xs, short[] ys, int g, byte[] arg)
    {
        if (flagAt + nPoints > flagStream.Length)
            throw new ArgumentException($"Transformed 'glyf' flag stream ran out at glyph {g}.", nameof(arg));

        int x = 0, y = 0;
        for (var i = 0; i < nPoints; i++)
        {
            var raw = flagStream[flagAt + i];
            var onCurve = (raw & 0x80) == 0;
            var flag = raw & 0x7F;

            var nBytes = flag < 84 ? 1 : flag < 120 ? 2 : flag < 124 ? 3 : 4;
            if (glyphAt + nBytes > glyphStream.Length)
                throw new ArgumentException($"Transformed 'glyf' glyph stream ran out at glyph {g}, point {i}.", nameof(arg));

            int dx, dy;
            if (flag < 10)
            {
                dx = 0;
                dy = Signed(flag, ((flag & 14) << 7) + glyphStream[glyphAt]);
            }
            else if (flag < 20)
            {
                dx = Signed(flag, (((flag - 10) & 14) << 7) + glyphStream[glyphAt]);
                dy = 0;
            }
            else if (flag < 84)
            {
                var b0 = flag - 20;
                var b1 = glyphStream[glyphAt];
                dx = Signed(flag, 1 + (b0 & 0x30) + (b1 >> 4));
                dy = Signed(flag >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F));
            }
            else if (flag < 120)
            {
                var b0 = flag - 84;
                dx = Signed(flag, 1 + ((b0 / 12) << 8) + glyphStream[glyphAt]);
                dy = Signed(flag >> 1, 1 + (((b0 % 12) >> 2) << 8) + glyphStream[glyphAt + 1]);
            }
            else if (flag < 124)
            {
                var b1 = glyphStream[glyphAt + 1];
                dx = Signed(flag, (glyphStream[glyphAt] << 4) + (b1 >> 4));
                dy = Signed(flag >> 1, ((b1 & 0x0F) << 8) + glyphStream[glyphAt + 2]);
            }
            else
            {
                dx = Signed(flag, (glyphStream[glyphAt] << 8) + glyphStream[glyphAt + 1]);
                dy = Signed(flag >> 1, (glyphStream[glyphAt + 2] << 8) + glyphStream[glyphAt + 3]);
            }

            glyphAt += nBytes;
            x += dx;
            y += dy;
            if (x is < short.MinValue or > short.MaxValue || y is < short.MinValue or > short.MaxValue)
                throw new ArgumentException($"Transformed 'glyf' glyph {g} point {i} leaves the coordinate range.", nameof(arg));

            xs[i] = (short)x;
            ys[i] = (short)y;
            flags[i] = (byte)(onCurve ? 0x01 : 0x00);
        }
        flagAt += nPoints;
    }

    /// <summary>The flag's low bit is the sign: set means positive.</summary>
    private static int Signed(int flag, int magnitude) => (flag & 1) != 0 ? magnitude : -magnitude;

    // ---- primitives ---------------------------------------------------------------------------

    /// <summary>255UInt16: one byte for 0..252, and three escape codes for everything else. Tuned
    /// for point counts and instruction lengths, which are usually small and occasionally not.</summary>
    private static int Read255UInt16(ReadOnlySpan<byte> s, ref int at, int g, byte[] arg)
    {
        // 253 is the escape to a full 16-bit value; 255 and 254 each add one byte to a base of 253
        // and 506. Note the encoding is NOT canonical — 506 is reachable three ways — so a decoder
        // must accept all of them and can never assume a shortest form.
        const int lowest = 253;
        const byte wordCode = 253, oneMoreByte2 = 254, oneMoreByte1 = 255;

        if (at >= s.Length) throw new ArgumentException($"Transformed 'glyf' stream ran out at glyph {g}.", nameof(arg));
        var code = s[at++];
        switch (code)
        {
            case wordCode:
                if (at + 2 > s.Length) throw new ArgumentException($"Transformed 'glyf' 255UInt16 truncated at glyph {g}.", nameof(arg));
                var v = BinaryPrimitives.ReadUInt16BigEndian(s[at..]);
                at += 2;
                return v;
            case oneMoreByte1:
                if (at >= s.Length) throw new ArgumentException($"Transformed 'glyf' 255UInt16 truncated at glyph {g}.", nameof(arg));
                return s[at++] + lowest;
            case oneMoreByte2:
                if (at >= s.Length) throw new ArgumentException($"Transformed 'glyf' 255UInt16 truncated at glyph {g}.", nameof(arg));
                return s[at++] + lowest * 2;
            default:
                return code;
        }
    }

    /// <summary>Rebuilds <c>loca</c> from the offsets the glyphs produced. The short format stores
    /// offset/2, so it can only be used when every offset is even — which the 4-byte padding
    /// guarantees.</summary>
    private static byte[] BuildLoca(uint[] offsets, ushort indexFormat, byte[] arg)
    {
        if (indexFormat == 0)
        {
            var loca = new byte[offsets.Length * 2];
            for (var i = 0; i < offsets.Length; i++)
            {
                if ((offsets[i] & 1) != 0 || offsets[i] / 2 > 0xFFFF)
                    throw new ArgumentException(
                        "Transformed 'glyf' asks for the short 'loca' format but the glyphs do not " +
                        "fit it. The font's indexFormat and its glyph data disagree.", nameof(arg));
                BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(i * 2), (ushort)(offsets[i] / 2));
            }
            return loca;
        }

        var wide = new byte[offsets.Length * 4];
        for (var i = 0; i < offsets.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(wide.AsSpan(i * 4), offsets[i]);
        return wide;
    }

    private static void Pad4(System.IO.MemoryStream s)
    {
        while ((s.Length & 3) != 0) s.WriteByte(0);
    }

    // `scoped ref` on the cursor: without it the returned slice inherits the cursor local's
    // lifetime rather than the buffer's, and every span derived from it becomes unusable.
    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> d, scoped ref int at, int len, string name, byte[] arg)
    {
        if (len < 0 || at + len > d.Length)
            throw new ArgumentException($"Transformed 'glyf' {name} stream runs past the end of the table.", nameof(arg));
        var s = d.Slice(at, len);
        at += len;
        return s;
    }
}
