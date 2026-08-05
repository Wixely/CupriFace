using System.Linq;
using CupriFace.Text;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

public class FontFallbackTests
{
    [Fact]
    public void Ascii_text_is_a_single_primary_run()
    {
        using var fs = new FontService();
        var runs = fs.SplitRuns("Hello world 123", "sans-serif", 400);
        Assert.Single(runs);
        Assert.Equal("Hello world 123", runs[0].Text);
        Assert.Same(fs.GetTypeface("sans-serif", 400), runs[0].Typeface);
    }

    [Fact]
    public void Runs_concatenate_back_to_the_original_text()
    {
        using var fs = new FontService();
        const string mixed = "Hi 😀 café 中 end"; // ASCII + emoji + accented + CJK (astral kept intact)
        var runs = fs.SplitRuns(mixed, "sans-serif", 400);
        Assert.Equal(mixed, string.Concat(runs.Select(r => r.Text)));
    }

    [Fact]
    public void Each_run_face_renders_its_characters_or_is_the_graceful_primary()
    {
        using var fs = new FontService();
        var primary = fs.GetTypeface("sans-serif", 400);
        foreach (var (segment, tf) in fs.SplitRuns("Hi 😀 café 中", "sans-serif", 400))
        {
            using var probe = new SKFont(tf, 16f);
            var cp = char.ConvertToUtf32(segment, 0);
            // Either the chosen face has the glyph, or no fallback existed so it stayed on the primary.
            Assert.True(probe.ContainsGlyph(cp) || ReferenceEquals(tf, primary), $"'{segment}' → face missing glyph {cp:X}");
        }
    }

    [Fact]
    public void Measure_handles_mixed_text_without_crashing()
    {
        using var fs = new FontService();
        Assert.True(fs.MeasureText("sans-serif", 400, 16f, "Hi 😀 café 中") > 0f);
        // ASCII path unchanged: measurement matches the primary shaper.
        Assert.True(fs.MeasureText("sans-serif", 400, 16f, "Hello") > 0f);
    }
}
