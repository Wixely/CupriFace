using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.WebTouchGate;

/// <summary>
/// The minimal web build (#325), in a REAL browser: samples/WebLlvmShaders, which registers only the
/// two controls it uses and publishes without the host's video, file drop or bundled bold face.
///
/// <para>A trimmed binary that links is not a binary that works — the trimmer removes what it
/// cannot see a use of, and a feature switch folded the wrong way fails at run time, in the page.
/// So what the minimal build keeps is checked here against the published output: it boots and
/// paints with no console error, the shaders animate, the bound controls are there and answer, and
/// text still has its sans face. What it drops is checked by CI against the link (the minimal-build
/// step in the WebLlvm job), where an absence can be measured.</para>
///
/// <para>Point it at the published sample:</para>
/// <code>
///   dotnet publish samples/WebLlvmShaders -c Release -o out
///   CUPRI_WEB_WWWROOT=out CUPRI_WEB_HOST=llvm CUPRI_WEB_MINIMAL=1 dotnet test tests/WebTouchGate --filter MinimalBuildTests
/// </code>
/// </summary>
[Collection("web")]
public class MinimalBuildTests(WebHostFixture host, ITestOutputHelper output)
{
    private const string Slider = "#cupri-a11y [role=\"slider\"]";
    private const string Switch = "#cupri-a11y [role=\"switch\"]";

    private static async Task<string> CanvasFingerprintAsync(IPage page) =>
        await page.EvaluateAsync<string>(
            "() => document.getElementById('cupri').toDataURL('image/png').slice(-6000)");

    [MinimalBuildFact]
    public async Task It_boots_animates_and_its_bound_controls_answer()
    {
        var console = new List<string>();
        var errors = new List<string>();
        var page = await host.DesktopAsync(onPage: p =>
        {
            p.Console += (_, m) => { console.Add(m.Text); if (m.Type == "error") errors.Add(m.Text); };
            p.PageError += (_, e) => errors.Add(e);
            // ShadersApp has no icon, so a browser that asks for /favicon.ico (an installed Edge or
            // Chrome does; CI's headless shell does not) logs a 404 that has nothing to do with the
            // build. Answered here so the no-errors assertion means the app's own errors.
            p.RouteAsync("**/favicon.ico", r => r.FulfillAsync(new() { Status = 204 })).GetAwaiter().GetResult();
        });

        // The two controls the registry kept, expanded and bound: the slider reports the model's
        // Amount (4) through the mirror, which only an expanded, bound <cupri-slider> can do.
        await page.WaitForFunctionAsync($"() => document.querySelector('{Slider}') && document.querySelector('{Switch}')",
                                        null, new() { Timeout = 15_000 });
        Assert.Equal("4", await page.Locator(Slider).GetAttributeAsync("aria-valuenow"));

        // The banner is an animated shader: the canvas must keep changing, not paint once.
        var a = await CanvasFingerprintAsync(page);
        await page.WaitForTimeoutAsync(400);
        var b = await CanvasFingerprintAsync(page);
        Assert.True(a != b, "the canvas did not change over 400 ms — the shaders are not animating");

        // Input reaches the model and the binding comes back: the CRT switch flips.
        var sw = page.Locator(Switch);
        var before = await sw.GetAttributeAsync("aria-checked");
        await sw.DispatchEventAsync("click");
        await Assertions.Expect(sw).ToHaveAttributeAsync("aria-checked", before == "true" ? "false" : "true",
                                                         new() { Timeout = 5_000 });

        // CUPRI_WEB_SCREENSHOT=path saves what the browser drew, to look at the text: with
        // CupriWebFonts=regular its bold is synthesised, which no assertion here can judge.
        if (Environment.GetEnvironmentVariable("CUPRI_WEB_SCREENSHOT") is { Length: > 0 } shot)
            await page.ScreenshotAsync(new() { Path = shot });

        foreach (var line in console) output.WriteLine(line);
        // CupriWebFonts=regular keeps the sans face; only `none` would print this.
        Assert.DoesNotContain(console, l => l.Contains("no bundled sans face"));
        Assert.Contains(console, l => l.Contains("[CupriFace] renderer:"));
        Assert.True(errors.Count == 0, $"[{host.Host}] console errors: {string.Join(" | ", errors)}");
    }
}

/// <summary>Runs only when the gate is pointed at the minimal build; the default wwwroot is the
/// Showcase, which has neither the slider's value nor the animation this asserts.</summary>
internal sealed class MinimalBuildFactAttribute : FactAttribute
{
    public const string OptIn = "CUPRI_WEB_MINIMAL";

    public MinimalBuildFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(OptIn) != "1")
            Skip = $"set {OptIn}=1 and point CUPRI_WEB_WWWROOT at a published samples/WebLlvmShaders";
    }
}
