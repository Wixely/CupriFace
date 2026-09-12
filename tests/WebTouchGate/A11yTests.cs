using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

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
/// <para>Two halves. The first is what a screen reader can READ. The second (#133) is what it can
/// DO: the mirror is an overlay with geometry, a click dispatched to a node operates the control,
/// DOM focus follows the engine's focus and the engine follows focus an AT places — measured
/// against the desktop UIA bridge, these were the three rows the web host lacked.</para>
/// </summary>
[Collection("web")]
public class A11yTests(WebHostFixture host, ITestOutputHelper output)
{
    private const string Mirror = "#cupri-a11y";
    private const string DarkMode = "#cupri-a11y [role=\"switch\"][aria-label=\"Dark mode\"]";

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

    // ---- reading ---------------------------------------------------------------------------------

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
        // advertise tab stops it can never receive: no tabindex="0". Nodes ARE focusable by script
        // (tabindex="-1") — that is how DOM focus follows the engine's — so after a Tab the focus
        // is either on the node the engine focused or on the keyboard textarea, never anywhere else.
        await page.Keyboard.PressAsync("Tab");
        var active = await page.EvaluateAsync<string>(
            "() => { const a = document.activeElement; return a ? (a.id || (a.closest('#cupri-a11y') ? 'mirror-node' : a.tagName)) : 'none'; }");
        Assert.True(active is "cupri-kbd" or "mirror-node", $"focus went to '{active}'");
        var tabStops = await page.EvaluateAsync<int>($"() => document.querySelectorAll('{Mirror} [tabindex=\"0\"]').length");
        Assert.Equal(0, tabStops);
    }

    // ---- operating (#133) --------------------------------------------------------------------------

    [Fact]
    public async Task Every_node_sits_over_its_control_and_the_overlay_never_takes_the_pointer()
    {
        var page = await ArriveAsync(host);
        var sw = page.GetByRole(AriaRole.Switch, new() { Name = "Dark mode" });
        var box = await sw.BoundingBoxAsync();
        var canvas = await page.Locator("#cupri").BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.NotNull(canvas);
        output.WriteLine($"Dark mode switch at {box!.X:0},{box.Y:0} {box.Width:0}x{box.Height:0}; canvas {canvas!.Width:0}x{canvas.Height:0}");
        // Real geometry — the mirror used to be a 1x1 clipped div, which is what an AT's hit-testing,
        // focus ring and touch exploration all worked from.
        Assert.True(box.Width > 10 && box.Height > 10, "the switch has no size in the overlay");
        Assert.True(box.X >= canvas.X && box.Y >= canvas.Y
                    && box.X + box.Width <= canvas.X + canvas.Width + 1
                    && box.Y + box.Height <= canvas.Y + canvas.Height + 1, "the switch's box lies outside the canvas");
        // ...and it costs the pointer nothing: what is under the switch's centre is the canvas, so a
        // real click still goes down the ordinary input path.
        var under = await page.EvaluateAsync<string>(
            "([x, y]) => { const e = document.elementFromPoint(x, y); return e ? e.id : 'none'; }",
            new object[] { (double)(box.X + box.Width / 2), (double)(box.Y + box.Height / 2) });
        Assert.Equal("cupri", under);
    }

    [Fact]
    public async Task Activating_a_node_through_the_browser_tree_operates_the_control()
    {
        var page = await ArriveAsync(host);
        var sw = page.GetByRole(AriaRole.Switch, new() { Name = "Dark mode" });
        var before = await sw.GetAttributeAsync("aria-checked");
        var brightnessBefore = await ContentBrightnessAsync(page);

        // DispatchEvent, not Click: a screen reader activates a node by dispatching `click` to it —
        // no pointer, no coordinates. Locator.Click would drive a real pointer at the box's centre,
        // which the overlay deliberately passes through to the canvas, so a pass there would prove
        // the canvas path and say nothing about this one. Measured before #133: this left
        // aria-checked unchanged.
        await sw.DispatchEventAsync("click");

        var expected = before == "true" ? "false" : "true";
        await Assertions.Expect(sw).ToHaveAttributeAsync("aria-checked", expected, new() { Timeout = 5_000 });
        // And it is the MODEL that flipped, not the attribute: the page repainted in the new theme.
        var brightnessAfter = await ContentBrightnessAsync(page);
        output.WriteLine($"aria-checked {before} -> {expected}; content brightness {brightnessBefore:0} -> {brightnessAfter:0}");
        Assert.True(expected == "true" ? brightnessAfter < brightnessBefore : brightnessAfter > brightnessBefore,
            $"the canvas did not change theme (brightness {brightnessBefore:0} -> {brightnessAfter:0})");
    }

    [Fact]
    public async Task Focus_follows_the_engine_and_the_engine_follows_focus()
    {
        var page = await ArriveAsync(host);

        // Engine → DOM. Tab moves the engine's keyboard focus; the page puts DOM focus on that node
        // so the screen reader announces it — the web's UIA focus-changed event.
        await page.Keyboard.PressAsync("Tab");
        await page.WaitForFunctionAsync($"() => !!document.querySelector('{Mirror} [data-focused]')", null, new() { Timeout = 5_000 });
        var first = await page.EvaluateAsync<string[]>($$"""
            () => { const f = document.querySelector('{{Mirror}} [data-focused]'); const a = document.activeElement;
                    return [f.getAttribute('role'), f.getAttribute('aria-label') || '', a === f ? 'synced' : (a && a.id) || 'elsewhere']; }
            """);
        output.WriteLine($"after Tab: {first[0]} '{first[1]}' -> DOM focus {first[2]}");
        // A text field keeps DOM focus on the keyboard textarea (IME and clipboard live there);
        // anything else is announced from its own node.
        var textRole = first[0] is "textbox" or "searchbox" or "combobox" or "spinbutton";
        Assert.Equal(textRole ? "cupri-kbd" : "synced", first[2]);

        // DOM → engine. An AT moving its cursor onto a control focuses the node; the page forwards
        // that to AccessibilityFocus, and the engine's next publish agrees about who has focus.
        await page.EvaluateAsync($"() => document.querySelector('{DarkMode}').focus()");
        await page.WaitForFunctionAsync(
            $"() => document.querySelector('{DarkMode}').hasAttribute('data-focused')", null, new() { Timeout = 5_000 });

        // Enter on the node the AT focused operates it (Space is the same path).
        var sw = page.Locator(DarkMode);
        var before = await sw.GetAttributeAsync("aria-checked");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(sw).ToHaveAttributeAsync("aria-checked", before == "true" ? "false" : "true", new() { Timeout = 5_000 });

        // The republish that carried the flip patched the tree in place: focus is still on the
        // switch. Replacing innerHTML would have dropped it to <body>, and a screen reader would
        // have gone silent mid-interaction.
        var still = await page.EvaluateAsync<bool>($"() => document.activeElement === document.querySelector('{DarkMode}')");
        Assert.True(still, "the republish tore DOM focus off the switch");

        // A clipboard chord while an overlay node holds focus goes back to the textarea, where the
        // native copy/cut/paste listeners are — otherwise Ctrl+C after tabbing to a button is lost.
        await page.EvaluateAsync("() => { window.__copied = 0; document.getElementById('cupri-kbd').addEventListener('copy', () => window.__copied++); }");
        await page.Keyboard.PressAsync("Control+c");
        var afterChord = await page.EvaluateAsync<string>("() => document.activeElement && document.activeElement.id");
        var copied = await page.EvaluateAsync<int>("() => window.__copied");
        output.WriteLine($"after Ctrl+C: focus on '{afterChord}', copy events on the textarea: {copied}");
        Assert.Equal("cupri-kbd", afterChord);
    }

    [Fact]
    public async Task A_slider_answers_a_value_written_by_path()
    {
        var page = await ArriveAsync(host);
        // The overlay's third action, RangeValue.SetValue's equivalent. No DOM event carries it (an
        // AT drives a slider with arrow keys, which reach the engine through the ordinary key path),
        // so it is driven through the automation contract both hosts publish.
        var slider = await page.EvaluateAsync<string[]>($$"""
            () => { const s = document.querySelector('{{Mirror}} [role="slider"]:not([aria-hidden])');
                    return s ? [s.getAttribute('data-path'), s.getAttribute('aria-valuenow'), s.getAttribute('aria-valuemin') || '0', s.getAttribute('aria-valuemax') || '100'] : null; }
            """);
        Assert.NotNull(slider);
        var (path, now, min, max) = (slider![0], double.Parse(slider[1]), double.Parse(slider[2]), double.Parse(slider[3]));
        var target = Math.Abs(now - min) > Math.Abs(max - now) ? min : max;   // whichever end is further
        output.WriteLine($"slider {path}: {now} in [{min},{max}] -> {target}");

        await page.EvaluateAsync("([p, v]) => globalThis.__cupri.a11yAct.setValue(p, v)", new object[] { path, target });
        await page.WaitForFunctionAsync(
            "([m, p, v]) => { const s = document.querySelector(m + ' [data-path=\"' + p + '\"]'); return s && Number(s.getAttribute('aria-valuenow')) === v; }",
            new object[] { Mirror, path, target }, new() { Timeout = 5_000 });
    }

    /// <summary>Mean brightness of a patch of the page's content area, read back from the canvas —
    /// the only thing that proves the MODEL changed rather than an attribute in the mirror.</summary>
    private static Task<double> ContentBrightnessAsync(IPage page) => page.EvaluateAsync<double>("""
        () => { const c = document.getElementById('cupri'); const d = c.getContext('2d').getImageData(c.width - 120, c.height - 120, 100, 100).data;
                let s = 0; for (let i = 0; i < d.length; i += 4) s += (d[i] + d[i + 1] + d[i + 2]) / 3; return s / (d.length / 4); }
        """);
}
