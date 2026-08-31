using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// How an app learns that a link was followed (#89).
///
/// A click on an <c>&lt;a href&gt;</c> is claimed by the engine's built-in link branch, which raises
/// <see cref="CupriDocument.Navigated"/> and stops the walk — so an <c>OnClick("a", …)</c> selector
/// registered for the same anchor never runs. That is the intended split, but it is invisible: a
/// shadowed handler looks exactly like a selector that failed to match.
///
/// The distinction that matters is <c>Navigated</c> (the event, raised for EVERY non-<c>#</c> href)
/// versus a host's re-emission of it (raised only when <c>External</c>). <c>WebHostCore</c> subscribes
/// as <c>if (e.External) _js.Navigate(e.Href)</c>, so watching <c>IWebBridge.Navigate</c> shows only
/// the absolute http(s)/mailto/tel subset and makes relative and custom-scheme links look dropped.
/// They are not dropped; they arrive at <c>Navigated</c> with <c>External = false</c>, which is
/// exactly the in-app route — the shape the DemoApp already uses to change section.
/// </summary>
public class AnchorRoutingTests
{
    private const string Html = """
        <body style="margin:0">
          <a href="https://example.com/" class="abs"   style="display:block;height:20px">absolute</a>
          <a href="/second.html"         class="rel"   style="display:block;height:20px">relative</a>
          <a href="cuprinet://x"         class="sch"   style="display:block;height:20px">scheme</a>
          <button class="btn"                          style="display:block;height:20px">button</button>
        </body>
        """;

    /// <summary>The reporter's table, at the layer that actually carries the routing decision. All
    /// three hrefs arrive; only the absolute one is marked for a host to open externally.</summary>
    [Theory]
    [InlineData("abs", "https://example.com/", true)]
    [InlineData("rel", "/second.html", false)]
    [InlineData("sch", "cuprinet://x", false)]
    public void Every_href_form_reaches_the_app_through_Navigated(string cls, string href, bool external)
    {
        using var t = new TestDoc(Html, "", width: 200, height: 200);
        NavigateEvent? got = null;
        t.Doc.Navigated += e => got = e;

        t.ClickMatch(n => n.Element?.ClassList.Contains(cls) == true);

        Assert.NotNull(got);
        Assert.Equal(href, got!.Value.Href);
        Assert.Equal(external, got.Value.External);
    }

    /// <summary>The silence in #89: the link branch returns before the selector handlers are reached,
    /// so an anchor is never delivered to OnClick — for any href, external or not. The button proves
    /// the same registration call works; only the anchors are shadowed.</summary>
    [Fact]
    public void OnClick_never_sees_an_anchor_because_the_link_branch_claims_it_first()
    {
        using var t = new TestDoc(Html, "", width: 200, height: 200);
        var clicked = new List<string>();
        t.Doc.OnClick("a", e => clicked.Add("a:" + e.Element.GetAttribute("href")));
        t.Doc.OnClick("button", _ => clicked.Add("button"));

        foreach (var cls in new[] { "abs", "rel", "sch" })
            t.ClickMatch(n => n.Element?.ClassList.Contains(cls) == true);

        Assert.Empty(clicked);                              // no anchor reaches OnClick, whatever its href

        t.ClickMatch(n => n.Element?.ClassList.Contains("btn") == true);
        Assert.Equal(["button"], clicked);                  // …while the same mechanism works elsewhere
    }

    /// <summary>Why routing off a hit-test of the release position is not equivalent: a link can be
    /// followed from the keyboard, which produces no pointer position to hit-test. An app that routes
    /// by walking up from HitTest(x, y) silently loses keyboard navigation; Navigated carries it.</summary>
    [Fact]
    public void Keyboard_activation_routes_with_no_pointer_position_to_hit_test()
    {
        using var t = new TestDoc(Html, "", width: 200, height: 200);
        NavigateEvent? got = null;
        t.Doc.Navigated += e => got = e;

        t.Key(EditKey.Tab);                                 // first focusable is the first link
        t.Key(EditKey.Enter);

        Assert.NotNull(got);
        Assert.Equal("https://example.com/", got!.Value.Href);
    }
}
