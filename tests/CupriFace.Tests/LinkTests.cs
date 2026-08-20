using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Style;
using Xunit;

namespace CupriFace.Tests;

/// <summary><c>&lt;a href&gt;</c> links: an in-page <c>#anchor</c> is scrolled into view by the engine;
/// every other href raises <c>Navigated</c>; only explicitly safe OS/browser schemes are external. Links are
/// focusable (Enter activates) and show the pointer cursor.</summary>
public class LinkTests
{
    [Fact]
    public void Internal_link_raises_navigated_as_not_external()
    {
        using var t = new TestDoc("<body><a href=\"charts\" class=\"go\">Charts</a></body>", "", width: 240, height: 120);
        NavigateEvent? got = null;
        t.Doc.Navigated += e => got = e;

        t.HoverClass("go");                                  // (also proves the link is hit-testable)
        Assert.Equal(CursorType.Pointer, t.Doc.CursorAt(t.FindClass("go").X + 4, t.FindClass("go").Y + 6));
        t.ClickMatch(n => n.Element?.ClassList.Contains("go") == true);

        Assert.NotNull(got);
        Assert.Equal("charts", got!.Value.Href);
        Assert.False(got.Value.External);
    }

    [Theory]
    [InlineData("https://skia.org")]
    [InlineData("mailto:hi@example.com")]
    [InlineData("tel:+442071234567")]
    public void Supported_absolute_links_are_external(string href)
    {
        using var t = new TestDoc($"<body><a href=\"{href}\" class=\"go\">Link</a></body>", "", width: 260, height: 120);
        NavigateEvent? got = null;
        t.Doc.Navigated += e => got = e;

        t.ClickMatch(n => n.Element?.ClassList.Contains("go") == true);
        Assert.NotNull(got);
        Assert.Equal(href, got!.Value.Href);
        Assert.True(got.Value.External);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("intent://scan/#Intent;scheme=zxing;end")]
    [InlineData("data:text/html,hello")]
    [InlineData("custom:route")]
    [InlineData("//cdn.example.com/x")]
    public void Unsafe_or_ambiguous_schemes_are_never_marked_for_the_host(string href)
    {
        using var t = new TestDoc($"<body><a href=\"{href}\" class=\"go\">Link</a></body>", "",
            width: 260, height: 120);
        NavigateEvent? got = null;
        t.Doc.Navigated += e => got = e;

        t.ClickMatch(n => n.Element?.ClassList.Contains("go") == true);

        Assert.NotNull(got);                // the app may still implement a custom in-app route
        Assert.Equal(href, got!.Value.Href);
        Assert.False(got.Value.External);   // but hosts must not hand it to an OS/browser handler
    }

    [Fact]
    public void Anchor_link_scrolls_the_target_into_view_and_does_not_navigate()
    {
        using var t = new TestDoc(
            "<body>" +
            "<a href=\"#target\" class=\"jump\" style=\"display:block\">Jump</a>" +
            "<div class=\"scroller\" style=\"height:100px; overflow:scroll\">" +
            "  <div style=\"height:140px\">top filler</div>" +
            "  <div id=\"target\" style=\"height:20px\">TARGET</div>" +
            "  <div style=\"height:140px\">bottom filler</div>" +
            "</div></body>", "", width: 260, height: 200);

        var scroller = t.FindClass("scroller");
        Assert.True(scroller.MaxScrollY > 50);               // it can actually scroll
        Assert.Equal(0, scroller.ScrollY, 0.5);              // starts at the top

        NavigateEvent? got = null;
        t.Doc.Navigated += e => got = e;
        t.ClickMatch(n => n.Element?.ClassList.Contains("jump") == true);

        Assert.Null(got);                                    // an anchor does not raise Navigated
        Assert.True(t.FindClass("scroller").ScrollY > 80, "the target should have scrolled near the top");
    }

    [Fact]
    public void A_focused_link_activates_on_enter()
    {
        using var t = new TestDoc("<body><a href=\"about\" class=\"go\">About</a></body>", "", width: 220, height: 100);
        NavigateEvent? got = null;
        t.Doc.Navigated += e => got = e;

        t.Key(EditKey.Tab);                                  // focus the (only) focusable — the link
        t.Key(EditKey.Enter);                                // activate it from the keyboard
        Assert.NotNull(got);
        Assert.Equal("about", got!.Value.Href);
    }
}
