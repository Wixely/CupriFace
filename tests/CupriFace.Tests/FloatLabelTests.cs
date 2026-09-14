using CupriFace.Accessibility;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>float-label</c> on <c>&lt;cupri-textfield&gt;</c>: the placeholder stays as a label once the
/// field has something in it, instead of vanishing at the moment it is most needed.
///
/// <para>The point is space. A labelled field normally costs two lines — a label above and a box
/// below — and a placeholder-only field costs one but forgets what it was for as soon as you type
/// into it. This costs one: the prompt sits where the value will go while the field is empty, and
/// rises to a smaller line inside the box once there is a value to label.</para>
///
/// <para>The row is reserved whether the label has risen or not. Typing the first character must not
/// shove the rest of a form down, which is asserted below because it is the thing most likely to
/// break and the least likely to be noticed in a screenshot.</para>
/// </summary>
public class FloatLabelTests(ITestOutputHelper output)
{
    private sealed class M { public string Note { get; set; } = ""; }

    private const string Css = "body{margin:0;font-family:sans-serif;background:#fff}"
                             + "cupri-textfield{width:320px}";

    private static (TestDoc T, M Model) Field(string value, bool floatLabel = true,
                                              string placeholder = "What is wrong with it?")
    {
        var m = new M { Note = value };
        var ph = placeholder.Length > 0 ? $" placeholder='{placeholder}'" : "";
        var fl = floatLabel ? " float-label" : "";
        var t = new TestDoc($"<body><cupri-textfield value='{{{{Note}}}}'{ph}{fl}></cupri-textfield></body>",
                            Css, m, width: 380, height: 140, components: true);
        return (t, m);
    }

    private static RenderNode? LabelNode(TestDoc t)
        => t.Find(n => n.Element?.ClassList.Contains("cupri-tf-label") == true);

    private static bool Raised(TestDoc t) => LabelNode(t)?.Element?.HasAttribute("data-raised") == true;

    private static AccessibilityNode Textbox(TestDoc t)
    {
        AccessibilityNode? Walk(AccessibilityNode n)
        {
            if (n.Role == "textbox") return n;
            foreach (var c in n.Children) { var f = Walk(c); if (f is not null) return f; }
            return null;
        }
        return Walk(t.Doc.BuildAccessibilityTree(380, 140))!;
    }

    // ---- the two states ---------------------------------------------------------------------

    [Fact]
    public void An_empty_field_shows_the_prompt_where_the_value_will_go()
    {
        using var t = Field("").T;
        var label = LabelNode(t);

        Assert.NotNull(label);
        Assert.False(Raised(t));
        t.Dispose();
    }

    [Fact]
    public void A_field_with_a_value_raises_its_label()
    {
        var (t, _) = Field("test123");
        Assert.True(Raised(t));
        t.Dispose();
    }

    /// <summary>The label rises when someone TYPES, not only when the model arrives pre-filled —
    /// which is the whole interaction and goes through a rebuild to get there.</summary>
    [Fact]
    public void Typing_the_first_character_raises_the_label()
    {
        var (t, m) = Field("");
        t.ClickNode(t.FindClass("cupri-textfield"));
        Assert.False(Raised(t));

        t.Type("a");
        Assert.True(Raised(t));

        // …and deleting it back to empty puts the prompt back where it started.
        t.Key(EditKey.Backspace);
        Assert.False(Raised(t));
        output.WriteLine("model: '" + m.Note + "'");
        t.Dispose();
    }

    /// <summary>Nothing below the field may move when the label rises. The row is reserved in the
    /// padding either way; if it were not, every field in a form would jog downward on the first
    /// keystroke.</summary>
    [Fact]
    public void The_field_is_the_same_height_empty_and_filled()
    {
        var (a, _) = Field("");
        var empty = a.FindClass("cupri-textfield").Height;
        a.Dispose();

        var (b, _) = Field("test123");
        var filled = b.FindClass("cupri-textfield").Height;
        b.Dispose();

        output.WriteLine($"empty {empty:0.0}  filled {filled:0.0}");
        Assert.Equal(empty, filled, 0.5);
    }

    // ---- what it costs, and what it leaves alone -----------------------------------------------

    /// <summary>
    /// It is EXACTLY as tall as a plain field, filled or empty. One of these sitting in a row beside
    /// ordinary fields has to line up with them; a control nine pixels taller than its neighbours
    /// reads as a mistake whatever it is doing with its label.
    ///
    /// <para>That is why the label's row is bought out of the existing padding rather than added to
    /// it. The only visible cost is the value sitting 3px lower than it would in a plain field.</para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("test123")]
    public void It_is_exactly_as_tall_as_a_plain_field(string value)
    {
        var (a, _) = Field(value);
        var floated = a.FindClass("cupri-textfield").Height;
        a.Dispose();

        var (b, _) = Field(value, floatLabel: false);
        var plain = b.FindClass("cupri-textfield").Height;
        b.Dispose();

        output.WriteLine($"plain {plain:0.0}  float-label {floated:0.0}");
        Assert.Equal(plain, floated, 0.5);
    }

    /// <summary>A field that did not ask for it is untouched — same markup as before, placeholder
    /// span and all.</summary>
    [Fact]
    public void A_field_without_the_attribute_keeps_the_old_behaviour()
    {
        var (t, _) = Field("", floatLabel: false);

        Assert.Null(LabelNode(t));
        Assert.NotNull(t.Find(n => n.Element?.ClassList.Contains("cupri-tf-ph") == true));
        Assert.False(t.FindClass("cupri-textfield").Element!.HasAttribute("data-float-label"));
        t.Dispose();
    }

    /// <summary>There is nothing to float without a placeholder, so the attribute does nothing
    /// rather than reserving a row for an empty label.</summary>
    [Fact]
    public void Without_a_placeholder_the_attribute_is_a_no_op()
    {
        var (t, _) = Field("test123", placeholder: "");

        Assert.Null(LabelNode(t));
        Assert.False(t.FindClass("cupri-textfield").Element!.HasAttribute("data-float-label"));
        t.Dispose();
    }

    // ---- what a screen reader gets --------------------------------------------------------------

    /// <summary>
    /// The label must not become part of the VALUE. It is rendered text inside the field, so the
    /// obvious implementation reads back "What is wrong with it? test123" as the thing someone
    /// typed. It carries the placeholder class for exactly this reason.
    /// </summary>
    [Fact]
    public void The_label_is_not_read_back_as_the_value()
    {
        var (t, _) = Field("test123");
        var box = Textbox(t);
        output.WriteLine($"name='{box.Name}' value='{box.Value}'");

        Assert.Equal("test123", (box.Value ?? "").Trim());
        t.Dispose();
    }

    /// <summary>…and it IS the field's name, in both states. That is better than an ordinary
    /// placeholder manages: that one is only rendered while the field is empty.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("test123")]
    public void The_field_is_named_by_its_label_whether_or_not_it_has_a_value(string value)
    {
        var (t, _) = Field(value);
        Assert.Equal("What is wrong with it?", Textbox(t).Name);
        t.Dispose();
    }

    // ---- and what someone actually sees ---------------------------------------------------------

    /// <summary>
    /// UNTIL SOMEONE TYPES, IT IS A PLAIN FIELD. Pixel for pixel — same height, same border, same
    /// prompt on the same line. The feature is meant to cost nothing until it has something to say,
    /// and an empty form of these should not read as a column of subtly different boxes.
    ///
    /// <para>Asserted by subtraction rather than by eye: the two are rendered alone at the same
    /// place and differenced. This is the assertion that catches the geometry drifting — the value
    /// sits 3px lower to make room for the risen label, and the label has to ignore that and stay on
    /// the plain field's text line.</para>
    /// </summary>
    [Fact]
    public void An_empty_one_is_indistinguishable_from_a_plain_field()
    {
        SKBitmap Shot(bool floatLabel)
        {
            var (t, _) = Field("", floatLabel);
            var bmp = t.Render(SKColors.White);
            t.Dispose();
            return bmp;
        }

        using var plain = Shot(false);
        using var floated = Shot(true);

        var diff = CupriFace.Diagnostics.ImageDiff.Compare(plain, floated);
        output.WriteLine(diff.ToString());
        Assert.True(diff.IsIdentical, "an empty float-label field must paint exactly like a plain one: " + diff);
    }

    /// <summary>
    /// …and it is still a plain field once someone has CLICKED INTO it. This is the state a person
    /// actually looks at longest before typing, and the one where the caret gives the geometry away:
    /// anchored to the value span it sat three pixels below the prompt it was standing in front of.
    /// </summary>
    [Fact]
    public void An_empty_focused_one_is_indistinguishable_too()
    {
        SKBitmap Shot(bool floatLabel)
        {
            var (t, _) = Field("", floatLabel);
            t.ClickNode(t.FindClass("cupri-textfield"));
            var bmp = t.Render(SKColors.White);
            t.Dispose();
            return bmp;
        }

        using var plain = Shot(false);
        using var floated = Shot(true);

        var diff = CupriFace.Diagnostics.ImageDiff.Compare(plain, floated);
        output.WriteLine(diff.ToString());
        Assert.True(diff.IsIdentical, "a focused empty float-label field must paint like a plain one: " + diff);
    }

    /// <summary>
    /// The caret never moves — not between empty and filled, and not from where a plain field puts
    /// it. Stated on the geometry as well as in pixels, because "the caret is 3px low" is the kind
    /// of thing an image diff reports as a blur of changed pixels rather than as a cause.
    ///
    /// <para>It took two goes. Anchoring the caret to the value span put it below the prompt it was
    /// standing in front of; then buying the label's room out of the padding moved the content box
    /// down, which moved the value, the caret AND the caret's clip — the clip cropping three pixels
    /// off the top of the caret in an empty field. The version that survives moves nothing at all.</para>
    /// </summary>
    [Fact]
    public void The_caret_is_exactly_where_a_plain_field_puts_it()
    {
        float CaretY(string value, bool floatLabel)
        {
            var (t, _) = Field(value, floatLabel);
            t.ClickNode(t.FindClass("cupri-textfield"));
            var y = t.Doc.GetTextInputState().CaretRect!.Value.Y;
            t.Dispose();
            return y;
        }

        var plainEmpty = CaretY("", false);
        var floatEmpty = CaretY("", true);
        var floatFilled = CaretY("test123", true);

        output.WriteLine($"plain {plainEmpty:0.00}   float empty {floatEmpty:0.00}   float filled {floatFilled:0.00}");
        Assert.Equal(plainEmpty, floatEmpty, 0.01);
        Assert.Equal(plainEmpty, floatFilled, 0.01);
    }

    /// <summary>…and the moment there IS a value, it is different — the control above proves nothing
    /// on its own if the label never moves.</summary>
    [Fact]
    public void A_filled_one_is_visibly_different()
    {
        var (a, _) = Field("test123", floatLabel: false);
        using var plain = a.Render(SKColors.White);
        a.Dispose();

        var (b, _) = Field("test123");
        using var floated = b.Render(SKColors.White);
        b.Dispose();

        Assert.False(CupriFace.Diagnostics.ImageDiff.Compare(plain, floated).IsIdentical);
    }

    /// <summary>The topmost row of pixels the label's ink occupies. A raised label has to be higher
    /// than a resting one — the display list can say `data-raised` all it likes.</summary>
    private static int LabelTop(TestDoc t)
    {
        using var bmp = t.Render(SKColors.White);
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 14; x < 200; x++)
            {
                var p = bmp.GetPixel(x, y);
                // The muted grey of the label, not the field's border or its near-black value text.
                if (p.Red is > 120 and < 190 && p.Green is > 130 and < 200 && p.Blue is > 140 and < 215)
                    return y;
            }
        return -1;
    }

    [Fact]
    public void The_label_actually_moves_up_on_screen()
    {
        var (a, _) = Field("");
        var resting = LabelTop(a);
        a.Dispose();

        var (b, _) = Field("test123");
        var raised = LabelTop(b);
        b.Dispose();

        output.WriteLine($"resting y={resting}  raised y={raised}");
        // -1 is "no label ink found at all"; 0 is legitimate now that the raised label reaches the
        // field's top edge to sit on its border.
        Assert.True(resting >= 0 && raised >= 0, $"could not find the label (resting {resting}, raised {raised})");
        Assert.True(raised < resting - 6, $"the raised label should sit clearly higher: {raised} vs {resting}");
    }
}
