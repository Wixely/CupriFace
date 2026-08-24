using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Mid-token line breaking (issue #59): a long string with no spaces — a 62-char bech32 address —
/// always overflowed its container, and none of `word-break: break-all`, `overflow-wrap:
/// break-word|anywhere`, or legacy `word-wrap: break-word` did anything. One such token forced the
/// whole page into horizontal overflow, which broke responsive layouts outright rather than one
/// element. These pin: the emergency break (overflow-wrap), the pack-every-line break (break-all),
/// all spellings and the alias, inheritance, no characters lost, and forced progress in a
/// sliver-thin container.
/// </summary>
public class WordBreakTests
{
    private const string Address = "cupri1qyatwxe0wck97wu3yrg5mf3069tqud3c3ajpgnf54qved4we2k8qlnz2ju";

    private static RenderNode TextNode(CupriDocument doc, string cls)
    {
        RenderNode? box = null;
        void Walk(RenderNode n)
        {
            if (box is null && n.Element?.ClassList.Contains(cls) == true) box = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(doc.Root);
        RenderNode? text = null;
        void Find(RenderNode n)
        {
            if (text is null && n.IsText) text = n;
            foreach (var c in n.Children) Find(c);
        }
        Find(box!);
        return text!;
    }

    private static CupriDocument Load(string css) => CupriDocument.Load(
        $"<body><div class='box'>{Address}</div></body>",
        "body { margin:0; font-size:13px } .box { width:260px } " + css);

    [Theory]
    [InlineData(".box { overflow-wrap: break-word }")]
    [InlineData(".box { overflow-wrap: anywhere }")]
    [InlineData(".box { word-wrap: break-word }")]      // the legacy alias
    [InlineData(".box { word-break: break-all }")]
    public void Every_spelling_wraps_the_address_inside_the_box(string css)
    {
        using var doc = Load(css);
        doc.BuildFrame(400, 300);

        var text = TextNode(doc, "box");
        Assert.True(text.Lines!.Count > 1, "the address should wrap onto multiple lines");
        Assert.All(text.Lines, l => Assert.True(l.Width <= 260.5f,
            $"a line is {l.Width:F0}px wide in a 260px box"));

        // No characters may be invented or lost by the breaking — the address must survive intact,
        // because comparison is the whole reason it is displayed.
        Assert.Equal(Address, string.Concat(text.Lines.Select(l => l.Text)));
    }

    [Fact]
    public void Without_a_break_property_the_token_still_overflows()
    {
        // The control, and the pre-existing behaviour pinned: unbreakable means unbreakable unless
        // a property says otherwise.
        using var doc = Load("");
        doc.BuildFrame(400, 300);

        var text = TextNode(doc, "box");
        Assert.Single(text.Lines!);
        Assert.True(text.Lines![0].Width > 260f, "the control case should overflow its box");
    }

    [Fact]
    public void Break_all_packs_the_line_that_break_word_leaves_short()
    {
        // The semantic difference between the two properties: after a short word, break-all fills
        // the rest of the line with the long token's head; break-word wraps the token whole first
        // and only splits what cannot fit a line by itself. Same markup, different first lines.
        var html = $"<body><div class='box'>id {Address}</div></body>";
        using var all = CupriDocument.Load(html,
            "body { margin:0; font-size:13px } .box { width:260px; word-break: break-all }");
        using var word = CupriDocument.Load(html,
            "body { margin:0; font-size:13px } .box { width:260px; overflow-wrap: break-word }");
        all.BuildFrame(400, 300);
        word.BuildFrame(400, 300);

        var allFirst = TextNode(all, "box").Lines![0].Text;
        var wordFirst = TextNode(word, "box").Lines![0].Text;

        Assert.StartsWith("id ", allFirst);
        Assert.True(allFirst.Length > "id ".Length + 4, "break-all should pack the first line with the token's head");
        Assert.Equal("id", wordFirst);                    // break-word: the token starts its own line
    }

    [Fact]
    public void The_property_inherits_from_an_ancestor()
    {
        // Both properties are inherited in CSS — declaring on a container must reach the text.
        using var doc = CupriDocument.Load(
            $"<body><div class='outer'><div><div class='box'>{Address}</div></div></div></body>",
            "body { margin:0; font-size:13px } .outer { overflow-wrap: break-word } .box { width:260px }");
        doc.BuildFrame(400, 300);

        Assert.True(TextNode(doc, "box").Lines!.Count > 1, "overflow-wrap declared two levels up should apply");
    }

    [Fact]
    public void A_sliver_thin_container_terminates_with_one_code_point_per_line()
    {
        // The progress guarantee: a container narrower than one glyph must not loop forever, and
        // every line still carries at least one code point.
        using var doc = CupriDocument.Load(
            "<body><div class='box'>abcdef</div></body>",
            "body { margin:0; font-size:13px } .box { width:2px; overflow-wrap: anywhere }");
        doc.BuildFrame(400, 300);

        var text = TextNode(doc, "box");
        Assert.Equal(6, text.Lines!.Count);
        Assert.All(text.Lines, l => Assert.Equal(1, l.Text.Length));
    }

    [Fact]
    public void Surrogate_pairs_are_never_split()
    {
        // Emoji are surrogate pairs in UTF-16: a break that lands inside one paints two broken
        // glyphs. Every line must start on a real code-point boundary.
        var emoji = string.Concat(Enumerable.Repeat("🟦", 30)); // 60 chars, 30 code points
        using var doc = CupriDocument.Load(
            $"<body><div class='box'>{emoji}</div></body>",
            "body { margin:0; font-size:13px } .box { width:60px; word-break: break-all }");
        doc.BuildFrame(400, 300);

        var text = TextNode(doc, "box");
        Assert.True(text.Lines!.Count > 1);
        Assert.All(text.Lines, l =>
        {
            Assert.True(l.Text.Length > 0);
            Assert.False(char.IsLowSurrogate(l.Text[0]), "a line begins mid-surrogate-pair");
            Assert.False(char.IsHighSurrogate(l.Text[^1]), "a line ends mid-surrogate-pair");
        });
        Assert.Equal(emoji, string.Concat(text.Lines.Select(l => l.Text)));
    }
}
