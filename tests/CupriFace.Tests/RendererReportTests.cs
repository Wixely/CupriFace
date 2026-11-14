using CupriFace.Demo;
using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <see cref="CupriDocument.Renderer"/>: every host tries the GPU first and falls back to the CPU by
/// itself, and this is the one place that says which happened and why. A fallback that only reaches
/// a console is a page that is mysteriously slow.
/// </summary>
public class RendererReportTests(ITestOutputHelper output)
{
    private const string Html = "<body><p>x</p></body>";

    [Fact]
    public void A_report_is_kept_with_its_history_and_repeats_are_ignored()
    {
        using var doc = CupriDocument.Load(Html, "");
        var raised = new List<RendererInfo>();
        doc.RendererChanged += raised.Add;

        Assert.Equal(RendererKind.Unknown, doc.Renderer.Kind);

        doc.ReportRenderer(new RendererInfo(RendererKind.Gpu, "WebGL2 — test GPU"));
        doc.ReportRenderer(new RendererInfo(RendererKind.Gpu, "WebGL2 — test GPU"));   // every frame, say
        doc.ReportRenderer(new RendererInfo(RendererKind.Cpu, "raster", "the context was lost"));

        Assert.Equal(RendererKind.Cpu, doc.Renderer.Kind);
        Assert.Equal("the context was lost", doc.Renderer.Reason);
        Assert.Equal(2, doc.RendererHistory.Count);
        Assert.Equal(2, raised.Count);
        Assert.Equal("Cpu: raster (the context was lost)", doc.Renderer.ToString());
    }

    [Fact]
    public void The_debug_dump_carries_the_renderer_and_its_history()
    {
        using var doc = CupriDocument.Load(Html, "");
        doc.ReportRenderer(new RendererInfo(RendererKind.Gpu, "WebGL2"));
        doc.ReportRenderer(new RendererInfo(RendererKind.Cpu, "raster", "a GPU frame failed"));

        using var json = System.Text.Json.JsonDocument.Parse(doc.DebugDump(200, 100));
        var root = json.RootElement;
        Assert.Equal("Cpu", root.GetProperty("renderer").GetProperty("kind").GetString());
        Assert.Equal("a GPU frame failed", root.GetProperty("renderer").GetProperty("reason").GetString());
        Assert.Equal(2, root.GetProperty("rendererHistory").GetArrayLength());
    }

    /// <summary>The Showcase's Diagnostics page shows it — and a fallback reason is a sentence, which
    /// must wrap on a phone rather than run off it or over the card beside it.</summary>
    [Fact]
    public void The_showcase_diagnostics_page_shows_the_renderer_and_survives_a_long_reason_on_a_phone()
    {
        var app = new ShowcaseApp();
        using var doc = app.CreateDocument();
        var model = (ShowcaseModel)app.Model!;
        model.Section = "diag";
        doc.ReportRenderer(new RendererInfo(RendererKind.Gpu, "WebGL2 — ANGLE (NVIDIA, NVIDIA GeForce GTX 980 Direct3D11 vs_5_0 ps_5_0), or similar"));
        doc.ReportRenderer(new RendererInfo(RendererKind.Cpu, "Skia raster in wasm, presented with putImageData",
            "the WebGL context was lost and the browser did not restore it within 3 s"));
        doc.Refresh();

        foreach (var (w, h) in new[] { (1280, 800), (412, 915) })
        {
            var p = app.Present(w, h);
            int lw = (int)p.LogicalWidth, lh = (int)p.LogicalHeight;
            using (doc.RenderToImage(lw, lh)) { }
            var report = CupriDoctor.Check(doc, lw, lh);
            var mine = report.Findings.Where(f => f.Message.Contains("diag-")).ToList();
            output.WriteLine($"{w}x{h}: {report.Findings.Count} findings, {mine.Count} on the renderer cards");
            foreach (var f in mine) output.WriteLine("  " + f);
            Assert.Empty(mine);

        }

        Assert.Contains("Cpu: Skia raster in wasm", model.RendererNow);
        Assert.Contains("did not restore it within 3 s", model.RendererNow);
        Assert.Contains("Gpu", model.RendererLog);
        Assert.Contains("Cpu", model.RendererLog);
    }
}
