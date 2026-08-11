namespace CupriFace.Media.Webm;

/// <summary>What a Matroska track carries. Values match Matroska's TrackType.</summary>
public enum WebmTrackKind { Other = 0, Video = 1, Audio = 2 }

/// <summary>One track's static description (from the Tracks element).</summary>
public sealed record WebmTrack(
    int Number, WebmTrackKind Kind, string CodecId,
    int Width, int Height,
    double SampleRate, int Channels,
    byte[]? CodecPrivate, double CodecDelaySeconds);

/// <summary>One demuxed frame/packet: which track, when, whether it can start decode, the bytes
/// (a slice over the file buffer — no copy).</summary>
public readonly record struct WebmBlock(int Track, double TimeSeconds, bool Keyframe, ReadOnlyMemory<byte> Data);

/// <summary>
/// A managed WebM (Matroska-subset) demuxer — the container side of <c>CupriFace.Media</c>,
/// deliberately pure C# so the package's native surface is decoders only. Parses the subset real
/// WebM uses: EBML header, Segment (unknown-size tolerated), Info (TimestampScale/Duration),
/// Tracks (V_VP9/V_VP8/A_OPUS/…), Clusters (unknown-size tolerated — MediaRecorder emits these)
/// with SimpleBlocks and BlockGroups, and all three lacing modes. Whole-buffer parsing; block
/// payloads are slices of the input, indexed once up front.
/// </summary>
public sealed class WebmFile
{
    public IReadOnlyList<WebmTrack> Tracks { get; }
    public IReadOnlyList<WebmBlock> Blocks { get; }

    /// <summary>Nanoseconds per timestamp unit (Matroska default 1,000,000 = 1 ms).</summary>
    public long TimestampScaleNs { get; }

    /// <summary>From the Info element; often ABSENT in recorded/streamed files (MediaRecorder) —
    /// then the last block's timestamp is the best available estimate.</summary>
    public double? DurationSeconds { get; }

    public WebmTrack? VideoTrack => FirstOf(WebmTrackKind.Video);
    public WebmTrack? AudioTrack => FirstOf(WebmTrackKind.Audio);
    private WebmTrack? FirstOf(WebmTrackKind kind)
    {
        foreach (var t in Tracks) if (t.Kind == kind) return t;
        return null;
    }

    private WebmFile(List<WebmTrack> tracks, List<WebmBlock> blocks, long scaleNs, double? duration)
    {
        Tracks = tracks;
        Blocks = blocks;
        TimestampScaleNs = scaleNs;
        DurationSeconds = duration;
    }

    // ---- element ids (marker bit kept, as they appear in the file) ---------------------------
    private const uint EbmlHeader = 0x1A45DFA3;
    private const uint Segment = 0x18538067;
    private const uint SegInfo = 0x1549A966;
    private const uint TimestampScaleId = 0x2AD7B1;
    private const uint DurationId = 0x4489;
    private const uint TracksId = 0x1654AE6B;
    private const uint TrackEntry = 0xAE;
    private const uint TrackNumber = 0xD7;
    private const uint TrackType = 0x83;
    private const uint CodecId = 0x86;
    private const uint CodecPrivate = 0x63A2;
    private const uint CodecDelay = 0x56AA;
    private const uint VideoEl = 0xE0;
    private const uint PixelWidth = 0xB0;
    private const uint PixelHeight = 0xBA;
    private const uint AudioEl = 0xE1;
    private const uint SamplingFrequency = 0xB5;
    private const uint ChannelsId = 0x9F;
    private const uint Cluster = 0x1F43B675;
    private const uint ClusterTimestamp = 0xE7;
    private const uint SimpleBlock = 0xA3;
    private const uint BlockGroup = 0xA0;
    private const uint BlockId = 0xA1;
    private const uint ReferenceBlock = 0xFB;

    // Segment-level ids: an unknown-size Cluster ends when one of these (or EOF) appears next.
    private static bool IsSegmentLevel(uint id) => id is Cluster or SegInfo or TracksId
        or 0x114D9B74 /* SeekHead */ or 0x1C53BB6B /* Cues */ or 0x1043A770 /* Chapters */
        or 0x1254C367 /* Tags */ or 0x1941A469 /* Attachments */;

    public static WebmFile Parse(byte[] bytes)
    {
        var r = new Reader(bytes);
        var tracks = new List<WebmTrack>();
        var blocks = new List<WebmBlock>();
        long scaleNs = 1_000_000;
        double? durationUnits = null;

        while (r.HasMore)
        {
            var (id, size, unknown) = r.ReadElement();
            if (id == EbmlHeader) { r.Skip(size); continue; }
            if (id != Segment) { r.Skip(unknown ? 0 : size); continue; }

            // Segment (size frequently unknown): its children run to the declared end or EOF.
            var segEnd = unknown ? bytes.Length : r.Position + size;
            while (r.Position < segEnd && r.HasMore)
            {
                var (cid, csize, cunknown) = r.ReadElement();
                switch (cid)
                {
                    case SegInfo:
                        ParseInfo(ref r, r.Position + csize, ref scaleNs, ref durationUnits);
                        break;
                    case TracksId:
                        ParseTracks(ref r, r.Position + csize, tracks);
                        break;
                    case Cluster:
                        ParseCluster(ref r, cunknown ? -1 : r.Position + csize, bytes, blocks, scaleNs);
                        break;
                    default:
                        r.Skip(cunknown ? 0 : csize);
                        break;
                }
            }
        }

        blocks.Sort(static (a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        double? duration = durationUnits is { } d ? d * scaleNs / 1e9 : null;
        return new WebmFile(tracks, blocks, scaleNs, duration);
    }

    private static void ParseInfo(ref Reader r, long end, ref long scaleNs, ref double? durationUnits)
    {
        while (r.Position < end)
        {
            var (id, size, _) = r.ReadElement();
            if (id == TimestampScaleId) scaleNs = (long)r.ReadUInt(size);
            else if (id == DurationId) durationUnits = r.ReadFloat(size);
            else r.Skip(size);
        }
    }

    private static void ParseTracks(ref Reader r, long end, List<WebmTrack> tracks)
    {
        while (r.Position < end)
        {
            var (id, size, _) = r.ReadElement();
            if (id != TrackEntry) { r.Skip(size); continue; }

            var entryEnd = r.Position + size;
            int number = 0, type = 0, width = 0, height = 0, channels = 0;
            double rate = 0, delayNs = 0;
            var codec = "";
            byte[]? priv = null;
            while (r.Position < entryEnd)
            {
                var (tid, tsize, _) = r.ReadElement();
                switch (tid)
                {
                    case TrackNumber: number = (int)r.ReadUInt(tsize); break;
                    case TrackType: type = (int)r.ReadUInt(tsize); break;
                    case CodecId: codec = r.ReadAscii(tsize); break;
                    case CodecPrivate: priv = r.ReadBytes(tsize); break;
                    case CodecDelay: delayNs = r.ReadUInt(tsize); break;
                    case VideoEl:
                    {
                        var vEnd = r.Position + tsize;
                        while (r.Position < vEnd)
                        {
                            var (vid, vsize, _) = r.ReadElement();
                            if (vid == PixelWidth) width = (int)r.ReadUInt(vsize);
                            else if (vid == PixelHeight) height = (int)r.ReadUInt(vsize);
                            else r.Skip(vsize);
                        }
                        break;
                    }
                    case AudioEl:
                    {
                        var aEnd = r.Position + tsize;
                        while (r.Position < aEnd)
                        {
                            var (aid, asize, _) = r.ReadElement();
                            if (aid == SamplingFrequency) rate = r.ReadFloat(asize);
                            else if (aid == ChannelsId) channels = (int)r.ReadUInt(asize);
                            else r.Skip(asize);
                        }
                        break;
                    }
                    default: r.Skip(tsize); break;
                }
            }
            tracks.Add(new WebmTrack(number, type is 1 or 2 ? (WebmTrackKind)type : WebmTrackKind.Other,
                codec, width, height, rate, channels, priv, delayNs / 1e9));
        }
    }

    // end == -1: unknown-size cluster (MediaRecorder) — runs until the next segment-level id or EOF.
    private static void ParseCluster(ref Reader r, long end, byte[] bytes, List<WebmBlock> blocks, long scaleNs)
    {
        long clusterTs = 0;
        while (end < 0 ? r.HasMore : r.Position < end)
        {
            if (end < 0 && IsSegmentLevel(r.PeekId())) return;
            var (id, size, _) = r.ReadElement();
            switch (id)
            {
                case ClusterTimestamp:
                    clusterTs = (long)r.ReadUInt(size);
                    break;
                case SimpleBlock:
                    ParseBlockPayload(ref r, size, bytes, blocks, clusterTs, scaleNs, keyframeFromFlags: true, forcedKeyframe: false);
                    break;
                case BlockGroup:
                {
                    // A Block is a keyframe iff its group carries no ReferenceBlock.
                    var gEnd = r.Position + size;
                    long blockPos = -1, blockSize = 0;
                    var hasReference = false;
                    while (r.Position < gEnd)
                    {
                        var (gid, gsize, _) = r.ReadElement();
                        if (gid == BlockId) { blockPos = r.Position; blockSize = gsize; r.Skip(gsize); }
                        else if (gid == ReferenceBlock) { hasReference = true; r.Skip(gsize); }
                        else r.Skip(gsize);
                    }
                    if (blockPos >= 0)
                    {
                        var save = r.Position;
                        r.Seek(blockPos);
                        ParseBlockPayload(ref r, blockSize, bytes, blocks, clusterTs, scaleNs, keyframeFromFlags: false, forcedKeyframe: !hasReference);
                        r.Seek(save);
                    }
                    break;
                }
                default:
                    r.Skip(size);
                    break;
            }
        }
    }

    private static void ParseBlockPayload(ref Reader r, long size, byte[] bytes, List<WebmBlock> blocks,
        long clusterTs, long scaleNs, bool keyframeFromFlags, bool forcedKeyframe)
    {
        var payloadEnd = r.Position + size;
        var track = (int)r.ReadVintValue();
        var rel = (short)((r.ReadByte() << 8) | r.ReadByte());
        var flags = r.ReadByte();
        var keyframe = keyframeFromFlags ? (flags & 0x80) != 0 : forcedKeyframe;
        var time = (clusterTs + rel) * scaleNs / 1e9;

        var lacing = (flags >> 1) & 0x3;   // 0 none · 1 Xiph · 2 fixed · 3 EBML
        if (lacing == 0)
        {
            blocks.Add(new WebmBlock(track, time, keyframe, bytes.AsMemory((int)r.Position, (int)(payloadEnd - r.Position))));
            r.Seek(payloadEnd);
            return;
        }

        // Laced: N frames in one block (audio commonly). All get the block's timestamp — decode
        // order is what matters downstream; sub-frame timing is reconstructed by the audio clock.
        var frameCount = r.ReadByte() + 1;
        var sizes = new long[frameCount];
        switch (lacing)
        {
            case 1: // Xiph: 255-run-length sizes for all but the last
                for (var i = 0; i < frameCount - 1; i++)
                {
                    long s = 0;
                    byte b;
                    do { b = r.ReadByte(); s += b; } while (b == 255);
                    sizes[i] = s;
                }
                break;
            case 2: // fixed: equal split of the remainder
            {
                var each = (payloadEnd - r.Position) / frameCount;
                for (var i = 0; i < frameCount; i++) sizes[i] = each;
                break;
            }
            case 3: // EBML: first absolute, then signed deltas
            {
                sizes[0] = (long)r.ReadVintValue();
                for (var i = 1; i < frameCount - 1; i++) sizes[i] = sizes[i - 1] + r.ReadSignedVint();
                break;
            }
        }
        // The last frame (Xiph/EBML) is the remainder after the others.
        if (lacing != 2)
        {
            long known = 0;
            for (var i = 0; i < frameCount - 1; i++) known += sizes[i];
            sizes[frameCount - 1] = payloadEnd - r.Position - known;
        }
        foreach (var s in sizes)
        {
            if (s < 0 || r.Position + s > payloadEnd) throw new FormatException("WebM: lacing sizes exceed the block.");
            blocks.Add(new WebmBlock(track, time, keyframe, bytes.AsMemory((int)r.Position, (int)s)));
            r.Skip(s);
        }
        r.Seek(payloadEnd);
    }

    // ---- the EBML byte reader ----------------------------------------------------------------
    private struct Reader(byte[] bytes)
    {
        private readonly byte[] _b = bytes;
        public long Position { get; private set; }

        public readonly bool HasMore => Position < _b.Length;
        public void Seek(long pos) => Position = pos;
        public void Skip(long n)
        {
            if (n < 0 || Position + n > _b.Length) throw new FormatException("WebM: element runs past the end of the file.");
            Position += n;
        }

        public byte ReadByte()
        {
            if (Position >= _b.Length) throw new FormatException("WebM: unexpected end of file.");
            return _b[Position++];
        }

        /// <summary>(id, size, sizeUnknown). Ids keep their marker bit; sizes strip it. A size of
        /// all value bits set means "unknown" (streamed Segments/Clusters).</summary>
        public (uint Id, long Size, bool Unknown) ReadElement()
        {
            var id = ReadId();
            var (size, unknown) = ReadSize();
            if (!unknown && Position + size > _b.Length) throw new FormatException("WebM: element size runs past the end of the file.");
            return (id, size, unknown);
        }

        public readonly uint PeekId()
        {
            var copy = this;
            try { return copy.ReadId(); }
            catch (FormatException) { return 0; }
        }

        private uint ReadId()
        {
            var first = ReadByte();
            var len = LengthOf(first);
            if (len is < 1 or > 4) throw new FormatException("WebM: invalid element id.");
            uint id = first;
            for (var i = 1; i < len; i++) id = (id << 8) | ReadByte();
            return id;
        }

        private (long Size, bool Unknown) ReadSize()
        {
            var first = ReadByte();
            var len = LengthOf(first);
            if (len is < 1 or > 8) throw new FormatException("WebM: invalid element size.");
            long value = first & (0xFF >> len); // strip the marker bit
            var allOnes = value == (0xFF >> len);
            for (var i = 1; i < len; i++)
            {
                var b = ReadByte();
                value = (value << 8) | b;
                allOnes &= b == 0xFF;
            }
            return allOnes ? (0, true) : (value, false);
        }

        /// <summary>A size-style VINT used inside block payloads (track number).</summary>
        public ulong ReadVintValue()
        {
            var first = ReadByte();
            var len = LengthOf(first);
            if (len is < 1 or > 8) throw new FormatException("WebM: invalid vint.");
            ulong value = (ulong)(first & (0xFF >> len));
            for (var i = 1; i < len; i++) value = (value << 8) | ReadByte();
            return value;
        }

        /// <summary>EBML-lacing signed vint: unsigned value minus the mid-range offset.</summary>
        public long ReadSignedVint()
        {
            var first = ReadByte();
            var len = LengthOf(first);
            if (len is < 1 or > 8) throw new FormatException("WebM: invalid signed vint.");
            long value = first & (0xFF >> len);
            for (var i = 1; i < len; i++) value = (value << 8) | ReadByte();
            return value - ((1L << (7 * len - 1)) - 1);
        }

        private static int LengthOf(byte first)
        {
            if (first == 0) return 9; // invalid — no marker bit in the first byte
            var len = 1;
            for (var mask = 0x80; (first & mask) == 0; mask >>= 1) len++;
            return len;
        }

        public ulong ReadUInt(long size)
        {
            if (size is < 0 or > 8) throw new FormatException("WebM: bad integer size.");
            ulong v = 0;
            for (var i = 0; i < size; i++) v = (v << 8) | ReadByte();
            return v;
        }

        public double ReadFloat(long size)
        {
            if (size == 4)
            {
                var raw = (uint)ReadUInt(4);
                return BitConverter.Int32BitsToSingle(unchecked((int)raw));
            }
            if (size == 8)
            {
                var raw = ReadUInt(8);
                return BitConverter.Int64BitsToDouble(unchecked((long)raw));
            }
            if (size == 0) return 0;
            throw new FormatException("WebM: bad float size.");
        }

        public string ReadAscii(long size)
        {
            var s = System.Text.Encoding.ASCII.GetString(_b, (int)Position, (int)size).TrimEnd('\0');
            Skip(size);
            return s;
        }

        public byte[] ReadBytes(long size)
        {
            var arr = new byte[size];
            Array.Copy(_b, Position, arr, 0, size);
            Skip(size);
            return arr;
        }
    }
}
