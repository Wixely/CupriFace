using System.Globalization;
using Xunit;

namespace CupriFace.Tests;

public class CssCultureTests
{
    [Fact]
    public void Decimal_css_values_are_parsed_with_the_css_invariant_culture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            const string css = """
                body { margin: 0 }
                .box {
                    width: 12.5px;
                    height: 10.5px;
                    opacity: .5;
                    background: rgba(10, 20, 30, .5);
                    transform: scale(1.25) rotate(12.5deg);
                    transition: width .25s cubic-bezier(.1, .2, .3, .4);
                }
                @media (min-width: 200.5px) { .box { height: 22.5px } }
                """;

            using var doc = new TestDoc("<body><div class='box'></div></body>", css,
                width: 300, height: 200);
            var box = doc.FindClass("box");

            Assert.Equal(12.5f, box.Width, 3);
            Assert.Equal(22.5f, box.Height, 3);
            Assert.Equal(.5f, box.Style.Opacity, 3);
            Assert.Equal(127, box.Style.Background.Alpha);
            Assert.Equal(1.25f, box.Style.ScaleX, 3);
            Assert.Equal(12.5f, box.Style.RotateDeg, 3);
            Assert.Equal(.25f, Assert.Single(box.Style.Transitions!).Duration, 3);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void Non_finite_values_are_not_accepted_as_css_numbers(string value)
    {
        using var doc = new TestDoc("<body><div class='box'></div></body>",
            $".box{{width:{value}px;opacity:{value};transform:scale({value})}}");
        var box = doc.FindClass("box");

        Assert.True(float.IsFinite(box.Style.Width.Value));
        Assert.True(float.IsFinite(box.Style.Opacity));
        Assert.True(float.IsFinite(box.Style.ScaleX));
    }
}
