using CupriFace.Components;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The <c>cupri-*</c> catalogue is <c>box-sizing: border-box</c>; author markup is not.
///
/// <para>A control chose its own padding and often its own border, so a caller who writes
/// <c>width: 120px</c> on one means the box they can see. Under content-box that silently excluded
/// the control's padding and it came out wider than asked for — indistinguishable from a layout
/// bug.</para>
/// </summary>
public class ComponentBoxSizingTests(ITestOutputHelper output)
{
    /// <summary>A width a CALLER sets is the box they can see — the whole point of the change.</summary>
    [Fact]
    public void A_width_set_on_a_control_is_the_outer_box()
    {
        using var t = new TestDoc(
            "<body><cupri-textfield style='width:220px'></cupri-textfield></body>", "",
            width: 600, height: 200, components: true);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(600, 200)) { }
        Assert.Equal(220f, t.FindClass("cupri-textfield").Width, 0.5);
    }

    /// <summary>An AUTHOR's own element keeps the CSS default. Flipping that under them would be a
    /// surprise in the other direction, and someone who knows CSS expects content-box in their own
    /// markup.</summary>
    [Fact]
    public void An_author_element_still_uses_content_box()
    {
        using var t = new TestDoc(
            "<body><div class='mine'>x</div></body>",
            ".mine { width:200px; padding:12px; background:#eee; }",
            width: 600, height: 200);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(600, 200)) { }
        Assert.Equal(224f, t.FindClass("mine").Width, 0.5);   // 200 content + 12 each side
    }

    /// <summary>
    /// EVERY CONTROL LAYS OUT AT THE SIZE IT ALWAYS DID. The migration re-derived each control's own
    /// declared sizes so the flip changes what a CALLER's width means without changing any control's
    /// appearance — the text field's <c>min-height</c> went 20 → 42 (its 9px padding and 2px border
    /// each side now counted), the textarea's 78 → 102, the checkbox and radio 20 → 24.
    ///
    /// <para>These are the four that moved, found by censusing all 79 registered components before
    /// and after. Pinned here so a later control added with content-box arithmetic is caught by a
    /// test rather than by eye.</para>
    /// </summary>
    [Theory]
    [InlineData("cupri-textfield", 248f, 42f)]
    [InlineData("cupri-textarea", 600f, 102f)]
    [InlineData("cupri-checkbox", 24f, 24f)]
    [InlineData("cupri-radio", 24f, 24f)]
    public void The_controls_that_moved_lay_out_at_their_original_size(string tag, float w, float h)
    {
        using var t = new TestDoc($"<body><{tag}>Label</{tag}></body>",
                                  "body { background:#fff; margin:0; }",
                                  width: 600, height: 300, components: true);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(600, 300)) { }
        var n = t.Find(x => x.Element?.LocalName == tag)!;
        output.WriteLine($"{tag} {n.Width:0.0}x{n.Height:0.0}");
        Assert.Equal(w, n.Width, 0.5);
        Assert.Equal(h, n.Height, 0.5);
    }

    /// <summary>The rule is generated from the registry, so a control added tomorrow is covered
    /// without anyone remembering to list it. A hand-kept list of forty tags is wrong within a
    /// month.</summary>
    [Fact]
    public void Every_registered_tag_is_in_the_rule()
    {
        var registry = ComponentRegistry.Default();
        var css = registry.AggregatedCss;
        var rule = css[..css.IndexOf('\n')];
        foreach (var tag in registry.Tags)
            Assert.Contains(tag, rule, StringComparison.Ordinal);
        Assert.Contains("border-box", rule, StringComparison.Ordinal);
    }
}
