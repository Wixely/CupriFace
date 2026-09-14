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
           first character does not shove the rest of the form down. */
        .cupri-textfield[data-float-label] { position:relative; padding-top:22px; padding-bottom:5px; }
        /* Colour comes from .cupri-tf-ph and does not change when it rises. Darkening it would
           have to name a second colour, and a theme that defines --cupri-muted would then see no
           change at all while an unthemed page did — a difference that only shows up in someone
           else's app. Size and position carry the state instead, in both. */
        .cupri-tf-label { position:absolute; left:12px; top:0; font-size:15px;
                          transform-origin:left center; transition:transform 150ms; }
        /* transform, not top/font-size: transform is what this engine animates. */
        .cupri-tf-label[data-raised] { transform:translateY(-17px) scale(0.76); }
        /* Inline validation message the engine injects after an invalid, visited field. */
        .cupri-field-error { display:block; color:#d92d20; font-size:13px; margin:5px 0 2px; }
        """;

    public override void Expand(IElement el)
    {
        var value = Str(el, "value");
        el.SetAttribute("role", "textbox");
        el.ClassList.Add("cupri-textfield");

        // float-label: the placeholder becomes a label once the field has something in it, instead
        // of vanishing the moment it is most needed. Opt-in, because it changes the field's height
        // and a dense form full of them is not always what an author wants.
        var placeholder = Str(el, "placeholder");
        if (Flag(el, "float-label") && placeholder.Length > 0)
        {
            el.SetAttribute("data-float-label", "");
            // The caret anchor is the VALUE span either way, empty or not, so the caret sits on the
            // text line rather than on the label — the label is a label, not the text being edited.
            el.InnerHtml = Label(placeholder, raised: value.Length > 0)
                           + $"<span class='cupri-tf-text' data-caret-anchor>{Escape(value)}</span>";
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
    internal static string Label(string text, bool raised) =>
        $"<span class='cupri-tf-ph cupri-tf-label'{(raised ? " data-raised" : "")}>{Escape(text)}</span>";

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
