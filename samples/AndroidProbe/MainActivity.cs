using Android.Content.PM;
using Android.Runtime;
using Android.Util;
using Android.Views;
using CupriFace;
using CupriFace.Demo;
using SkiaSharp;
using SkiaSharp.Views.Android;

namespace CupriFace.AndroidProbe;

/// <summary>
/// The whole Android "host", such as it is: an Activity, a GL surface, and touch forwarding.
///
/// The point of the probe is how SHORT this is. The engine has no windowing dependency, so a
/// platform needs to supply exactly three things — a surface to draw on, input events, and (later)
/// an accessibility bridge. Everything below the <c>CreateDocument()</c> call is the same code the
/// desktop and web hosts already run.
///
/// Timings and sizes go to logcat under the tag "cupri", which is what the probe reads back.
/// </summary>
[Activity(Label = "CupriFace probe", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : Activity
{
    private const string Tag = "cupri";

    // From process start, not OnCreate: the interesting number includes the runtime and the
    // native loads, which is exactly what a user waiting at a splash screen experiences.
    private static readonly System.Diagnostics.Stopwatch Clock =
        System.Diagnostics.Stopwatch.StartNew();

    private CupriApp? _app;
    private CupriDocument? _doc;
    private SKGLSurfaceView? _view;
    private float _density = 1f;
    private int _frames;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _density = Resources?.DisplayMetrics?.Density ?? 1f;

        // Everything here is logged rather than allowed to crash the process. The first run on a
        // device died with a bare "NullReferenceException in OnCreate" and no more, which is the
        // least useful possible answer — a probe that cannot name what failed has not probed.
        var t0 = Clock.Elapsed.TotalMilliseconds;
        try
        {
            _app = new ShowcaseApp();
            Log.Info(Tag, $"app constructed at {Clock.Elapsed.TotalMilliseconds:F0} ms");
            _doc = _app.CreateDocument();      // the SAME call the desktop and web hosts make
            Log.Info(Tag, $"document built in {Clock.Elapsed.TotalMilliseconds - t0:F0} ms " +
                          $"(density {_density}, {Clock.Elapsed.TotalMilliseconds:F0} ms since start)");
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"BUILDING THE DOCUMENT FAILED: {ex}");
            return;                            // leave the Activity up so logcat can be read
        }

        _view = new SKGLSurfaceView(this);
        _view.PaintSurface += OnPaintSurface;
        _view.Touch += OnTouch;
        SetContentView(_view);
    }

    private void OnPaintSurface(object? sender, SKPaintGLSurfaceEventArgs e)
    {
        if (_doc is null || _app is null || _view is null) return;

        // Android gives us PHYSICAL pixels; the engine works in logical ones, so density is the
        // whole conversion — the same job DPI scaling does on desktop.
        var w = _view.Width / _density;
        var h = _view.Height / _density;
        var p = _app.Present(w, h);

        var canvas = e.Surface.Canvas;
        canvas.Clear(_app.Background);
        canvas.Save();
        canvas.Scale(_density * p.Scale);
        _doc.Render(canvas, p.LogicalWidth, p.LogicalHeight);
        canvas.Restore();

        if (++_frames == 1)
            Log.Info(Tag, $"FIRST FRAME at {Clock.Elapsed.TotalMilliseconds:F0} ms since start " +
                          $"({_view.Width}x{_view.Height} px, {p.LogicalWidth:F0}x{p.LogicalHeight:F0} logical)");
        else if (_frames == 60)
            Log.Info(Tag, $"60 frames by {Clock.Elapsed.TotalMilliseconds:F0} ms since start");
    }

    /// <summary>Touch is just a pointer as far as the engine is concerned — which is the easy half.
    /// Fling, momentum scrolling and pinch do not exist yet, and that is the real mobile work.</summary>
    private void OnTouch(object? sender, View.TouchEventArgs e)
    {
        if (_doc is null || e.Event is not { } ev) return;
        var x = ev.GetX() / _density;
        var y = ev.GetY() / _density;

        var changed = ev.Action switch
        {
            MotionEventActions.Down => _doc.DispatchClick(x, y),
            MotionEventActions.Move => _doc.DispatchPointerMove(x, y),
            MotionEventActions.Up or MotionEventActions.Cancel => _doc.DispatchPointerUp(x, y),
            _ => false,
        };
        if (changed) _view?.Invalidate();
        e.Handled = true;
    }

    protected override void OnDestroy()
    {
        _doc?.Dispose();
        base.OnDestroy();
    }
}
