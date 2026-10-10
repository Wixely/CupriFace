using Microsoft.Playwright;
using Xunit;

namespace CupriFace.WebTouchGate;

/// <summary>
/// The devicePixelRatio ceiling, measured on the canvas a real browser actually allocated.
///
/// <para>This is the one seam the managed tests cannot reach. <c>WebHostCoreTests</c> asserts what
/// the engine ANSWERS; what matters to a user is the size of the backing store the PAGE then
/// allocated, and those are two different programs talking over an interop boundary. The failure
/// mode they cannot see is the page and the engine disagreeing — draw at one scale into a buffer
/// sized for another and the compositor stretches the difference, which is #218 and reads as soft
/// text rather than as an error.</para>
///
/// <para>Both hosts publish the same export, so this gate drives either without knowing which.</para>
/// </summary>
[Collection("web")]
public class PixelRatioTests(WebHostFixture host)
{
    // Canvas backing store, CSS box, and the ratio the page actually applied between them.
    private const string MeasureFn = """
        () => { const c = document.getElementById('cupri');
                return c.width / c.clientWidth; }
        """;

    /// <summary>The page allocates at the ceiling the ENGINE named, not at the raw ratio. The phone
    /// fixture reports 2.625, which uncapped is 6.9x the pixels of a 1x buffer — and on the
    /// interpreted host that is the difference between momentum running and momentum not happening.
    /// The default is 2 and it is deliberately the same on both hosts (#227): the compiled host can
    /// afford 3, but an app that looks sharper depending on which runtime painted it is a divergence
    /// nobody asked for, so the engine ships one number and lets a caller override it per device.</summary>
    [Fact]
    public async Task The_page_sizes_its_canvas_at_the_ceiling_the_engine_named()
    {
        var page = await host.PhoneAsync();

        Assert.Equal(2.625, await page.EvaluateAsync<double>("() => window.devicePixelRatio"), 3);
        Assert.Equal(2.0, await page.EvaluateAsync<double>(MeasureFn), 3);
    }

    /// <summary>The ceiling is re-asked when the canvas is sized, not cached from boot — which is what
    /// makes a per-device answer possible at all (#227). Asserted by RESIZING: a cached ceiling and a
    /// live one agree on this app, so what is being checked is that the resize path still produces a
    /// correctly-sized buffer rather than one scaled from the old box.</summary>
    [Fact]
    public async Task A_resize_re_sizes_the_backing_store_at_the_ceiling()
    {
        var page = await host.PhoneAsync();
        await page.SetViewportSizeAsync(360, 640);
        // Wait for the BACKING STORE, not just the CSS box. clientWidth reaches 360 as soon as the
        // browser has laid the page out, while the buffer is resized by the engine on its next
        // frame — and between those two moments the ratio is the OLD buffer over the NEW box. That
        // is 824/360 = 2.2889 on this fixture (412 CSS at the ceiling of 2), which is exactly what
        // this test failed with once before the wait was widened.
        await page.WaitForFunctionAsync(
            "() => { const c = document.getElementById('cupri'); return c.clientWidth === 360 && c.width === 720; }");

        Assert.Equal(2.0, await page.EvaluateAsync<double>(MeasureFn), 3);
        Assert.Equal(720, await page.EvaluateAsync<int>("() => document.getElementById('cupri').width"));
    }
}
