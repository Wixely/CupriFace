using Microsoft.Playwright;
using Xunit;

namespace CupriFace.WebTouchGate;

/// <summary>
/// The web host's accessibility contract, driven by a real browser's accessibility tree.
///
/// <para>The other four bridges (UIA, AT-SPI, NSAccessibility, TalkBack) are each gated by a real
/// assistive-technology client. The web host had no gate at all, and the reason was circular: the
/// existing tests avoided the ARIA mirror because it was empty, and it was empty because nothing
/// noticed. Measured before this gate existed: zero nodes 20 s after boot, 45 nodes 109 ms after
/// the first click. A screen-reader user does not click first.</para>
///
/// <para>Playwright's <c>GetByRole</c> resolves through Chromium's accessibility tree — the same
/// tree a screen reader reads — which is the closest thing a browser offers to "a real AT client
/// asked". Everything here is asserted WITHOUT any prior input, because that is the state a
/// screen reader meets.</para>
///
/// <para>What this gate does NOT claim: that the mirror can be operated. It is read-only today
/// (activating a node reaches nothing) and has no geometry. Those are the next piece of work, and
/// when they land this file is where their guarantees go.</para>
/// </summary>
[Collection("web")]
public class A11yTests(WebHostFixture host)
{
    private const string Mirror = "#cupri-a11y";

    /// <summary>The state a screen reader meets on arrival. No input has been sent.</summary>
    private static async Task<IPage> ArriveAsync(WebHostFixture host)
    {
        var page = await host.DesktopAsync();
        // The mirror is published on the settled frame after any load animation, and once a second
        // while something animates. Either way it must be there without anyone touching the page.
        await page.WaitForFunctionAsync(
            $"() => document.querySelectorAll('{Mirror} [role]').length > 20",
            null, new() { Timeout = 15_000 });
        return page;
    }

    [Fact]
    public async Task The_mirror_is_populated_before_any_input()
    {
        var page = await ArriveAsync(host);
        var nodes = await page.EvaluateAsync<int>($"() => document.querySelectorAll('{Mirror} [role]').length");
        Assert.True(nodes > 20, $"only {nodes} nodes in the mirror with no input sent - it used to be zero until the first click");
    }

    [Fact]
    public async Task Controls_resolve_by_role_and_name_through_the_browser_tree()
    {
        var page = await ArriveAsync(host);
        // Two pieces of chrome that exist on every Showcase page, found the way an AT finds them.
        Assert.Equal(1, await page.GetByRole(AriaRole.Switch, new() { Name = "Dark mode" }).CountAsync());
        Assert.Equal(1, await page.GetByRole(AriaRole.Button, new() { Name = "Toggle sidebar" }).CountAsync());
    }

    [Fact]
    public async Task Every_interactive_node_has_an_accessible_name()
    {
        var page = await ArriveAsync(host);
        // An icon-only button with no label is announced as "button" and nothing else. The
        // Showcase had four of these, two of them owned by the pagination control itself.
        var nameless = await page.EvaluateAsync<string[]>($$"""
            () => [...document.querySelectorAll('{{Mirror}} [role="button"],{{Mirror}} [role="slider"],{{Mirror}} [role="switch"],{{Mirror}} [role="checkbox"],{{Mirror}} [role="radio"],{{Mirror}} [role="textbox"],{{Mirror}} [role="combobox"],{{Mirror}} [role="spinbutton"]')]
                .filter(n => !n.getAttribute('aria-label') && !n.textContent.trim())
                .map(n => n.getAttribute('role'))
            """);
        Assert.True(nameless.Length == 0, $"nameless interactive nodes: {string.Join(", ", nameless)}");
    }

    [Fact]
    public async Task A_field_reads_its_value_not_its_placeholder()
    {
        var page = await ArriveAsync(host);
        // A value-bearing role's text content is its accessible VALUE. It used to be the name, so an
        // empty search box read its own placeholder back as though it had been typed.
        var echoes = await page.EvaluateAsync<int>($$"""
            () => [...document.querySelectorAll('{{Mirror}} [role="textbox"],{{Mirror}} [role="searchbox"]')]
                .filter(n => n.getAttribute('aria-label') && n.textContent.trim() === n.getAttribute('aria-label').trim()).length
            """);
        Assert.Equal(0, echoes);
    }

    [Fact]
    public async Task Content_scrolled_out_of_view_is_hidden_from_the_tree()
    {
        var page = await ArriveAsync(host);
        // At the fixture's fixed 1100x780 the Inputs page overflows; whatever is below the fold
        // must say so, the way the desktop bridge reports IsOffscreen so Narrator skips it.
        var hidden = await page.EvaluateAsync<int>($"() => document.querySelectorAll('{Mirror} [aria-hidden=\"true\"]').length");
        Assert.True(hidden > 0, "nothing in the mirror is marked aria-hidden, on a page that scrolls");
    }

    [Fact]
    public async Task Tab_stays_with_the_engine()
    {
        var page = await ArriveAsync(host);
        // The engine owns Tab (the page handler stops the browser's default), so the mirror must not
        // advertise tab stops it can never receive. Measured: focus never leaves the keyboard textarea.
        await page.Keyboard.PressAsync("Tab");
        var active = await page.EvaluateAsync<string>("() => document.activeElement && document.activeElement.id");
        Assert.Equal("cupri-kbd", active);
        var tabStops = await page.EvaluateAsync<int>($"() => document.querySelectorAll('{Mirror} [tabindex]').length");
        Assert.Equal(0, tabStops);
    }
}
