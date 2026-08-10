using System.IO;
using CupriFace.Text;
using Xunit;

namespace CupriFace.Tests;

/// <summary>Hosts can register embedded fonts (doc.LoadFont) — the browser builds must, because the
/// wasm Skia ships only "Noto Mono", which silently rendered the whole UI monospaced. Registered faces
/// win over platform lookup and the first family becomes the generic sans target.</summary>
public class FontRegistrationTests
{
    private const string Fonts = @"c:\Users\dev\Git\CupriFace\samples\WebWasm\Assets\fonts";

    [Fact]
    public void Registered_font_takes_over_the_generic_sans_family()
    {
        using var fonts = new FontService();
        fonts.RegisterFont(File.ReadAllBytes(Path.Combine(Fonts, "NotoSans-Regular.ttf")));
        fonts.RegisterFont(File.ReadAllBytes(Path.Combine(Fonts, "NotoSans-Bold.ttf")));

        Assert.Equal("Noto Sans", fonts.GetTypeface("sans-serif", 400).FamilyName);
        Assert.Equal("Noto Sans", fonts.GetTypeface("system-ui", 400).FamilyName);   // alias too
        Assert.Equal("Noto Sans", fonts.GetTypeface("Noto Sans", 400).FamilyName);   // by name
        // Weight resolves to the registered bold face, not a synthetic.
        Assert.True(fonts.GetTypeface("sans-serif", 700).FontStyle.Weight >= 600);

        // And the result is genuinely proportional — the very defect this exists to fix.
        var i = fonts.MeasureText("sans-serif", 400, 32, "iiiiiiii");
        var m = fonts.MeasureText("sans-serif", 400, 32, "mmmmmmmm");
        Assert.True(m > i * 1.5f, $"proportional face expected (i={i:F1}, m={m:F1})");
    }

    [Fact]
    public void Monospace_stays_on_the_platform_face()
    {
        using var fonts = new FontService();
        fonts.RegisterFont(File.ReadAllBytes(Path.Combine(Fonts, "NotoSans-Regular.ttf")));
        // `code` must keep its monospace look — registration must not hijack the monospace family.
        Assert.NotEqual("Noto Sans", fonts.GetTypeface("monospace", 400).FamilyName);
    }

    [Fact]
    public void Without_registration_nothing_changes()
    {
        using var fonts = new FontService();
        var tf = fonts.GetTypeface("sans-serif", 400);
        Assert.NotNull(tf); // desktop resolves via the platform as before
    }
}
