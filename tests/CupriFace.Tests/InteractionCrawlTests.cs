using CupriFace.Components;
using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>InteractionCrawl.Click</c>: every control clicked, each on a fresh document, every check run
/// after each — the states a click HANDLER produces, which no flag in the markup names.
/// </summary>
public class InteractionCrawlTests(ITestOutputHelper output)
{
    // The handler is the only way in: no bool on the model opens the panel. Clicking "Show details"
    // sets a string that reveals a fixed-height panel too small for its rows (CF0070).
    private sealed class Model
    {
        public string Details { get; set; } = "none";
        public int Clicks { get; set; }
    }

    private const string Html =
        "<body><div class='bar'><cupri-button class='show'>Show details</cupri-button><cupri-button class='count'>Count</cupri-button></div>"
        + "<div class='panel' style='display:{{Details}}'><div class='r'>one</div><div class='r'>two</div><div class='r'>three</div></div></body>";
    private const string Css = "body{margin:0;font-family:sans-serif} .bar{display:flex;gap:8px} .panel{height:30px} .r{height:20px}";

    private static CupriDocument Fresh(Model model)
    {
        var doc = CupriDocument.Load(Html, Css);
        doc.UseComponents(ComponentRegistry.Default());
        doc.Bind(model);
        doc.OnClick(".show", _ => model.Details = "block");
        doc.OnClick(".count", _ => model.Clicks++);
        return doc;
    }

    [Fact]
    public void A_fault_only_a_handler_reveals_is_found_and_stamped_with_the_click()
    {
        var model = new Model();
        var plain = CupriDoctor.Check(Html, Css, width: 400, height: 300, model: model);
        Assert.DoesNotContain(plain.Findings, f => f.Code == "CF0070");
        Assert.Empty(CupriDoctor.DiscoverFlags(Html, model));                 // nothing for a sweep to flip

        var report = InteractionCrawl.Click(() => Fresh(new Model()), 400, 300);
        output.WriteLine(report.ToString());

        var f = Assert.Single(report.Findings, x => x.Code == "CF0070");
        Assert.Equal("after clicking \"Show details\"", f.State);
        Assert.Contains("[after clicking \"Show details\"]", f.ToString());
        Assert.Equal(1 + 2, report.Checks);                                    // as it loads, then each button
    }

    [Fact]
    public void Each_click_lands_on_a_fresh_document()
    {
        var builds = 0;
        var seen = new List<Model>();
        var report = InteractionCrawl.Click(() => { builds++; var m = new Model(); seen.Add(m); return Fresh(m); }, 400, 300);
        Assert.Equal(3, builds);
        // The second button's document never saw the first button's click.
        Assert.All(seen, m => Assert.True(m.Details == "none" || m.Clicks == 0));
        Assert.Equal(3, report.Checks);
    }

    [Fact]
    public void The_clicks_can_be_filtered_and_capped()
    {
        var filtered = InteractionCrawl.Click(() => Fresh(new Model()), 400, 300,
                                              new CrawlOptions { Where = n => n.Contains("Count") });
        Assert.Equal(2, filtered.Checks);
        Assert.DoesNotContain(filtered.Findings, f => f.Code == "CF0070");

        var capped = InteractionCrawl.Click(() => Fresh(new Model()), 400, 300, new CrawlOptions { MaxClicks = 1 });
        Assert.Equal(2, capped.Checks);
    }

    [Fact]
    public void A_finding_present_as_the_document_loads_carries_no_click()
    {
        var report = InteractionCrawl.Click(() => Fresh(new Model { Details = "block" }), 400, 300);
        var f = Assert.Single(report.Findings, x => x.Code == "CF0070");
        Assert.Equal("", f.State);
    }

    /// <summary>The shape an app's test takes: the real factory, the real handlers, a phone.</summary>
    [Fact]
    public void The_shipped_showcase_can_be_crawled()
    {
        var app = new Demo.ShowcaseApp("overlays");
        var report = InteractionCrawl.Click(app.CreateDocument, 412, 915, new CrawlOptions { MaxClicks = 12 });
        output.WriteLine($"{report.Checks} checks");
        output.WriteLine(report.ToString());
        Assert.True(report.Checks > 1, "no control was clicked");
        Assert.False(report.HasErrors, report.ToString());
    }
}
