using Android.Content;
using Android.Opengl;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using CupriFace.Interaction;
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

        // The IME only attaches to a focused, focusable view.
        Focusable = true;
        FocusableInTouchMode = true;

        // Focus edges from the engine: show/hide the soft keyboard, and restart the connection
        // when the field KIND changes (numeric -> text swaps the keyboard layout).
        host.TextInputChanged += OnTextInputChanged;

        // WHEN_DIRTY parks the GL thread between frames; RequestRender wakes it. Everything the
        // engine's render-on-demand model needs — Dispatch* returns and HasActiveAnimations —
        // maps onto exactly this. (Must be set after the base ctor installs its renderer.)
        RenderMode = Rendermode.WhenDirty;

        PaintSurface += (_, e) =>
            _host.PaintFrame(e.Surface.Canvas, e.BackendRenderTarget.Width, e.BackendRenderTarget.Height, _density);
    }

    /// <summary>Publish the view's SCREEN-SPACE geometry whenever layout places it — the status
    /// bar sits above this view, so screen taps computed from the display size miss the app.
    /// The CI gate parses this line and computes its taps in VIEW space instead.</summary>
    protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
    {
        base.OnLayout(changed, left, top, right, bottom);
        var loc = new int[2];
        GetLocationOnScreen(loc);
        _talkBack?.SetViewOrigin(loc[0], loc[1]);        // a11y bounds are SCREEN rects
        global::Android.Util.Log.Info(AndroidHost.Tag,
            $"view origin {loc[0]},{loc[1]} size {Width}x{Height} density {_density}");
    }

    // ---- TalkBack -----------------------------------------------------------------------------

    private TalkBackBridge? _talkBack;
    private bool _talkBackKilled;

    /// <summary>The framework reads this when an accessibility client (TalkBack, uiautomator)
    /// inspects the view — the moment the virtual hierarchy comes alive. Lazy so the bridge (and
    /// its per-frame publish) costs nothing until a client actually exists.</summary>
    public override global::Android.Views.Accessibility.AccessibilityNodeProvider? AccessibilityNodeProvider
    {
        get
        {
            if (_talkBack is null && !_talkBackKilled)
            {
                _talkBack = TalkBackBridge.Create(this, _host);
                if (_talkBack is { } tb)
                {
                    var loc = new int[2];
                    GetLocationOnScreen(loc);
                    tb.SetViewOrigin(loc[0], loc[1]);
                    _host.AttachTalkBack(tb);
                }
                else _talkBackKilled = true;             // the sysprop kill switch said no
            }
            return _talkBack;
        }
    }

    /// <summary>Explore-by-touch: while TalkBack is on, finger movement arrives as HOVER events;
    /// the bridge maps them to virtual nodes so TalkBack's focus follows the finger.</summary>
    protected override bool DispatchHoverEvent(MotionEvent? e) =>
        (_talkBack?.OnHover(e) ?? false) || base.DispatchHoverEvent(e!);

    /// <summary>Surface (re)created — first show, or EGL loss on background/foreground, which is
    /// ROUTINE on Android. The host must drop any retained-frame assumption.</summary>
    public override void SurfaceCreated(ISurfaceHolder? holder)
    {
        base.SurfaceCreated(holder!);
        _host.OnSurfaceRecreated();
    }

    // ---- IME ----------------------------------------------------------------------------------

    private bool _keyboardShown;
    private (bool Numeric, bool Multiline, bool Masked) _lastKind;

    private void OnTextInputChanged(TextInputState state)
    {
        var imm = Context?.GetSystemService(Context.InputMethodService) as InputMethodManager;
        if (imm is null) return;
        if (state.Focused)
        {
            RequestFocus();
            var kind = (state.Numeric, state.Multiline, state.Masked);
            if (_keyboardShown && kind != _lastKind) imm.RestartInput(this);
            _lastKind = kind;
            imm.ShowSoftInput(this, ShowFlags.Implicit);
            _keyboardShown = true;
        }
        else if (_keyboardShown)
        {
            imm.HideSoftInputFromWindow(WindowToken, HideSoftInputFlags.None);
            _keyboardShown = false;
        }
    }

    public override bool OnCheckIsTextEditor() => _host.Current?.TextInput.Focused == true;

    public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
    {
        var state = _host.Current?.TextInput ?? default;
        if (!state.Focused) return null;

        if (outAttrs is not null)
        {
            // The field-kind attributes the components already emit map straight onto EditorInfo.
            outAttrs.InputType = state switch
            {
                { Numeric: true } => InputTypes.ClassNumber | InputTypes.NumberFlagDecimal | InputTypes.NumberFlagSigned,
                { Masked: true } => InputTypes.ClassText | InputTypes.TextVariationPassword | InputTypes.TextFlagNoSuggestions,
                { Multiline: true } => InputTypes.ClassText | InputTypes.TextFlagMultiLine,
                _ => InputTypes.ClassText,
            };
            outAttrs.ImeOptions = state.Multiline ? ImeFlags.NoEnterAction : (ImeFlags)ImeAction.Done;
            outAttrs.InitialSelStart = state.SelStart;
            outAttrs.InitialSelEnd = state.SelEnd;
        }
        return new CupriInputConnection(this, _host);
    }

    // ---- hardware keyboard --------------------------------------------------------------------

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (e is null) return base.OnKeyDown(keyCode, e);
        var shift = e.IsShiftPressed;
        var mods = (shift ? KeyMods.Shift : KeyMods.None) | (e.IsCtrlPressed ? KeyMods.Ctrl : KeyMods.None);

        if (e.IsCtrlPressed)
        {
            var ch = (char)e.GetUnicodeChar(MetaKeyStates.None);   // the base character, ignoring ctrl
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
            {
                QueueEvent(() => _host.HwShortcut(ch, shift));
                return true;
            }
        }

        var key = keyCode switch
        {
            Keycode.Del => EditKey.Backspace,
            Keycode.ForwardDel => EditKey.Delete,
            Keycode.DpadLeft => EditKey.Left,
            Keycode.DpadRight => EditKey.Right,
            Keycode.DpadUp => EditKey.Up,
            Keycode.DpadDown => EditKey.Down,
            Keycode.MoveHome => EditKey.Home,
            Keycode.MoveEnd => EditKey.End,
            Keycode.Enter or Keycode.NumpadEnter => EditKey.Enter,
            Keycode.Tab => shift ? EditKey.ShiftTab : EditKey.Tab,
            Keycode.Escape => EditKey.Escape,
            _ => EditKey.None,
        };
        if (key != EditKey.None)
        {
            QueueEvent(() => _host.Key(key, mods));
            return true;
        }

        var unicode = e.UnicodeChar;
        if (unicode > 0 && !char.IsControl((char)unicode))
        {
            var text = char.ConvertFromUtf32(unicode);
            QueueEvent(() => _host.KeyText(text));
            return true;
        }
        return base.OnKeyDown(keyCode, e);
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
