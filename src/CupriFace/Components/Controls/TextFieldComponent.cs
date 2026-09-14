using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-textfield value="{{Name}}" placeholder="…"&gt;</c> — an editable single-line
/// text field. role=textbox; two-way binds its value; focus/caret/typing are driven by the
/// document's key dispatch.
/// </summary>
public sealed class TextFieldComponent : ComponentBase
{
    public override string Tag => "cupri-textfield";
    public override string DefaultCss => """
        /* min-height reserves one line so the field never collapses when its value renders no
           line box — e.g. a whitespace-only value (the render tree drops whitespace text nodes)
           or an empty field mid-frame. Matches how real form controls keep a fixed height. */
        .cupri-textfield { display:inline-block; min-width:220px; min-height:20px; background:var(--cupri-surface, white);
                           border:2px var(--cupri-border, #cbd2dc); border-radius:8px; padding:9px 12px; font-size:15px;
                           white-space:nowrap; overflow:hidden; } /* single line: a long value scrolls, not wraps */
        .cupri-textfield[data-hover] { border-color:#98a2b3; }
        .cupri-textfield:focus { border-color:var(--cupri-accent,#B87333); }
        .cupri-textfield[data-invalid] { border-color:#d92d20; }
        .cupri-tf-text { color:var(--cupri-text, #1e2430); }
        .cupri-tf-ph { color:var(--cupri-muted, #98a2b3); }

        /* float-label: the placeholder IS the label. It sits where the value will be while the
           field is empty, and rises to a smaller line above it once there is something to label —
           so one field costs one label's worth of height instead of two, and an empty form still
           reads as prompts rather than as a column of headings.

           The row is reserved in the padding whether the label has risen or not, so typing the
           first character does not shove the rest of the form down.

           IT CHANGES NOTHING ABOUT THE BOX, and that is the constraint everything else here bends
           to. Same height, same padding, same content box — so the value, the caret and the caret's
           clip sit exactly where a plain field puts them, and one of these in a row of ordinary
           fields lines up with them in every state.

           That leaves the risen label nowhere to go but the 11px of headroom the top padding
           already provides, which is why it shrinks as far as it does. Buying the room instead — a
           deeper top padding, a shallower bottom — was tried first and moved the content box down
           with it: the value sat 3px low, and the caret's clip cut 3px off the top of the caret in
           an empty field. Six stray pixels, found by differencing against a plain field, and the
           reason this version does not touch the padding at all. */
        .cupri-textfield[data-float-label] { position:relative; }
        /* AT REST IT PAINTS EXACTLY WHERE A PLAIN FIELD'S PLACEHOLDER DOES. Until someone types,
           nobody should be able to tell the two apart — the feature is meant to cost nothing until
           it has something to say. It sits at the top of the content box, which is where a plain
           field's placeholder sits, because the box is never moved.

           Colour comes from .cupri-tf-ph and does not change when the label rises. Darkening it
           would have to name a second colour, and a theme that defines --cupri-muted would then see
           no change at all while an unthemed page did — a difference that only shows up in someone
           else's app. Size and position carry the state instead, in both. */
        .cupri-tf-label { position:absolute; left:0; top:0; font-size:15px;
                          transform-origin:left center; transition:transform 150ms; }
        /* transform, not top/font-size: transform is what this engine animates. */
        .cupri-tf-label[data-raised] { transform:translateY(-13px) scale(0.6); }
        /* Inline validation message the engine injects after an invalid, visited field. */
        .cupri-field-error { display:block; color:#d92d20; font-size:13px; margin:5px 0 2px; }
        """;

    public override void Expand(IElement el)
    {
        var value = Str(el, "value");
        el.SetAttribute("role", "textbox");
        el.ClassList.Add("cupri-textfield");

        // float-label: the placeholder becomes a label once the field has something in it, instead
        // of vanishing the moment it is most needed. Opt-in because it is a look, not because it
        // costs anything: the box is identical to a plain field's in every state.
        var placeholder = Str(el, "placeholder");
        if (Flag(el, "float-label") && placeholder.Length > 0)
        {
            el.SetAttribute("data-float-label", "");
            // The caret anchors to whatever is CURRENTLY on the text line, which is the label while
            // the field is empty and the value once there is one. Anchoring it to the value span
            // either way put the caret three pixels below the prompt it was sitting in front of, so
            // a focused empty field looked subtly wrong next to a plain one — the whole thing this
            // field is supposed to be indistinguishable from until you type.
            //
            // This is what the ordinary branch below already does: an empty field anchors the caret
            // to its placeholder span.
            var filled = value.Length > 0;
            el.InnerHtml = Label(placeholder, raised: filled, caretAnchor: !filled)
                           + $"<span class='cupri-tf-text'{(filled ? " data-caret-anchor" : "")}>{Escape(value)}</span>";
            return;
        }

        el.InnerHtml = value.Length > 0
            ? $"<span class='cupri-tf-text' data-caret-anchor>{Escape(value)}</span>"
            : $"<span class='cupri-tf-ph' data-caret-anchor>{Escape(placeholder)}</span>";
    }

    /// <summary>
    /// The floating label. <c>data-raised</c> rather than a second class so the state is one
    /// attribute an author can style on, and so the element itself persists across the rebuild that
    /// flips it — which is what lets the transition run rather than snap.
    ///
    /// <para>It keeps <c>cupri-tf-ph</c> as well, and that is not decoration. The accessibility tree
    /// treats any class ending <c>-ph</c> as a placeholder: excluded from the field's VALUE, and used
    /// as its NAME when nothing else names it. Without that the label's words would be read back as
    /// part of what someone had typed — "What is wrong with it? test123". With it, this field is
    /// named in BOTH states, which is better than an ordinary placeholder manages: that one is only
    /// rendered while the field is empty, so a filled field fell through to the attribute.</para>
    /// </summary>
    internal static string Label(string text, bool raised, bool caretAnchor = false) =>
        $"<span class='cupri-tf-ph cupri-tf-label'{(raised ? " data-raised" : "")}"
        + $"{(caretAnchor ? " data-caret-anchor" : "")}>{Escape(text)}</span>";

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
