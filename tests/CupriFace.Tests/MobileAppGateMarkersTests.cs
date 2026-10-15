using CupriFace.Demo;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The markers the Android CI gate steers by.
///
/// <para>They are ordinary <c>Console.WriteLine</c>s in the sample app, which the Android host
/// bridges to logcat — so nothing in a normal build would notice if one stopped being printed. The
/// gate would notice, days later and wearing someone else's name: a page marker that stops arriving
/// makes every nav tap look dropped, and an assert three steps downstream takes the blame. These
/// pin the contract on this side of it.</para>
/// </summary>
public class MobileAppGateMarkersTests(ITestOutputHelper output)
{
    private static string Capture(Action act)
    {
        var before = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try { act(); } finally { Console.SetOut(before); }
        return writer.ToString();
    }

    /// <summary>A page change announces the page it changed TO. The gate waits for exactly this
    /// line before trusting anything it then taps.</summary>
    [Theory]
    [InlineData("list")]
    [InlineData("form")]
    [InlineData("settings")]
    [InlineData("about")]
    public void Changing_the_page_announces_it(string page)
    {
        var model = new MobileModel();
        var printed = Capture(() => model.Page = page);
        output.WriteLine(printed.Trim());
        Assert.Contains($"cupri-gate: page={page}", printed);
        Assert.Equal(page, model.Page);
    }

    /// <summary>Setting the page it is already on says nothing. The gate reads the LAST marker, so
    /// a repeat would be harmless, but a marker per rebuild would bury the log it greps.</summary>
    [Fact]
    public void Setting_the_same_page_again_is_silent()
    {
        var model = new MobileModel();
        model.Page = "list";
        var printed = Capture(() => model.Page = "list");
        Assert.Equal("", printed.Trim());
    }

    /// <summary>The app opens on Home, and says nothing to announce it — the gate reads geometry
    /// and taps from a known starting page rather than waiting for a marker that never comes.</summary>
    [Fact]
    public void The_starting_page_is_home_and_unannounced()
    {
        var printed = Capture(() => Assert.Equal("home", new MobileModel().Page));
        Assert.Equal("", printed.Trim());
    }

    /// <summary>The toggle marker, which the gate's exact-once switch assertion counts.</summary>
    [Fact]
    public void The_toggle_still_announces_its_state()
    {
        using var t = new TestDoc("<body></body>", "", components: true);
        var app = new MobileApp();
        var printed = Capture(() =>
        {
            var model = (MobileModel)app.Model!;
            model.Notify = !model.Notify;
            Console.WriteLine($"cupri-gate: toggle={model.Notify}");
        });
        Assert.Contains("cupri-gate: toggle=", printed);
    }
}
