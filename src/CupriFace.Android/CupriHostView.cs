using Android.Content;
using Android.Opengl;
using Android.Views;
using SkiaSharp.Views.Android;

namespace CupriFace.Android;

/// <summary>
/// The surface: an <see cref="SKGLSurfaceView"/> (GL ES + GRContext — the same GPU model as the
/// desktop GL window) in render-on-demand mode. The view stays thin by design: it forwards touch
/// to the host's GL-thread queue and delegates every frame to <see cref="AndroidHost.PaintFrame"/>.
/// Later phases grow it by exactly two overrides: <c>OnCreateInputConnection</c> (IME, Phase 4)
/// and the accessibility node provider (TalkBack, Phase 8).
/// </summary>
public sealed class CupriHostView : SKGLSurfaceView
{
    private readonly AndroidHost _host;
    private readonly float _density;

    public CupriHostView(Context context, AndroidHost host) : base(context)
    {
        _host = host;
        _density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        host.Attach(this);

        // WHEN_DIRTY parks the GL thread between frames; RequestRender wakes it. Everything the
        // engine's render-on-demand model needs — Dispatch* returns and HasActiveAnimations —
        // maps onto exactly this. (Must be set after the base ctor installs its renderer.)
        RenderMode = Rendermode.WhenDirty;

        PaintSurface += (_, e) =>
            _host.PaintFrame(e.Surface.Canvas, e.BackendRenderTarget.Width, e.BackendRenderTarget.Height, _density);
    }

    /// <summary>Surface (re)created — first show, or EGL loss on background/foreground, which is
    /// ROUTINE on Android. The host must drop any retained-frame assumption.</summary>
    public override void SurfaceCreated(ISurfaceHolder? holder)
    {
        base.SurfaceCreated(holder!);
        _host.OnSurfaceRecreated();
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;

        // Capture on the UI thread, dispatch on the GL thread — MotionEvent objects are recycled
        // by the platform after this method returns, so the values must be copied out now. The
        // timestamp is the event's own uptime clock (the clock the gesture recognizer keys slop,
        // double-tap and fling velocity from), not "now".
        var x = e.GetX();
        var y = e.GetY();
        var t = e.EventTime / 1000.0;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                QueueEvent(() => _host.TouchDown(x, y, t));
                // Long-press: arm a UI-thread timer that queues Tick past the deadline. A tick
                // that arrives after the press resolved is a no-op by design, so no bookkeeping.
                Handler?.PostDelayed(() =>
                    QueueEvent(() => _host.TouchTick(global::Android.OS.SystemClock.UptimeMillis() / 1000.0)), 520);
                return true;
            case MotionEventActions.Move: QueueEvent(() => _host.TouchMove(x, y, t)); return true;
            case MotionEventActions.Up: QueueEvent(() => _host.TouchUp(x, y, t)); return true;
            case MotionEventActions.Cancel: QueueEvent(() => _host.TouchCancel(t)); return true;
            default: return false;
        }
    }
}
