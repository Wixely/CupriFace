using CupriFace.Media.Webm;

namespace CupriFace.Media.Decoding;

/// <summary>
/// Opus decode via libopus (inside <c>cupricodecs</c>). WebM Opus always plays out at 48 kHz;
/// channel count comes from the track. The returned PCM is a view over a reused buffer — valid
/// until the next <see cref="Decode"/>, which matches the player's submit-immediately pump.
/// Honors the track's CodecDelay (pre-skip) by trimming the first samples.
/// </summary>
internal sealed unsafe class OpusAudioDecoder : IAudioDecoder
{
    private const int Rate = 48000;
    private const int MaxFrame = 5760;   // 120 ms at 48 kHz, the Opus maximum

    private nint _decoder;
    private readonly float[] _pcm;
    private int _skipSamples;            // per-channel samples to trim (CodecDelay / pre-skip)

    public int SampleRate => Rate;
    public int Channels { get; }

    public OpusAudioDecoder(WebmTrack track)
    {
        Channels = track.Channels is 1 or 2 ? track.Channels : 2;
        _decoder = Vpx.opus_decoder_create(Rate, Channels, out var err);
        if (err != 0 || _decoder == 0)
            throw new InvalidOperationException($"libopus decoder init failed ({err}).");
        _pcm = new float[MaxFrame * Channels];
        _skipSamples = (int)(track.CodecDelaySeconds * Rate);
    }

    public ReadOnlyMemory<float> Decode(ReadOnlySpan<byte> packet)
    {
        if (_decoder == 0 || packet.IsEmpty) return ReadOnlyMemory<float>.Empty;
        int samples;
        fixed (byte* data = packet)
        fixed (float* pcm = _pcm)
            samples = Vpx.opus_decode_float(_decoder, data, packet.Length, pcm, MaxFrame, 0);
        if (samples <= 0) return ReadOnlyMemory<float>.Empty;   // corrupt packet: skip, don't kill

        var start = 0;
        if (_skipSamples > 0)
        {
            var skip = Math.Min(_skipSamples, samples);
            _skipSamples -= skip;
            start = skip * Channels;
            if (start >= samples * Channels) return ReadOnlyMemory<float>.Empty;
        }
        return _pcm.AsMemory(start, samples * Channels - start);
    }

    public void Dispose()
    {
        if (_decoder == 0) return;
        Vpx.opus_decoder_destroy(_decoder);
        _decoder = 0;
    }
}
