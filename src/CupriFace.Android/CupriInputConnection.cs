using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using CupriFace.Interaction;
using Java.Lang;

namespace CupriFace.Android;

/// <summary>
/// The IME's view of the focused CupriFace field. Android's input methods speak
/// composition-first — <c>SetComposingText</c> streams the preedit, <c>CommitText</c>/
/// <c>FinishComposingText</c> land it — which maps one-to-one onto the engine's composition API.
///
/// Threading is the whole trick here: the IME calls this on the UI thread and expects SYNCHRONOUS
/// answers to its questions (text around the cursor), while every mutation must run on the GL
/// (document) thread. Reads answer from the host's immutable post-frame <c>TextInputState</c>
/// snapshot; writes queue. The snapshot can be one frame stale, which the InputConnection contract
/// tolerates — IMEs re-query after every edit they make.
/// </summary>
internal sealed class CupriInputConnection(CupriHostView view, AndroidHost host)
    : BaseInputConnection(view, fullEditor: true)
{
    private TextInputState State => host.ImeState;

    // ---- mutations (queued to the document thread) --------------------------------------------

    public override bool SetComposingText(ICharSequence? text, int newCursorPosition)
    {
        var s = text?.ToString() ?? "";
        host.OnGlThread(() => host.Ime(d => d.SetComposition(s)));
        return true;
    }

    public override bool FinishComposingText()
    {
        host.OnGlThread(() => host.Ime(d => d.CommitComposition()));
        return true;
    }

    public override bool CommitText(ICharSequence? text, int newCursorPosition)
    {
        // CommitComposition(final) is both halves: replaces an in-flight preedit with the IME's
        // final text, or inserts as ordinary typing when no composition is open. ImeCommitted also
        // logs the commit marker the CI gate asserts on.
        var s = text?.ToString() ?? "";
        host.OnGlThread(() => host.ImeCommitted(s));
        return true;
    }

    public override bool DeleteSurroundingText(int beforeLength, int afterLength)
    {
        // The engine's Backspace/Delete step by CODE POINT (Phase 3), which is also the honest
        // reading of this API for the text a user sees; IMEs asking in UTF-16 units about emoji
        // get whole-glyph deletion, which is what every real keyboard wants.
        host.OnGlThread(() => host.Ime(d =>
        {
            var changed = false;
            for (var i = 0; i < beforeLength; i++) changed |= d.DispatchKey(null, EditKey.Backspace);
            for (var i = 0; i < afterLength; i++) changed |= d.DispatchKey(null, EditKey.Delete);
            return changed;
        }));
        return true;
    }

    public override bool PerformEditorAction(ImeAction actionCode)
    {
        host.OnGlThread(() => host.Ime(d => d.DispatchKey(null, EditKey.Enter)));
        return true;
    }

    public override bool SendKeyEvent(KeyEvent? e)
    {
        // Some IMEs deliver deletes and Enter as raw key events rather than the calls above.
        if (e is { Action: KeyEventActions.Down })
        {
            // Navigation as well as editing. Some keyboards move the caret by synthesising arrow
            // keys rather than calling SetSelection — dropping these is why a soft keyboard could
            // not move the cursor at all, while a hardware one could (that path is in the VIEW).
            var key = e.KeyCode switch
            {
                Keycode.Del => EditKey.Backspace,
                Keycode.ForwardDel => EditKey.Delete,
                Keycode.Enter or Keycode.NumpadEnter => EditKey.Enter,
                Keycode.DpadLeft => EditKey.Left,
                Keycode.DpadRight => EditKey.Right,
                Keycode.DpadUp => EditKey.Up,
                Keycode.DpadDown => EditKey.Down,
                Keycode.MoveHome => EditKey.Home,
                Keycode.MoveEnd => EditKey.End,
                Keycode.Tab => e.IsShiftPressed ? EditKey.ShiftTab : EditKey.Tab,
                _ => EditKey.None,
            };
            if (key != EditKey.None)
            {
                var mods = (e.IsShiftPressed ? KeyMods.Shift : KeyMods.None)
                         | (e.IsCtrlPressed ? KeyMods.Ctrl : KeyMods.None);
                host.OnGlThread(() => host.Ime(d => d.DispatchKey(null, key, mods)));
                return true;
            }
        }
        return base.SendKeyEvent(e);
    }

    /// <summary>Move the caret or select a range. THE call a soft keyboard makes to move the
    /// cursor — swiping the spacebar on FUTO, tapping to reposition, selecting a word to correct.
    /// Unimplemented, it lands on BaseInputConnection's private Editable, "succeeds", and changes
    /// nothing the user can see.</summary>
    public override bool SetSelection(int start, int end)
    {
        host.OnGlThread(() => host.Ime(d => d.SetTextSelection(start, end)));
        return true;
    }

    /// <summary>Mark an already-committed range as preedit — what a keyboard asks for when you tap
    /// a finished word to correct it.</summary>
    public override bool SetComposingRegion(int start, int end)
    {
        host.OnGlThread(() => host.Ime(d => d.SetComposingRegion(start, end)));
        return true;
    }

    /// <summary>The code-point-correct sibling of <see cref="DeleteSurroundingText"/>. The engine's
    /// Backspace/Delete already step by code point, so this is the same call — and the fact that
    /// both map here is the point: an emoji deletes as one glyph either way.</summary>
    public override bool DeleteSurroundingTextInCodePoints(int beforeLength, int afterLength) =>
        DeleteSurroundingText(beforeLength, afterLength);

    /// <summary>An IME asking where the caret is drawn — the call that lets it put its candidate
    /// window over the word being corrected instead of guessing.
    ///
    /// <para>Two INDEPENDENT bits, not an enum: IMMEDIATE means "answer once, now", MONITOR means
    /// "keep telling me until I say otherwise". Gboard sends both together when it opens a
    /// suggestion strip, so treating them as alternatives would answer once and then go quiet.
    /// Unimplemented, this returns false and a keyboard stops asking — which is why the strip had
    /// nowhere to go but its own top edge.</para></summary>
    public override bool RequestCursorUpdates(int cursorUpdateMode)
    {
        const int Immediate = 1;   // InputConnection.CURSOR_UPDATE_IMMEDIATE
        const int Monitor = 2;     // InputConnection.CURSOR_UPDATE_MONITOR

        view.StartCursorMonitoring((cursorUpdateMode & Monitor) != 0);

        // Answered from the post-frame snapshot like every other read here, and on the UI thread
        // we are already on — the IME expects the immediate report before this call returns.
        if ((cursorUpdateMode & Immediate) != 0) view.PublishCursorAnchorInfo(State);
        return true;
    }

    /// <summary>Whether the next character should auto-capitalise. Without this a keyboard cannot
    /// shift itself at the start of a sentence, which is most of what "it feels wrong to type in"
    /// amounts to on a phone.</summary>
    public override CapitalizationMode GetCursorCapsMode(CapitalizationMode reqModes)
    {
        var state = State;
        if (!state.Focused) return 0;

        var text = state.Value ?? "";
        var caret = System.Math.Clamp(state.SelStart, 0, text.Length);
        var before = text[..caret];

        var mode = (CapitalizationMode)0;
        var trimmed = before.TrimEnd();
        // Sentence start: nothing before the caret, or the last non-space character ends a sentence.
        if ((reqModes & CapitalizationMode.Sentences) != 0
            && (trimmed.Length == 0 || trimmed[^1] is '.' or '!' or '?')
            && (before.Length == 0 || before.Length > trimmed.Length || trimmed.Length == 0))
            mode |= CapitalizationMode.Sentences;
        if ((reqModes & CapitalizationMode.Characters) != 0) { /* the field never forces caps */ }
        return mode;
    }

    /// <summary>An IME wraps a compound edit (replace a word, move the caret, update the
    /// composition) in a batch. Coalescing it means one repaint for the whole thing instead of one
    /// per step — and the caret lands once, rather than visibly hopping through intermediate
    /// positions.</summary>
    public override bool BeginBatchEdit()
    {
        _batchDepth++;
        return true;
    }

    public override bool EndBatchEdit()
    {
        if (_batchDepth > 0) _batchDepth--;
        if (_batchDepth == 0) host.OnGlThread(host.MarkDirty);
        return _batchDepth > 0;
    }

    private int _batchDepth;

    /// <summary>The IME's own edit menu — its select-all, cut, copy and paste. These route to the
    /// SAME clipboard seam as the engine's context menu and the Ctrl chords, so all three agree
    /// about what "paste" means on this platform.</summary>
    public override bool PerformContextMenuAction(int id)
    {
        // The ids are android.R.id.*; the framework passes them straight through.
        const int SelectAll = 0x0102001f, Cut = 0x01020020, Copy = 0x01020021, Paste = 0x01020022;
        switch (id)
        {
            case SelectAll: host.OnGlThread(() => host.Ime(d => d.DispatchKey(null, EditKey.SelectAll))); return true;
            case Cut: host.OnGlThread(() => host.ContextCommandFromIme(ContextCommand.Cut)); return true;
            case Copy: host.OnGlThread(() => host.ContextCommandFromIme(ContextCommand.Copy)); return true;
            case Paste: host.OnGlThread(() => host.ContextCommandFromIme(ContextCommand.Paste)); return true;
            default: return false;
        }
    }

    /// <summary>The editor mirrored for an IME that renders its own copy — landscape "extract
    /// mode", and the several keyboards that track the full text this way rather than by asking
    /// around the cursor. Answered from the same post-frame snapshot as every other read.</summary>
    public override ExtractedText? GetExtractedText(ExtractedTextRequest? request, GetTextFlags flags)
    {
        var state = State;
        if (!state.Focused) return null;

        // Monitoring: the IME asks once and expects to be TOLD about later changes. The bit is
        // GET_EXTRACTED_TEXT_MONITOR (1) — which the managed enum only spells as WithStyles, since
        // the platform reuses the value 1 for a different meaning on the get-text calls. Tested
        // numerically rather than pretending the enum name fits.
        if (((int)flags & 1) != 0) view.StartExtractMonitoring(request?.Token ?? 0);

        var text = state.Value ?? "";
        return new ExtractedText
        {
            Text = new Java.Lang.String(text),
            StartOffset = 0,
            SelectionStart = System.Math.Clamp(state.SelStart, 0, text.Length),
            SelectionEnd = System.Math.Clamp(state.SelEnd, 0, text.Length),
            PartialStartOffset = -1,
            PartialEndOffset = -1,
            Flags = state.Multiline ? 0 : ExtractedTextFlags.SingleLine,
        };
    }

    // ---- synchronous questions (answered from the post-frame snapshot) ------------------------

    public override ICharSequence? GetTextBeforeCursorFormatted(int length, GetTextFlags flags)
    {
        var s = State;
        if (!s.Focused) return new Java.Lang.String("");
        var end = System.Math.Clamp(s.SelStart, 0, s.Value.Length);
        var start = System.Math.Max(0, end - length);
        return new Java.Lang.String(s.Value[start..end]);
    }

    public override ICharSequence? GetTextAfterCursorFormatted(int length, GetTextFlags flags)
    {
        var s = State;
        if (!s.Focused) return new Java.Lang.String("");
        var start = System.Math.Clamp(s.SelEnd, 0, s.Value.Length);
        var end = System.Math.Min(s.Value.Length, start + length);
        return new Java.Lang.String(s.Value[start..end]);
    }

    public override ICharSequence? GetSelectedTextFormatted(GetTextFlags flags)
    {
        var s = State;
        if (!s.Focused || s.SelStart == s.SelEnd) return null;
        return new Java.Lang.String(s.Value[s.SelStart..s.SelEnd]);
    }
}
