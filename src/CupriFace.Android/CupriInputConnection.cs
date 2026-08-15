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
    private TextInputState State => host.Current?.TextInput ?? default;

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
            var key = e.KeyCode switch
            {
                Keycode.Del => EditKey.Backspace,
                Keycode.ForwardDel => EditKey.Delete,
                Keycode.Enter or Keycode.NumpadEnter => EditKey.Enter,
                _ => EditKey.None,
            };
            if (key != EditKey.None)
            {
                host.OnGlThread(() => host.Ime(d => d.DispatchKey(null, key)));
                return true;
            }
        }
        return base.SendKeyEvent(e);
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
