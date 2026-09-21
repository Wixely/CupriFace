using System.Buffers.Binary;

namespace CupriFace.Woff2;

/// <summary>
/// Unwraps a WOFF 2 file to the SFNT (TTF/OTF) bytes Skia reads.
///
/// <para>WOFF 2 is not WOFF 1 with a better compressor. WOFF 1 is the same tables behind a header,
/// each optionally zlib-deflated — a repackaging, which is why the engine's own <c>Woff</c> handles
/// it in a page. WOFF 2 Brotli-compresses every table into ONE stream and, for TrueType outlines,
/// replaces <c>glyf</c> and <c>loca</c> with a transform that has to be run backwards: seven
/// parallel byte streams, a 128-case variable-length coordinate encoding, and a <c>loca</c> table
/// that does not travel at all because it is implied by the glyphs.</para>
///
/// <para>Written from the W3C WOFF 2 Recommendation (8 August 2024), which specifies the transform
/// in full and carries royalty-free patent commitments from Google (for Brotli) and Monotype. No
/// code is ported from the reference implementation, so nothing here adds a third-party copyright
/// to this repository — the same reason the engine's WOFF 1 reader is first-party.</para>
/// </summary>
public static class Woff2Reader
{
    private const uint Woff2Signature = 0x774F4632;  // 'wOF2'
    private const uint TtcFlavor = 0x74746366;       // 'ttcf'

    /// <summary>Known table tags, indexed by the 6-bit index in a directory entry's flags byte.
    /// Index 63 means "a 4-byte tag follows instead". The order is normative — it is how a tag is
    /// transmitted in one byte instead of four.</summary>
    private static readonly string[] KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post",
        "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
        "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea",
        "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
        "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar",
        "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
        "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop",
        "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    ];

    /// <summary>True when these bytes are a WOFF 2 file.</summary>
    public static bool IsWoff2(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == Woff2Signature;

    /// <summary>One table as the directory describes it, before its bytes are known.</summary>
    private sealed class Entry
    {
        public uint Tag;
        public int TransformVersion;
        public int OrigLength;
        public int TransformLength;   // == OrigLength when the table is not transformed
        public bool Transformed;
        public byte[] Data = [];      // filled in from the decompressed stream, then reconstructed
    }

    /// <summary>
    /// The SFNT payload of a WOFF 2 file.
    /// </summary>
    /// <exception cref="ArgumentException">The file is malformed, truncated, or uses a feature this
    /// reader does not implement. Every message names what and why — a font that silently renders in
    /// a substitute face is the failure this package exists to end.</exception>
    public static byte[] ToSfnt(byte[] woff2)
    {
        ArgumentNullException.ThrowIfNull(woff2);
        var d = woff2.AsSpan();
        if (d.Length < 48 || !IsWoff2(d))
            throw new ArgumentException("Not a WOFF 2 file.", nameof(woff2));

        var flavor = BinaryPrimitives.ReadUInt32BigEndian(d[4..]);
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(d[12..]);
        var totalSfntSize = BinaryPrimitives.ReadUInt32BigEndian(d[16..]);
        var totalCompressedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(d[20..]);

        if (flavor == TtcFlavor)
            throw new ArgumentException(
                "This is a WOFF 2 font COLLECTION (ttcf). Collections carry a second directory of " +
                "their own and are not implemented here — extract the face you need, or register a " +
                "single-face WOFF 2.", nameof(woff2));
        if (numTables == 0)
            throw new ArgumentException("WOFF 2 declares no tables.", nameof(woff2));

        // ---- table directory -----------------------------------------------------------------
        var entries = new Entry[numTables];
        var p = 48;
        for (var i = 0; i < numTables; i++)
        {
            if (p >= d.Length) throw new ArgumentException("WOFF 2 table directory is truncated.", nameof(woff2));
            var flags = d[p++];
            var index = flags & 0x3F;
            var e = new Entry { TransformVersion = (flags >> 6) & 0x03 };

            if (index == 0x3F)
            {
                if (p + 4 > d.Length) throw new ArgumentException("WOFF 2 custom tag is truncated.", nameof(woff2));
                e.Tag = BinaryPrimitives.ReadUInt32BigEndian(d[p..]);
                p += 4;
            }
            else
            {
                if (index >= KnownTags.Length)
                    throw new ArgumentException($"WOFF 2 table {i} uses reserved tag index {index}.", nameof(woff2));
                e.Tag = TagOf(KnownTags[index]);
            }

            e.OrigLength = (int)ReadBase128(d, ref p, woff2);

            // glyf and loca are transformed when the version is 0 (the null transform is 3 for them);
            // every other table is transformed when the version is non-zero. Only then does a
            // transformLength travel.
            var isGlyfOrLoca = e.Tag is 0x676C7966 or 0x6C6F6361;  // 'glyf' 'loca'
            e.Transformed = isGlyfOrLoca ? e.TransformVersion == 0 : e.TransformVersion != 0;
            e.TransformLength = e.Transformed ? (int)ReadBase128(d, ref p, woff2) : e.OrigLength;

            if (e.OrigLength < 0 || e.TransformLength < 0)
                throw new ArgumentException($"WOFF 2 table {Tag(e.Tag)} declares a negative length.", nameof(woff2));
            entries[i] = e;
        }

        // ---- one Brotli stream holding every table, back to back -----------------------------
        if (totalCompressedSize < 0 || p + totalCompressedSize > d.Length)
            throw new ArgumentException("WOFF 2 compressed data runs past the end of the file.", nameof(woff2));

        var expanded = 0L;
        foreach (var e in entries) expanded += e.TransformLength;
        if (expanded > int.MaxValue)
            throw new ArgumentException("WOFF 2 table streams do not fit in memory.", nameof(woff2));

        var streams = Brotli.Decompress(woff2.AsSpan(p, totalCompressedSize), (int)expanded);

        var at = 0;
        foreach (var e in entries)
        {
            e.Data = streams.AsSpan(at, e.TransformLength).ToArray();
            at += e.TransformLength;
        }

        // ---- undo the transforms -------------------------------------------------------------
        ReconstructTransforms(entries, woff2);

        // ---- reassemble the SFNT -------------------------------------------------------------
        return BuildSfnt(flavor, entries, totalSfntSize);
    }

    /// <summary>Runs every transform backwards, in the one order that works: <c>glyf</c> rebuilds
    /// <c>loca</c> as a side effect, so it has to go first.</summary>
    private static void ReconstructTransforms(Entry[] entries, byte[] woff2)
    {
        var glyf = Find(entries, "glyf");
        var loca = Find(entries, "loca");

        if (glyf?.Transformed == true)
        {
            if (loca is null)
                throw new ArgumentException("WOFF 2 has a transformed 'glyf' but no 'loca' table.", nameof(woff2));
            if (!loca.Transformed)
                throw new ArgumentException(
                    "WOFF 2 has a transformed 'glyf' with an untransformed 'loca'. The transform " +
                    "rebuilds both together, so this combination cannot be decoded.", nameof(woff2));

            var (glyfBytes, locaBytes) = Woff2Glyf.Reconstruct(glyf.Data, woff2);
            glyf.Data = glyfBytes;
            loca.Data = locaBytes;
            glyf.Transformed = loca.Transformed = false;
        }
        else if (loca?.Transformed == true)
        {
            throw new ArgumentException(
                "WOFF 2 has a transformed 'loca' without a transformed 'glyf'. 'loca' carries no " +
                "data of its own — it is rebuilt from the glyphs — so there is nothing to decode " +
                "it from.", nameof(woff2));
        }

        // Anything else claiming a transform is one this reader does not implement. The hmtx
        // transform (version 1) drops the left-side-bearing arrays and rebuilds them from glyph
        // bounding boxes; it is legal and some encoders emit it. Saying so beats a font that
        // loads and then measures wrongly.
        foreach (var e in entries)
        {
            if (!e.Transformed) continue;
            throw new ArgumentException(
                Tag(e.Tag) == "hmtx"
                    ? "This WOFF 2 uses the optional 'hmtx' transform, which this reader does not " +
                      "implement yet. Re-encode without it (fonttools does this by default), or " +
                      "supply TTF/OTF."
                    : $"WOFF 2 table '{Tag(e.Tag)}' declares transform version {e.TransformVersion}, " +
                      "which is not a transform this reader knows.", nameof(woff2));
        }
    }

    /// <summary>Writes the offset table, the records and the tables, and fixes up the checksums.
    /// Records go in ascending tag order as the SFNT spec requires; table DATA stays in the order
    /// the WOFF 2 directory gave, which is what the format intends and what keeps offsets sane.</summary>
    private static byte[] BuildSfnt(uint flavor, Entry[] entries, uint declaredTotal)
    {
        var n = entries.Length;
        var recordsEnd = 12 + n * 16;

        var size = recordsEnd;
        foreach (var e in entries) size += Align4(e.Data.Length);
        var output = new byte[size];
        var span = output.AsSpan();

        // Offset table. searchRange/entrySelector/rangeShift are derived, and wrong values are
        // tolerated by most readers — which is exactly why they are worth getting right.
        BinaryPrimitives.WriteUInt32BigEndian(span, flavor);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)n);
        var entrySelector = (ushort)Math.Floor(Math.Log2(n));
        var searchRange = (ushort)((1 << entrySelector) * 16);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(n * 16 - searchRange));

        // Lay the tables out in directory order, remembering where each landed.
        var offsets = new int[n];
        var write = recordsEnd;
        for (var i = 0; i < n; i++)
        {
            offsets[i] = write;
            entries[i].Data.CopyTo(output.AsSpan(write));
            write += Align4(entries[i].Data.Length);   // the padding is already zero
        }

        // Records, sorted by tag.
        var order = Enumerable.Range(0, n).OrderBy(i => entries[i].Tag).ToArray();
        var headRecord = -1;
        for (var slot = 0; slot < n; slot++)
        {
            var i = order[slot];
            var rec = span[(12 + slot * 16)..];
            BinaryPrimitives.WriteUInt32BigEndian(rec, entries[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(rec[4..], Checksum(entries[i].Data));
            BinaryPrimitives.WriteUInt32BigEndian(rec[8..], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(rec[12..], (uint)entries[i].Data.Length);
            if (Tag(entries[i].Tag) == "head") headRecord = i;
        }

        // head.checkSumAdjustment covers the whole file, so it can only be computed once the file
        // exists — and it must be zeroed while computing, which is why it is done last.
        if (headRecord >= 0 && entries[headRecord].Data.Length >= 12)
        {
            var headAt = offsets[headRecord];
            output.AsSpan(headAt + 8, 4).Clear();
            var whole = Checksum(output);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(headAt + 8), unchecked(0xB1B0AFBAu - whole));
        }

        // Advisory only: a mismatch means the file we built is not the size the file we read said it
        // would be, which is worth knowing but not worth refusing a font over, since the padding
        // rules leave room for honest disagreement.
        _ = declaredTotal;
        return output;
    }

    // ---- primitives ---------------------------------------------------------------------------

    /// <summary>UIntBase128: 7 bits per byte, high bit means "another byte follows". Capped at five
    /// bytes, and a leading zero byte is invalid — both rules exist so that one integer has exactly
    /// one encoding, which is what stops a hostile file smuggling a second reading past a check.</summary>
    private static uint ReadBase128(ReadOnlySpan<byte> d, ref int p, byte[] arg)
    {
        uint value = 0;
        for (var i = 0; i < 5; i++)
        {
            if (p >= d.Length) throw new ArgumentException("WOFF 2 ends inside a UIntBase128.", nameof(arg));
            var b = d[p++];
            if (i == 0 && b == 0x80) throw new ArgumentException("WOFF 2 UIntBase128 has a leading zero.", nameof(arg));
            if ((value & 0xFE000000) != 0) throw new ArgumentException("WOFF 2 UIntBase128 overflows.", nameof(arg));
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) return value;
        }
        throw new ArgumentException("WOFF 2 UIntBase128 is longer than five bytes.", nameof(arg));
    }

    private static Entry? Find(Entry[] entries, string tag)
    {
        var t = TagOf(tag);
        foreach (var e in entries) if (e.Tag == t) return e;
        return null;
    }

    private static int Align4(int n) => (n + 3) & ~3;

    /// <summary>The SFNT checksum: the table read as big-endian uint32s and summed, with the tail
    /// zero-padded to four bytes.</summary>
    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var i = 0;
        for (; i + 4 <= data.Length; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
        if (i < data.Length)
        {
            uint tail = 0;
            for (var k = 0; k < 4; k++) tail = (tail << 8) | (i + k < data.Length ? data[i + k] : 0u);
            sum += tail;
        }
        return sum;
    }

    internal static uint TagOf(string tag) =>
        ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

    internal static string Tag(uint tag) =>
        new([(char)(tag >> 24), (char)((tag >> 16) & 0xFF), (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF)]);
}
