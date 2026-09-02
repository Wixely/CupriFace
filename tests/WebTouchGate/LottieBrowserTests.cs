using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.WebTouchGate;

/// <summary>
/// Lottie in a REAL browser.
///
/// Everything else about the web story was inferred: the entry points are in the WASM archive, the
/// NativeAOT link succeeds, the payload delta is measured. None of that is the same as pixels moving
/// on a canvas in Chromium — a clean link proves the symbols resolve, not that Skottie runs under
/// Emscripten. This is the test that closes that gap.
///
/// Point it at a published Lottie build:
///   dotnet publish samples/WebLlvmLottie/WebLlvmLottie.csproj -c Release -o out
///   CUPRI_WEB_WWWROOT=out CUPRI_WEB_HOST=llvm dotnet test tests/WebTouchGate
/// </summary>
[Collection("web")]
public class LottieBrowserTests(WebHostFixture host, ITestOutputHelper output)
{
    /// <summary>Read the canvas back as a data URL and reduce it to a comparable fingerprint. Pixels
    /// rather than DOM, because the animation IS pixels — there is no element to query.</summary>
    private static async Task<string> CanvasFingerprintAsync(IPage page) =>
        await page.EvaluateAsync<string>(
            "() => { const c = document.getElementById('cupri');" +
            "        return c.toDataURL('image/png').slice(-6000); }");

    /// <summary>Count canvas pixels close to CupriFace's copper (#B87333), which is the only thing on
    /// the page that colour — so finding it means the animation rendered, not that something painted.</summary>
    private static async Task<int> CopperPixelsAsync(IPage page) =>
        await page.EvaluateAsync<int>(
            "() => { const c = document.getElementById('cupri');" +
            "  const g = c.getContext('2d'); if (!g) return -1;" +
            "  const d = g.getImageData(0, 0, c.width, c.height).data; let n = 0;" +
            "  for (let i = 0; i < d.length; i += 4) {" +
            "    if (Math.abs(d[i]-184) < 40 && Math.abs(d[i+1]-115) < 40 && Math.abs(d[i+2]-51) < 40) n++;" +
            "  } return n; }");

    [Fact]
    public async Task The_animation_renders_and_keeps_moving_in_a_browser()
    {
        var page = await host.PhoneAsync();

        // A crash in Skottie under Emscripten shows up here rather than as a failed assertion.
        var errors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error") errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);

        // Give the animation a moment to produce frames beyond the first paint.
        await page.WaitForTimeoutAsync(1200);

        var copper = await CopperPixelsAsync(page);
        output.WriteLine($"[{host.Host}] copper pixels on the canvas: {copper}");
        Assert.True(copper > 200,
            $"[{host.Host}] the spinner should be on the canvas; found {copper} copper pixels. " +
            "Zero means Skottie produced no frames, which a clean link would not have caught.");

        // …and it must still be MOVING. A single rendered frame would satisfy the count above, and a
        // player that rendered once and stopped is the failure mode worth catching here.
        var a = await CanvasFingerprintAsync(page);
        await page.WaitForTimeoutAsync(400);
        var b = await CanvasFingerprintAsync(page);
        output.WriteLine($"[{host.Host}] fingerprints equal across 400 ms: {a == b}");
        Assert.True(a != b, $"[{host.Host}] the canvas did not change over 400 ms — the animation " +
                            "rendered but is not running");

        Assert.True(errors.Count == 0, $"[{host.Host}] console errors: {string.Join(" | ", errors)}");
    }
}
