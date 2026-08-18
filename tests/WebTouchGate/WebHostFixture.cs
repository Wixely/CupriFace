using System.Diagnostics;
using Microsoft.Playwright;
using Xunit;

namespace CupriFace.WebTouchGate;

/// <summary>
/// Serves a PUBLISHED WebWasm build and opens it in a real Chromium with touch emulation.
///
/// The app is served by this repo's own <c>tools/Serve</c> — the managed static server that
/// replaced a Node script — so the gate exercises the same thing a developer's F5 does.
/// </summary>
public sealed class WebHostFixture : IAsyncLifetime
{
    private Process? _server;
    private IPlaywright? _pw;
    public IBrowser Browser { get; private set; } = null!;
    public string Url { get; private set; } = "";
    /// <summary>Which web host this run is driving — named in assertions so a matrix failure
    /// says which leg broke.</summary>
    public string Host { get; private set; } = "wasm";

    // Repo root, found by walking up to the file that only the root has.
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriFace.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    public async Task InitializeAsync()
    {
        var root = Root();

        // The published app. CI publishes it in the job step; locally, publish once yourself:
        //   dotnet publish samples/WebWasm/WebWasm.csproj -c Release
        Host = Environment.GetEnvironmentVariable("CUPRI_WEB_HOST") ?? "wasm";
        var wwwroot = Environment.GetEnvironmentVariable("CUPRI_WEB_WWWROOT")
                      ?? Path.Combine(root, "samples", "WebWasm", "bin", "Release", "net10.0", "publish", "wwwroot");
        if (!Directory.Exists(wwwroot))
            throw new DirectoryNotFoundException(
                $"No published web build at {wwwroot}. Publish WebWasm first, or set CUPRI_WEB_WWWROOT.");

        var port = 5391;
        Url = $"http://127.0.0.1:{port}/";
        _server = Process.Start(new ProcessStartInfo("dotnet",
            $"run --project \"{Path.Combine(root, "tools", "Serve")}\" -- \"{wwwroot}\" {port}")
        { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true });

        // Wait for it to ANSWER, and fail with the server's own output if it never does — a
        // fixture that proceeds past a dead server produces four identical mystery failures.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var up = false;
        for (var i = 0; i < 60 && !up; i++)
        {
            try { up = (await http.GetAsync(Url)).IsSuccessStatusCode; } catch { /* not up yet */ }
            if (!up) await Task.Delay(1000);
        }
        if (!up)
        {
            var so = _server is null ? "" : await _server.StandardOutput.ReadToEndAsync();
            var se = _server is null ? "" : await _server.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(string.Join(Environment.NewLine,
                $"the static server never answered on {Url}",
                $"serving: {wwwroot}",
                $"stdout: {so}",
                $"stderr: {se}"));
        }

        _pw = await Playwright.CreateAsync();
        // CI runs `playwright install chromium`, which puts the exact build this package expects
        // where it looks — nothing to configure. The two overrides are for a developer who already
        // has a browser and would rather not download a second copy: CUPRI_BROWSER_PATH points at
        // any Chromium binary, CUPRI_BROWSER_CHANNEL uses an installed channel (chrome/msedge).
        var exe = Environment.GetEnvironmentVariable("CUPRI_BROWSER_PATH");
        var channel = Environment.GetEnvironmentVariable("CUPRI_BROWSER_CHANNEL");
        Browser = await _pw.Chromium.LaunchAsync(new()
        {
            Headless = true,
            ExecutablePath = string.IsNullOrWhiteSpace(exe) ? null : exe,
            Channel = string.IsNullOrWhiteSpace(channel) ? null : channel,
        });
    }

    /// <summary>A page emulating a phone: real touch, no mouse.</summary>
    public async Task<IPage> PhoneAsync()
    {
        var ctx = await Browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 412, Height = 839 },
            HasTouch = true,
            IsMobile = true,
            DeviceScaleFactor = 2.625f,
        });
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(Url);
        // The engine boots, then paints. Wait for the canvas to carry pixels rather than for a
        // fixed delay — a slow runner would otherwise fail for no reason.
        // Both web hosts publish the same automation contract (__cupri.isCoarse), so this gate
        // drives either without knowing whether it is talking to JSExports (raw WASM) or to
        // Emscripten's module (NativeAOT-LLVM).
        await page.WaitForFunctionAsync(
            "() => { const c = document.getElementById('cupri');" +
            "  return c && c.width > 0 && typeof (globalThis.__cupri||{}).isCoarse === 'function'; }",
            null, new() { Timeout = 180_000 });
        return page;
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _pw?.Dispose();
        try { if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true); } catch { }
        _server?.Dispose();
    }
}

[CollectionDefinition("web")]
public sealed class WebCollection : ICollectionFixture<WebHostFixture> { }
