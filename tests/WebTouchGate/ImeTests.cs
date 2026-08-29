using Microsoft.Playwright;
using Xunit;

namespace CupriFace.WebTouchGate;

/// <summary>
/// The IME placement contract, driven by a real browser, on WHICHEVER host the matrix is running.
///
/// The canvas is opaque to an IME, so the host keeps a hidden textarea and must move it to the
/// caret — otherwise a candidate window opens at the page's top-left instead of at the field being
/// typed into, and a touch keyboard never learns that a numeric field wants digits.
///
/// This exists because the two web hosts drifted: the Mono host pushed this state from its paint
/// path and the NativeAOT-LLVM host had no such call at all, which nothing caught because nothing
/// asserted it (#77). Running for both hosts is the point — a gap that exists in one and not the
/// other should fail here, not be discovered by someone typing Japanese into a real app.
/// </summary>
[Collection("web")]
public class ImeTests(WebHostFixture host)
{
    /// <summary>Tab until the engine reports a focused text input, so the test needs no coordinates
    /// and works identically on either host.</summary>
    private static async Task<bool> TabToATextFieldAsync(IPage page)
    {
        for (var i = 0; i < 25; i++)
        {
            await page.Keyboard.PressAsync("Tab");
            await page.WaitForTimeoutAsync(60);
            var mode = await page.EvaluateAsync<string?>(
                "() => (document.getElementById('cupri-kbd') || {}).inputMode || ''");
            if (mode is "text" or "numeric") return true;
        }
        return false;
    }

    [Fact]
    public async Task Focusing_a_field_moves_the_hidden_textarea_to_the_caret()
    {
        var page = await host.PhoneAsync();

        // Before anything is focused the host has nothing to report, so the textarea sits at the
        // origin its stylesheet gave it. That is the baseline the move has to beat.
        var found = await TabToATextFieldAsync(page);
        Assert.True(found, $"[{host.Host}] tabbing never reached a text input — the host never told " +
                           "JS that a field had focus, so an IME would open at the page origin");

        // Read as one delimited string: Playwright cannot marshal a tuple.
        var raw = await page.EvaluateAsync<string>("""
            () => { const k = document.getElementById('cupri-kbd');
                    return [k.style.left || '', k.style.top || '', k.inputMode || ''].join('|'); }
            """);
        var parts = raw.Split('|');
        var (left, top, mode) = (parts[0], parts[1], parts[2]);

        Assert.True(mode is "text" or "numeric",
            $"[{host.Host}] inputmode was '{mode}' — a touch keyboard cannot pick a layout");

        // The caret is somewhere inside the canvas, never at its very corner, so a real position
        // has been pushed rather than the stylesheet's 0,0.
        Assert.True(left.EndsWith("px") && top.EndsWith("px"),
            $"[{host.Host}] the textarea was never positioned (left='{left}', top='{top}')");
        var x = float.Parse(left[..^2]);
        var y = float.Parse(top[..^2]);
        Assert.True(x > 0 || y > 0,
            $"[{host.Host}] the textarea stayed at the page origin ({x},{y}) — the caret push never arrived");
    }
}
