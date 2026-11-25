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

    // ---- the paths a page meets after it has booted: a resize, the CPU, a GPU that goes away -----

    private sealed record Booted(IPage Page, List<string> Console, List<string> Errors);

    private async Task<Booted> BootAsync(string? query = null)
    {
        var console = new List<string>();
        var errors = new List<string>();
        var page = await host.DesktopAsync(onPage: p =>
        {
            p.Console += (_, m) => { console.Add(m.Text); if (m.Type == "error") errors.Add(m.Text); };
            p.PageError += (_, e) => errors.Add(e);
            p.RouteAsync("**/favicon.ico", r => r.FulfillAsync(new() { Status = 204 })).GetAwaiter().GetResult();
        }, query: query);
        await page.WaitForFunctionAsync($"() => document.querySelector('{Switch}')", null, new() { Timeout = 15_000 });
        return new(page, console, errors);
    }

    private void Done(Booted b)
    {
        foreach (var line in b.Console) output.WriteLine(line);
        Assert.True(b.Errors.Count == 0, $"[{host.Host}] console errors: {string.Join(" | ", b.Errors)}");
    }

    // Frames the page has painted. Counted on both renderers (main.js: "__paints() means the same
    // thing on both"), so "still animating" needs no pixels read back — a WebGL canvas without
    // preserveDrawingBuffer reads back blank, which would prove nothing either way.
    private static async Task StillPaintingAsync(IPage page, string when)
    {
        var a = await page.EvaluateAsync<int>("() => globalThis.__cupri.paints");
        try
        {
            await page.WaitForFunctionAsync($"() => globalThis.__cupri.paints > {a + 5}", null,
                                            new() { Timeout = 10_000 });
        }
        catch (TimeoutException)
        {
            Assert.Fail($"the page stopped painting {when}: {await page.EvaluateAsync<int>("() => globalThis.__cupri.paints")} paints, from {a}");
        }
    }

    private static async Task SwitchStillAnswersAsync(IPage page)
    {
        var sw = page.Locator(Switch);
        var before = await sw.GetAttributeAsync("aria-checked");
        await sw.DispatchEventAsync("click");
        await Assertions.Expect(sw).ToHaveAttributeAsync("aria-checked", before == "true" ? "false" : "true",
                                                         new() { Timeout = 5_000 });
    }

    private static Task<string?> RendererAsync(IPage page) =>
        page.EvaluateAsync<string?>("() => globalThis.__cupri.renderer");

    private static Task<string?> CurrentAsync(IPage page, string field) =>
        page.EvaluateAsync<string?>($"() => (globalThis.__cupri.rendererState() || {{}}).current?.{field} ?? null");

    /// <summary>A window resized to a phone's width: the backing store follows, the layout re-flows
    /// into it (the controls' mirror nodes now sit inside 412px), and the page keeps painting and
    /// answering. The resize path is the host's, not the app's, so the minimal build has to prove
    /// it kept it.</summary>
    [MinimalBuildFact]
    public async Task A_resize_re_lays_out_and_it_keeps_answering()
    {
        var b = await BootAsync();
        await b.Page.SetViewportSizeAsync(412, 780);
        await b.Page.WaitForFunctionAsync(
            "() => { const c = document.getElementById('cupri'); return c.clientWidth === 412 && c.width === 412; }",
            null, new() { Timeout = 10_000 });
        await b.Page.WaitForFunctionAsync(
            $"() => {{ const r = document.querySelector('{Switch}').getBoundingClientRect(); return r.right <= 412 && r.width > 0; }}",
            null, new() { Timeout = 10_000 });

        await StillPaintingAsync(b.Page, "after the resize");
        await SwitchStillAnswersAsync(b.Page);
        Done(b);
    }

    /// <summary>The CPU renderer, asked for by name: what every browser without a usable GPU gets.
    /// It boots, animates and answers, and the engine records why it is on the CPU.</summary>
    [MinimalBuildFact]
    public async Task On_the_CPU_renderer_it_boots_animates_and_answers()
    {
        var b = await BootAsync("renderer=cpu");
        Assert.Equal("cpu", await RendererAsync(b.Page));
        Assert.Equal("Cpu", await CurrentAsync(b.Page, "kind"));
        Assert.Contains("?renderer=cpu", await CurrentAsync(b.Page, "reason") ?? "");

        await StillPaintingAsync(b.Page, "on the CPU");
        await SwitchStillAnswersAsync(b.Page);
        Done(b);
    }

    // On the GPU, by way of ?renderer=webgl, which also accepts the software WebGL a headless
    // browser has. Fails rather than skips without one: a gate that quietly stopped testing the GPU
    // would look exactly like one that passed.
    private async Task<Booted> OnTheGpuAsync()
    {
        var b = await BootAsync("renderer=webgl");
        Assert.True(await RendererAsync(b.Page) == "webgl",
            $"the page did not start on WebGL ({await CurrentAsync(b.Page, "reason")}), so the GPU paths cannot be checked");
        await StillPaintingAsync(b.Page, "on the GPU");
        return b;
    }

    private static Task LoseContextAsync(IPage page) => page.EvaluateAsync(
        "() => { const gl = document.getElementById('cupri').getContext('webgl2');" +
        "        globalThis.__lose = gl.getExtension('WEBGL_lose_context'); globalThis.__lose.loseContext(); }");

    /// <summary>A GPU reset that the browser recovers from: painting stops while the context is
    /// gone (the engine says why), then the host builds a new Skia context on the restored one and
    /// carries on on the GPU, where it was.</summary>
    [MinimalBuildFact]
    public async Task A_lost_GPU_context_that_comes_back_keeps_the_GPU()
    {
        var b = await OnTheGpuAsync();
        await LoseContextAsync(b.Page);
        await b.Page.WaitForFunctionAsync(
            "() => (globalThis.__cupri.rendererState().current.reason || '').includes('was lost')",
            null, new() { Timeout = 5_000 });

        await b.Page.EvaluateAsync("() => globalThis.__lose.restoreContext()");
        await b.Page.WaitForFunctionAsync(
            "() => globalThis.__cupri.renderer === 'webgl' && globalThis.__cupri.rendererState().current.reason === null",
            null, new() { Timeout = 5_000 });

        await StillPaintingAsync(b.Page, "after the restore");
        await SwitchStillAnswersAsync(b.Page);
        Done(b);
    }

    /// <summary>A GPU that never comes back: after the page's grace period (3 s) the host gives up on
    /// it, swaps in a fresh canvas and carries on on the CPU, mid-run, with the reason recorded.
    /// The controls are the same controls afterwards — the document never noticed.</summary>
    [MinimalBuildFact]
    public async Task A_lost_GPU_context_that_never_comes_back_falls_back_to_the_CPU()
    {
        var b = await OnTheGpuAsync();
        await LoseContextAsync(b.Page);
        await b.Page.WaitForFunctionAsync("() => globalThis.__cupri.renderer === 'cpu'", null,
                                          new() { Timeout = 15_000 });
        Assert.Equal("Cpu", await CurrentAsync(b.Page, "kind"));
        Assert.Contains("was lost", await CurrentAsync(b.Page, "reason") ?? "");

        await StillPaintingAsync(b.Page, "after falling back");
        await SwitchStillAnswersAsync(b.Page);
        Done(b);
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
