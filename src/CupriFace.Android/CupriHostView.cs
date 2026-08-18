using Android.Content;
using Android.Opengl;
using Android.Text;
using Android.Views;
using Android.Views.Autofill;
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
        host.SelectionChanged += OnSelectionChanged;
        host.TextInputChanged += NotifyAutofillFocus;
        host.FormSubmitted += CommitAutofill;

        // WHEN_DIRTY parks the GL thread between frames; RequestRender wakes it. Everything the
        // engine's render-on-demand model needs — Dispatch* returns and HasActiveAnimations —
        // maps onto exactly this. (Must be set after the base ctor installs its renderer.)
        RenderMode = Rendermode.WhenDirty;

        // Keep the EGL context across pause when the device allows: without this, every
        // background/foreground round-trip destroys the context under SkiaSharp's GRContext and
        // the first real device came back to a permanently blank surface. Where the OS still
        // reclaims it, SurfaceCreated fires and the retained-frame invalidation covers the rest.
        PreserveEGLContextOnPause = true;

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

    // ---- autofill -------------------------------------------------------------------------------

    private AutofillBridge? _autofill;

    /// <summary>Describe our fields to an autofill service. Android sees one opaque GL surface, so
    /// without a virtual structure a password manager has nothing to fill — the same reason
    /// TalkBack needs a node provider.</summary>
    public override void OnProvideAutofillVirtualStructure(ViewStructure? structure, AutofillFlags flags)
    {
        base.OnProvideAutofillVirtualStructure(structure, flags);
        if (structure is null) return;
        (_autofill ??= new AutofillBridge(this, _host)).ProvideStructure(structure);
    }

    /// <summary>A service filled something. Values land through the ordinary binding.</summary>
    public override void Autofill(global::Android.Util.SparseArray values)
    {
        (_autofill ??= new AutofillBridge(this, _host)).Fill(values);
    }

    /// <summary>The app finished a form: ask the autofill service whether it wants to save what
    /// was entered. Without this a password manager fills happily and never offers to remember
    /// anything, because nothing ever told it the entry was complete.</summary>
    private void CommitAutofill()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        if (Context?.GetSystemService(global::Java.Lang.Class.FromType(typeof(AutofillManager)))
                is AutofillManager manager) manager.Commit();
    }

    /// <summary>Autofill needs to know WHICH field was entered before it offers anything.
    ///
    /// This used to call the view-level <c>NotifyViewEntered(this)</c> — but we publish a VIRTUAL
    /// structure, and a virtual field's session must be announced with the virtual-id overload.
    /// Without the id the framework never learns that an autofillable node has focus, so no fill
    /// session starts: no dropdown, no keyboard-inline chip (a password manager's button in the
    /// IME strip), and nothing for <c>Commit()</c> to save. A device with Vaultwarden showed the
    /// exact symptom pair — no chip, no save prompt — while filling-by-structure worked.</summary>
    private int _autofillFocusedId;                     // 0 = nothing announced
    private string? _autofillLastValue;

    private void NotifyAutofillFocus(TextInputState state)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        if (Context?.GetSystemService(global::Java.Lang.Class.FromType(typeof(AutofillManager)))
                is not AutofillManager manager) return;

        var bridge = _autofill ??= new AutofillBridge(this, _host);
        var target = state.Focused ? bridge.FocusedField() : null;
        if (target is { } t)
        {
            // The framework wants the field's bounds in SCREEN coordinates — the bridge computed
            // view pixels, so add where this view sits.
            var loc = new int[2];
            GetLocationOnScreen(loc);
            t.Bounds.Offset(loc[0], loc[1]);
            if (_autofillFocusedId != 0 && _autofillFocusedId != t.Id)
                manager.NotifyViewExited(this, _autofillFocusedId);
            _autofillFocusedId = t.Id;
            _autofillLastValue = state.Value;
            manager.NotifyViewEntered(this, t.Id, t.Bounds);
        }
        else if (_autofillFocusedId != 0)
        {
            manager.NotifyViewExited(this, _autofillFocusedId);
            _autofillFocusedId = 0;
            _autofillLastValue = null;
        }
    }

    /// <summary>Typing must reach the autofill session too: save-on-submit is decided from the
    /// value changes the framework has SEEN, so a session that never hears the password being
    /// typed has nothing it considers worth saving.</summary>
    private void NotifyAutofillValue(TextInputState state)
    {
        if (_autofillFocusedId == 0 || !OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        if (state.Value == _autofillLastValue) return;
        _autofillLastValue = state.Value;
        if (Context?.GetSystemService(global::Java.Lang.Class.FromType(typeof(AutofillManager)))
                is AutofillManager manager)
            manager.NotifyValueChanged(this, _autofillFocusedId, AutofillValue.ForText(state.Value ?? ""));
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

    /// <summary>The half of the IME contract an editor owes the keyboard: where the caret is. A
    /// keyboard that has never been told cannot move the cursor, offer a correction range, or place
    /// its candidate window — it is guessing about an editor it cannot see.</summary>
    private void OnSelectionChanged(TextInputState state)
    {
        // Typing rides the same event as caret movement — the autofill session hears every value
        // change here, which is what save-on-submit is judged from.
        NotifyAutofillValue(state);

        if (Context?.GetSystemService(Context.InputMethodService) is not InputMethodManager imm) return;
        var (compStart, compEnd) = state.Composing ? (state.SelStart, state.SelEnd) : (-1, -1);
        imm.UpdateSelection(this, state.SelStart, state.SelEnd, compStart, compEnd);

        // An IME rendering its own copy of the editor (extract mode) asked to be TOLD about
        // changes rather than to poll for them. Without this its copy freezes at the text that
        // existed when it opened.
        if (_extractToken is { } token)
        {
            var text = state.Value ?? "";
            imm.UpdateExtractedText(this, token, new ExtractedText
            {
                Text = new Java.Lang.String(text),
                StartOffset = 0,
                SelectionStart = Math.Clamp(state.SelStart, 0, text.Length),
                SelectionEnd = Math.Clamp(state.SelEnd, 0, text.Length),
                PartialStartOffset = -1,
                PartialEndOffset = -1,
                Flags = state.Multiline ? 0 : ExtractedTextFlags.SingleLine,
            });
        }
    }

    private int? _extractToken;

    /// <summary>An IME asked to monitor the extracted text; remember who to tell.</summary>
    internal void StartExtractMonitoring(int token) => _extractToken = token;

    public override bool OnCheckIsTextEditor() => _host.ImeState.Focused;

    public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
    {
        // ImeState, not the painted snapshot: this is asked the instant focus moves, before the
        // frame showing that focus exists. The snapshot would describe the previous field.
        var state = _host.ImeState;
        if (!state.Focused) return null;

        if (outAttrs is not null)
        {
            // The field-kind attributes the components already emit map straight onto EditorInfo.
            // inputmode (the web platform's attribute) refines the keyboard the user gets: an
            // email field earns an @ key, a tel field a dial pad. The data-* kinds still win,
            // because they change what the field ACCEPTS, not merely what is convenient to type.
            outAttrs.InputType = state switch
            {
                { Numeric: true } => InputTypes.ClassNumber | InputTypes.NumberFlagDecimal | InputTypes.NumberFlagSigned,
                { Masked: true } => InputTypes.ClassText | InputTypes.TextVariationPassword | InputTypes.TextFlagNoSuggestions,
                { InputMode: "email" } => InputTypes.ClassText | InputTypes.TextVariationEmailAddress,
                { InputMode: "url" } => InputTypes.ClassText | InputTypes.TextVariationUri,
                { InputMode: "tel" } => InputTypes.ClassPhone,
                { InputMode: "numeric" } => InputTypes.ClassNumber,
                { InputMode: "decimal" } => InputTypes.ClassNumber | InputTypes.NumberFlagDecimal,
                { InputMode: "search", Multiline: false } => InputTypes.ClassText,
                { Multiline: true } => InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences,
                _ => InputTypes.ClassText | InputTypes.TextFlagCapSentences,
            };
            // Extract mode is supported now (GetExtractedText + UpdateExtractedText), so the
            // suppression flags are gone and a landscape keyboard may render its own editor.
            var action = state.EnterKeyHint switch
            {
                "go" => ImeAction.Go,
                "next" => ImeAction.Next,
                "previous" => ImeAction.Previous,
                "search" => ImeAction.Search,
                "send" => ImeAction.Send,
                "done" => ImeAction.Done,
                "enter" => ImeAction.None,
                _ => state.Multiline ? ImeAction.None : ImeAction.Done,
            };
            outAttrs.ImeOptions = state.Multiline && action == ImeAction.None
                ? ImeFlags.NoEnterAction
                : (ImeFlags)action;
            if (state.Placeholder is { Length: > 0 } hint)
                outAttrs.HintText = new Java.Lang.String(hint);   // shown when the app is not
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
        var t = e.EventTime / 1000.0;
        var action = e.ActionMasked;

        // EVERY pointer, not just the first. `GetX()`/`GetY()` read pointer 0, which is why a second
        // finger used to be invisible to the whole stack. ActionPointerDown/Up carry the index of
        // the pointer that changed; a Move carries new positions for all of them at once.
        switch (action)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
            {
                var index = e.ActionIndex;
                var id = e.GetPointerId(index);
                var (px, py) = (e.GetX(index), e.GetY(index));
                QueueEvent(() => _host.PointerDown(id, px, py, t));
                if (action == MotionEventActions.Down)
                    // Long-press: arm a UI-thread timer that queues Tick past the deadline. A tick
                    // that arrives after the press resolved is a no-op by design, so no bookkeeping.
                    Handler?.PostDelayed(() =>
                        QueueEvent(() => _host.TouchTick(global::Android.OS.SystemClock.UptimeMillis() / 1000.0)), 520);
                return true;
            }
            case MotionEventActions.Move:
            {
                var moves = new (int Id, float X, float Y)[e.PointerCount];
                for (var i = 0; i < e.PointerCount; i++)
                    moves[i] = (e.GetPointerId(i), e.GetX(i), e.GetY(i));
                QueueEvent(() => { foreach (var (id, mx, my) in moves) _host.PointerMove(id, mx, my, t); });
                return true;
            }
            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
            {
                var index = e.ActionIndex;
                var id = e.GetPointerId(index);
                var (px, py) = (e.GetX(index), e.GetY(index));
                QueueEvent(() => _host.PointerUp(id, px, py, t));
                return true;
            }
            case MotionEventActions.Cancel: QueueEvent(() => _host.TouchCancel(t)); return true;
            default: return false;
        }
    }
}
