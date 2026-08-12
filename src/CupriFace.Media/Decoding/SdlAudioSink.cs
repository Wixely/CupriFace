using Silk.NET.SDL;

namespace CupriFace.Media.Decoding;

/// <summary>
/// PCM out through SDL's audio queue — the managed Silk.NET binding the desktop shell already
/// ships, so audio adds ZERO new native dependencies. Queue mode (no callback): the player
/// pushes float frames slightly ahead; volume/mute are applied by scaling before queueing
/// (SDL queues have no gain). No device (headless/CI) → <see cref="TryCreate"/> returns null
/// and video plays silently.
/// </summary>
public sealed unsafe class SdlAudioSink : IAudioSink
{
    private const ushort AudioF32Sys = 0x8120;   // 32-bit float, native endian

    private readonly Sdl _sdl;
    private uint _device;
    private int _channels = 2;
    private int _rate = 48000;
    private double _volume = 1;
    private float[] _scaled = [];

    private SdlAudioSink(Sdl sdl) => _sdl = sdl;

    /// <summary>A sink, or null when this machine can't do audio — playback stays silent, video
    /// unaffected. Never throws.</summary>
    public static SdlAudioSink? TryCreate()
    {
        try
        {
            var sdl = Sdl.GetApi();
            if (sdl.InitSubSystem(Sdl.InitAudio) != 0) return null;
            return new SdlAudioSink(sdl);
        }
        catch
        {
            return null;
        }
    }

    public void Start(int sampleRate, int channels)
    {
        Stop();
        _rate = sampleRate;
        _channels = channels;
        var desired = new AudioSpec
        {
            Freq = sampleRate,
            Format = AudioF32Sys,
            Channels = (byte)channels,
            Samples = 1024,
            Callback = default,   // queue mode
        };
        AudioSpec obtained;
        _device = _sdl.OpenAudioDevice((byte*)null, 0, &desired, &obtained, 0);
        // _device 0 = no device; every later call checks, so a deaf machine just plays silent.
    }

    public void Submit(ReadOnlySpan<float> interleavedPcm)
    {
        if (_device == 0 || interleavedPcm.IsEmpty) return;
        var gain = (float)_volume;
        if (_scaled.Length < interleavedPcm.Length) _scaled = new float[interleavedPcm.Length];
        for (var i = 0; i < interleavedPcm.Length; i++) _scaled[i] = interleavedPcm[i] * gain;
        fixed (float* p = _scaled)
            _sdl.QueueAudio(_device, p, (uint)(interleavedPcm.Length * sizeof(float)));
    }

    public double QueuedSeconds =>
        _device == 0 ? 0 : _sdl.GetQueuedAudioSize(_device) / (double)(sizeof(float) * _channels * _rate);

    public void Pause(bool paused)
    {
        if (_device != 0) _sdl.PauseAudioDevice(_device, paused ? 1 : 0);
    }

    public void Flush()
    {
        if (_device != 0) _sdl.ClearQueuedAudio(_device);
    }

    public double Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 1);
    }

    private void Stop()
    {
        if (_device == 0) return;
        _sdl.CloseAudioDevice(_device);
        _device = 0;
    }

    public void Dispose() => Stop();
}
