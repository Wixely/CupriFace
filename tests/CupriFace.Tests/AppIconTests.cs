using CupriFace.Resources;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <see cref="CupriApp.Icon"/> is the RUNTIME icon — the one a host puts on a window, a browser
/// tab or an Android recents card. Every host reaches it through the same two members, so these
/// pin the contract the hosts rely on rather than any one host's use of it.
/// </summary>
public class AppIconTests
{
    private sealed class NoIconApp : CupriApp
    {
        public override string Html => "<body></body>";
    }

    private sealed class IconApp : CupriApp
    {
        public IconApp(byte[] icon) => _icon = icon;
        private readonly byte[] _icon;
        public override string Html => "<body></body>";
        public override byte[] Icon => _icon;
    }

    // The first eight bytes of any PNG; JPEG starts FF D8 FF instead.
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void No_icon_means_no_data_uri()
    {
        // Not an empty string: a host tests for null to decide whether to touch the tab icon at all.
        Assert.Null(new NoIconApp().IconDataUri);
    }

    [Fact]
    public void Png_bytes_are_announced_as_png()
    {
        var uri = new IconApp(PngMagic).IconDataUri;
        Assert.NotNull(uri);
        Assert.StartsWith("data:image/png;base64,", uri);
        Assert.Equal(Convert.ToBase64String(PngMagic), uri!["data:image/png;base64,".Length..]);
    }

    [Fact]
    public void Jpeg_bytes_are_not_announced_as_png()
    {
        // Sniffed, not assumed. A JPEG served as image/png is the kind of thing a browser forgives
        // and a stricter consumer does not.
        var uri = new IconApp([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]).IconDataUri;
        Assert.StartsWith("data:image/jpeg;base64,", uri);
    }

    [Fact]
    public void A_runt_icon_is_treated_as_absent()
    {
        // Too short to carry a signature — there is nothing to sniff and nothing worth showing.
        Assert.Null(new IconApp([0x89, 0x50]).IconDataUri);
        Assert.Null(new IconApp([]).IconDataUri);
    }

    [Fact]
    public void The_demo_app_ships_a_real_icon_on_both_desktop_and_mobile()
    {
        // Both sample apps carry one, and it decodes: the Android recents card and the web favicon
        // are only as good as the bytes behind them, and an embedded-resource typo is silent until
        // a host reads it.
        foreach (CupriApp app in new CupriApp[] { new Demo.ShowcaseApp(), new Demo.MobileApp() })
        {
            var bytes = app.Icon;
            Assert.NotNull(bytes);
            Assert.True(bytes!.Length > 8, $"{app.GetType().Name}: icon is {bytes.Length} bytes");
            Assert.Equal(PngMagic, bytes[..8]);
            Assert.StartsWith("data:image/png;base64,", app.IconDataUri);

            using var decoded = SkiaSharp.SKBitmap.Decode(bytes);
            Assert.NotNull(decoded);      // the desktop host decodes it exactly like this
        }
    }
}
