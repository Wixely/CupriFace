using CupriFace.Binding;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Whitespace a person typed is theirs, and every text control must keep it.
///
/// <para><b>What was reported, and what it turned out to be.</b> Spaces typed at the end of the
/// composer "appeared to be trimmed". The value was never trimmed — the model held
/// <c>"abc  "</c> throughout. What failed was the CARET: line layout drops trailing whitespace, as
/// prose layout should, and the caret was measured against the PAINTED row rather than the logical
/// value, so it stopped at the last non-space glyph and typing more spaces moved nothing. Text you
/// cannot see plus a caret that does not move is indistinguishable from text that was discarded.</para>
///
/// <para>So this file asserts both halves for every text control: the value keeps the whitespace,
/// and the caret advances over it. The second is what the person actually sees.</para>
/// </summary>
public class InputWhitespaceTests
{
    [CupriBindable]
    public sealed partial class M
    {
        public string V { get; set; } = "";
    }

    /// <summary>Every control a person types free text into. <c>cupri-number</c> is excluded on
    /// purpose — it parses what it is given — and <c>cupri-tagfield</c> too, which splits on commas
    /// and trims each chip by design.</summary>
    public static TheoryData<string, string> TextControls => new()
    {
        { "cupri-textfield", "cupri-textfield" },
        { "cupri-textarea", "cupri-ta-body" },
        { "cupri-search", "cupri-search-field" },
        { "cupri-password", "cupri-pw-field" },
    };

    private static RenderNode? Node(TestDoc t, string cls)
    {
        RenderNode? found = null;
        void Walk(RenderNode n)
        {
            if (n.Element?.ClassList.Contains(cls) == true) found ??= n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(t.Doc.Root);
        return found;
    }

    private static (TestDoc Doc, M Model) Focused(string tag, string anchorClass)
    {
        var m = new M();
        var t = new TestDoc($"<body><{tag} value=\"{{{{V}}}}\"></{tag}></body>", "", m, components: true);
        var n = Node(t, anchorClass);
        Assert.True(n is not null, $"could not find .{anchorClass} for <{tag}>");
        t.Doc.DispatchClick(n!.X + 5, n.Y + n.Height / 2);
        t.Layout();
        return (t, m);
    }

    [Theory]
    [MemberData(nameof(TextControls))]
    public void TrailingSpacesReachTheModel(string tag, string anchorClass)
    {
        var (t, m) = Focused(tag, anchorClass);
        using var _ = t;

        t.Doc.DispatchKey("abc", EditKey.None);
        t.Doc.DispatchKey(" ", EditKey.None);
        t.Doc.DispatchKey(" ", EditKey.None);
        t.Layout();

        Assert.Equal("abc  ", m.V);
    }

    /// <summary>The half that was actually broken: the caret must move when a space is typed at the
    /// end, or the field looks like it is silently discarding what you type.</summary>
    [Theory]
    [MemberData(nameof(TextControls))]
    public void TheCaretAdvancesOverTrailingSpaces(string tag, string anchorClass)
    {
        var (t, _) = Focused(tag, anchorClass);
        using var _d = t;

        t.Doc.DispatchKey("abc", EditKey.None);
        t.Layout();
        var before = t.Doc.GetTextInputState().CaretRect;

        t.Doc.DispatchKey(" ", EditKey.None);
        t.Doc.DispatchKey(" ", EditKey.None);
        t.Layout();
        var after = t.Doc.GetTextInputState().CaretRect;

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.True(after!.Value.X > before!.Value.X,
            $"<{tag}>: caret did not move after typing two spaces ({before.Value.X} -> {after.Value.X})");
    }

    /// <summary>Leading and interior runs of spaces too — a person pasting indented text, or lining
    /// something up by eye, keeps every space of it.</summary>
    [Theory]
    [MemberData(nameof(TextControls))]
    public void LeadingAndInteriorWhitespaceIsKept(string tag, string anchorClass)
    {
        var (t, m) = Focused(tag, anchorClass);
        using var _ = t;

        t.Doc.DispatchKey("  a   b  ", EditKey.None);
        t.Layout();

        Assert.Equal("  a   b  ", m.V);
    }

    /// <summary>A textarea keeps blank lines and the trailing spaces on each of them — this is where
    /// someone writes code or a list, and both would be quietly mangled.</summary>
    [Fact]
    public void TextAreaKeepsBlankLinesAndPerLineTrailingSpaces()
    {
        var (t, m) = Focused("cupri-textarea", "cupri-ta-body");
        using var _ = t;

        t.Doc.DispatchKey("one  ", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);
        t.Doc.DispatchKey(null, EditKey.Enter);
        t.Doc.DispatchKey("two ", EditKey.None);
        t.Layout();

        Assert.Equal("one  \n\ntwo ", m.V);
    }

    /// <summary>A value that arrives from the MODEL with whitespace is not trimmed on the way in
    /// either — binding a string with padding must not silently rewrite it.</summary>
    [Theory]
    [MemberData(nameof(TextControls))]
    public void AWhitespacePaddedModelValueIsNotRewritten(string tag, string anchorClass)
    {
        var m = new M { V = "  padded  " };
        using var t = new TestDoc($"<body><{tag} value=\"{{{{V}}}}\"></{tag}></body>", "", m, components: true);
        var n = Node(t, anchorClass);
        Assert.NotNull(n);

        t.Doc.DispatchClick(n!.X + 5, n.Y + n.Height / 2);
        t.Layout();

        Assert.Equal("  padded  ", m.V);
    }

    /// <summary>The controls that DO trim, asserted so the distinction is deliberate rather than
    /// accidental: a tag field splits on commas and each chip is a word, not a phrase with padding.</summary>
    [Fact]
    public void TagFieldTrimsItsChipsOnPurpose()
    {
        var m = new M();
        using var t = new TestDoc(
            "<body><cupri-tagfield value=\"{{V}}\"></cupri-tagfield></body>", "", m, components: true);

        var n = Node(t, "cupri-tagfield");
        if (n is null) return;                 // the control is not registered in this build

        t.Doc.DispatchClick(n.X + 5, n.Y + n.Height / 2);
        t.Layout();
        t.Doc.DispatchKey("  alpha  ", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);
        t.Layout();

        Assert.DoesNotContain("  ", m.V);
    }
}
