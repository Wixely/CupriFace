using CupriFace.Media.Webm;
using SkiaSharp;

namespace CupriFace.Media.Decoding;

/// <summary>
/// The seam between the managed player and the native codecs — and the reason the player is
/// fully testable without them (tests inject fakes; the native libvpx/libopus implementations
/// are this package's ONLY native surface).
/// </summary>
public interface IMediaDecoderFactory
{
    /// <summary>A decoder for the track's codec, or null when unsupported (the player then
    /// ignores that track — a video with an exotic audio codec still shows its picture).</summary>
    IVideoFrameDecoder? CreateVideo(WebmTrack track);

    IAudioDecoder? CreateAudio(WebmTrack track);
}

/// <summary>Decodes demuxed video packets to frames. Called only from the player's decode
/// thread; implementations need no thread safety of their own.</summary>
public interface IVideoFrameDecoder : IDisposable
{
    /// <summary>Decode one packet. Null when the packet produced no displayable frame (codec
    /// lag / alt-ref). The returned image is OWNED BY THE CALLER (the player retires it).</summary>
    SKImage? Decode(ReadOnlySpan<byte> packet, bool keyframe);
}

/// <summary>Decodes demuxed audio packets to interleaved float PCM (player thread only).</summary>
public interface IAudioDecoder : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    ReadOnlyMemory<float> Decode(ReadOnlySpan<byte> packet);
}

/// <summary>Where PCM goes — the host side of audio (desktop: SDL via the already-shipped
/// managed Silk.NET binding; absent/failed device → the player plays video silently).</summary>
public interface IAudioSink : IDisposable
{
    void Start(int sampleRate, int channels);
    void Submit(ReadOnlySpan<float> interleavedPcm);
    /// <summary>Seconds of submitted-but-unplayed audio (the audio-master clock reads this).</summary>
    double QueuedSeconds { get; }
    /// <summary>False when <see cref="Start"/> found no usable output (headless box, RDP session
    /// with no endpoint): the sink swallows audio silently and its queue reads 0 by definition —
    /// timing metrics (lag, underruns) are meaningless and must not be computed. Default true.</summary>
    bool DeviceOpen => true;
    void Pause(bool paused);
    /// <summary>Drop queued audio (seek).</summary>
    void Flush();
    /// <summary>0..1 (the player folds mute into this).</summary>
    double Volume { get; set; }
}
