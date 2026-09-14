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

    /// <summary>It is taller than a plain field, because it holds two rows of information. Asserted
    /// so the cost is a stated number rather than a surprise in someone's layout.</summary>
    [Fact]
    public void It_is_taller_than_a_plain_field_but_not_by_much()
    {
        var (a, _) = Field("test123");
        var floated = a.FindClass("cupri-textfield").Height;
        a.Dispose();

        var (b, _) = Field("test123", floatLabel: false);
        var plain = b.FindClass("cupri-textfield").Height;
        b.Dispose();

        output.WriteLine($"plain {plain:0.0}  float-label {floated:0.0}");
        Assert.InRange(floated - plain, 1f, 14f);
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
        Assert.True(resting > 0 && raised > 0, $"could not find the label (resting {resting}, raised {raised})");
        Assert.True(raised < resting - 6, $"the raised label should sit clearly higher: {raised} vs {resting}");
    }
}
