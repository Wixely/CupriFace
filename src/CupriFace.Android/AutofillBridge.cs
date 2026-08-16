using Android.Util;
using Android.Views;
using Android.Views.Autofill;
using CupriFace.Accessibility;

namespace CupriFace.Android;

/// <summary>
/// Password managers and autofill services. Architecturally this is the TalkBack bridge's twin:
/// our GL surface is ONE opaque view to Android, so anything that wants to see individual fields —
/// a screen reader, an autofill service — has to be handed a VIRTUAL view hierarchy. Same problem,
/// same shape of answer, different consumer.
///
/// The engine's semantics tree already carries everything needed: which nodes are text fields,
/// where they are, and (from the author's <c>autocomplete</c> attribute) what kind of value each
/// one wants. A field with no <c>autocomplete</c> is deliberately NOT offered: guessing that
/// something is a password field would be a security bug wearing a convenience costume.
///
/// Filling goes back through the ordinary binding path, so a filled username lands exactly as a
/// typed one would — validation, formatting and change notification included.
/// </summary>
internal sealed class AutofillBridge(CupriHostView view, AndroidHost host)
{
    // Virtual id → structural path, rebuilt whenever the structure is requested. Autofill's
    // lifecycle is request/respond rather than long-lived, so unlike TalkBack's ids these need no
    // permanence — they only have to survive from the request to the fill.
    private readonly Dictionary<int, string> _byId = new();

    /// <summary>Describe the fillable fields. Called when an autofill service inspects the window.</summary>
    internal void ProvideStructure(ViewStructure structure)
    {
        var snapshot = host.Current;
        if (snapshot is null) return;

        var fields = new List<AccessibilityNode>();
        void Walk(AccessibilityNode n)
        {
            // Text entry that the author has labelled. Both halves matter: the role makes it
            // fillable, the hint makes it identifiable.
            if (n.Role is "textbox" or "spinbutton" && n.AutofillHint is { Length: > 0 } && !n.Offscreen)
                fields.Add(n);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(host.BuildSemanticsForAutofill());

        _byId.Clear();
        structure.SetClassName("android.view.ViewGroup");
        structure.ChildCount = fields.Count;

        var scale = snapshot.InputScale;
        for (var i = 0; i < fields.Count; i++)
        {
            var node = fields[i];
            var child = structure.NewChild(i);
            var id = i + 1;
            _byId[id] = node.Path;

            child.SetAutofillId(structure.AutofillId!, id);
            child.SetAutofillType(AutofillType.Text);
            child.SetAutofillHints(SplitHints(node.AutofillHint!));
            child.SetAutofillValue(AutofillValue.ForText(node.Value ?? ""));
            child.SetClassName("android.widget.EditText");
            child.SetVisibility(ViewStates.Visible);
            child.SetFocused(node.Focused);
            // The label a fill dialog shows beside the suggestion — the field's accessible name,
            // which is the same string a screen reader announces for it.
            if (node.Name is { Length: > 0 } label) child.SetContentDescription(label);

            var (x, y, w, h) = node.Bounds;
            child.SetDimens((int)(x * scale), (int)(y * scale), 0, 0, (int)(w * scale), (int)(h * scale));
        }
    }

    /// <summary>An autofill service filled one or more fields. Each value is written through the
    /// ordinary binding, on the document thread.</summary>
    internal void Fill(SparseArray values)
    {
        for (var i = 0; i < values.Size(); i++)
        {
            var id = values.KeyAt(i);
            if (values.Get(id) is not AutofillValue value || !value.IsText) continue;
            if (!_byId.TryGetValue(id, out var path)) continue;

            var text = value.TextValue?.ToString() ?? "";
            host.OnGlThread(() =>
            {
                if (host.Document.AccessibilitySetText(path, text)) host.MarkDirty();
            });
        }
    }

    // "current-password", or a space-separated list as the web platform allows.
    private static string[] SplitHints(string autocomplete) =>
        autocomplete.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
