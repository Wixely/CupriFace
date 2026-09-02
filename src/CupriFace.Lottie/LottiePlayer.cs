using System.Diagnostics;
using CupriFace.Paint;
using SkiaSharp;
using SkiaSharp.Skottie;

namespace CupriFace.Lottie;

/// <summary>
/// One playing Lottie animation, published to the engine as a live surface.
///
/// <para>Frames are rendered at the animation's OWN size and the engine scales them to the element,
/// which is what makes <c>object-fit</c>, damage tracking and the render-on-demand cadence come for
/// free instead of being reinvented here. On a desktop host that is the same treatment a video frame
/// gets; on the web hosts it is NOT, because there a video is host-composited — the engine punches a
/// transparent hole and the browser decodes underneath — while a Lottie is still drawn by the engine
/// on every host. These pixels are the engine's everywhere.</para>
///
/// <para>The clock lives in the player. A surface has no per-frame callback to hang off, and adding
/// one would mean every host learning to pump animations; instead the frame is advanced when the
/// paint path asks for it, from a stopwatch, which is self-correcting if frames are skipped.</para>
/// </summary>
public sealed class LottiePlayer : ISurfaceSource, IDisposable
{
    private readonly Animation _animation;
    private readonly bool _loop;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _w, _h;
    private SKSurface? _surface;
    private SKImage? _frame;
    private SKImage? _retired;          // the frame the paint path may still be reading
    private double _time, _lastTick;
    private bool _disposed;

    private LottiePlayer(Animation animation, bool loop, bool autoplay)
    {
        _animation = animation;
        _loop = loop;
        Playing = autoplay;
        // Skottie reports a fractional size; a raster surface needs whole pixels. Floor-to-1 so a
        // degenerate file cannot ask for a zero-sized surface.
        _w = Math.Max(1, (int)MathF.Round(animation.Size.Width));
        _h = Math.Max(1, (int)MathF.Round(animation.Size.Height));
        _lastTick = 0;
    }

    /// <summary>Parse an animation, or null if the JSON is not one. Null rather than an exception
    /// because a bad asset is an authoring mistake in one element, and taking the whole document down
    /// for it would be the worse failure.</summary>
    public static LottiePlayer? TryCreate(byte[] json, bool loop = true, bool autoplay = true)
    {
        using var data = SKData.CreateCopy(json);
        return Animation.TryCreate(data, out var animation) && animation is not null
            ? new LottiePlayer(animation, loop, autoplay)
            : null;
    }

    public bool Playing { get; set; }

    /// <summary>Seconds of animation, from the file.</summary>
    public double Duration => _animation.Duration.TotalSeconds;

    /// <summary>Where playback currently sits, in seconds.</summary>
    public double Position => _time;

    // ---- ISurfaceSource ---------------------------------------------------

    /// <summary>The frame to draw, rendered on demand. Advancing here rather than on a timer is what
    /// keeps a paused animation free: nothing ticks, nothing repaints, and the last frame stays up.</summary>
    public SKImage? CurrentFrame
    {
        get
        {
            if (_disposed) return _frame;
            var now = _clock.Elapsed.TotalSeconds;
            if (Playing) Advance(now - _lastTick);
            else if (_frame is not null) { _lastTick = now; return _frame; }
            _lastTick = now;
            return _frame;
        }
    }

    public (int W, int H)? NaturalSize => (_w, _h);

    /// <summary>Only a PLAYING animation keeps a render-on-demand host awake. Paused, the last frame
    /// stays on screen and an idle window costs nothing again — the bargain video strikes too.</summary>
    public bool Ticking => Playing && !_disposed;

    // ---- driving ----------------------------------------------------------

    /// <summary>Advance by <paramref name="dt"/> seconds and re-render. Public so a test can drive it
    /// from a clock it controls rather than waiting on wall time.</summary>
    public bool Advance(double dt)
    {
        if (_disposed) return false;

        _time += Math.Max(0, dt);
        if (_time > Duration)
            _time = _loop && Duration > 0 ? _time % Duration : Duration;

        _surface ??= SKSurface.Create(new SKImageInfo(_w, _h, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (_surface is null) return false;

        // Transparent, not white: a Lottie draws OVER whatever the page put behind it, and a white
        // plate would show as a rectangle around every animation with a rounded or open design.
        _surface.Canvas.Clear(SKColors.Transparent);
        _animation.SeekFrameTime(_time);
        _animation.Render(_surface.Canvas, new SKRect(0, 0, _w, _h));

        // Swap first, dispose the PREVIOUS frame after — never free an image the paint path may still
        // be holding. One generation of lag is what the surface contract asks for.
        var previous = _frame;
        _frame = _surface.Snapshot();
        _retired?.Dispose();
        _retired = previous;
        return true;
    }

    /// <summary>Restart from the first frame.</summary>
    public void Rewind() { _time = 0; Advance(0); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _frame?.Dispose(); _retired?.Dispose(); _surface?.Dispose(); _animation.Dispose();
        _frame = _retired = null; _surface = null;
    }
}
