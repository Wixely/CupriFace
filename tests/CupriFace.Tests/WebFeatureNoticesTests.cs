using CupriFace.Web;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The console lines a web host prints when a page reaches for a part its build left out (#325).
/// Checked as functions because the host's own tests run with every feature on: a feature switch is
/// read once per process, so the "off" path is unreachable from WebHostCoreTests.
/// </summary>
public class WebFeatureNoticesTests
{
    private static AngleSharp.Dom.IDocument Dom(string html)
    {
        AngleSharp.Dom.IDocument? dom = null;
        var doc = CupriDocument.Load(html, "");
        doc.UseComponents(CupriFace.Components.ComponentRegistry.Default());   // the expanded control is what it looks for
        doc.OnRebuilt(d => dom = d);
        return dom!;
    }

    [Fact]
    public void A_page_with_a_video_hears_that_it_only_shows_its_poster()
    {
        var line = WebFeatureNotices.VideoOff(Dom("<body><cupri-video src='clip.webm'></cupri-video></body>"));
        Assert.NotNull(line);
        Assert.Contains("CupriWebVideo", line);
        Assert.Contains("poster", line);
    }

    [Fact]
    public void A_page_without_a_video_hears_nothing()
    {
        Assert.Null(WebFeatureNotices.VideoOff(Dom("<body><p>no video here</p></body>")));
    }

    [Fact]
    public void A_drag_over_a_document_with_a_drop_handler_names_the_switch()
    {
        var line = WebFeatureNotices.FileDropOff(documentAcceptsDrops: true);
        Assert.NotNull(line);
        Assert.Contains("CupriWebFileDrop", line);
    }

    [Fact]
    public void A_drag_over_a_document_that_takes_no_drops_is_not_reported()
    {
        // Refusing it is right whatever the build: there is nothing to explain.
        Assert.Null(WebFeatureNotices.FileDropOff(documentAcceptsDrops: false));
    }
}
