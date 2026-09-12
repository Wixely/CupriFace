using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using CupriFace.Resources;
using CupriFace.Style;
using CupriFace.Text;
using Xunit;

namespace CupriFace.Tests;

/// <summary>Installable fonts (experiments/CUPRICUT.md, the CupriFace side): <c>@font-face</c> in a
/// stylesheet, <c>LoadFont</c>/<c>LoadFonts</c> on a document, <c>CupriApp.Fonts</c>, WOFF 1, CSS
/// weight matching over registered faces, and the <c>RegisteredOnly</c> policy that makes a missing
/// face an error naming the family instead of a slightly different picture on another machine.</summary>
public class FontFaceTests
{
    private static readonly string Fonts = Path.Combine(AppContext.BaseDirectory, "fonts");
    private static string Regular => Path.Combine(Fonts, "NotoSans-Regular.ttf");
    private static string Bold => Path.Combine(Fonts, "NotoSans-Bold.ttf");
    private static string FileUrl(string path) => new Uri(path).AbsoluteUri;

    // ---- parsing ---------------------------------------------------------------------------------

    [Fact]
    public void Parse_reads_family_sources_weight_range_and_style()
    {
        var rules = FontFace.Parse("""
            .x { color: red }
            @font-face {
              font-family: "Brand";
              src: local("Brand"), url(brand.woff2) format("woff2"), url("brand.ttf") format("truetype");
              font-weight: 300 700;
              font-style: italic;
            }
            @media (max-width: 500px) { .x { color: blue } }
            """);
        var r = Assert.Single(rules);
        Assert.Equal("Brand", r.Family);
        Assert.Equal(2, r.Sources.Count);                 // local() is a platform font: skipped
        Assert.Equal(("brand.woff2", "woff2"), (r.Sources[0].Url, r.Sources[0].Format));
        Assert.Equal(("brand.ttf", "truetype"), (r.Sources[1].Url, r.Sources[1].Format));
        Assert.Equal((300, 700), (r.WeightMin, r.WeightMax));
        Assert.Equal(FontSlant.Italic, r.Slant);
    }

    [Fact]
    public void Parse_keeps_a_data_url_whole_and_maps_keywords()
    {
        var rules = FontFace.Parse("@font-face { font-family: D; src: url(data:font/ttf;base64,AAAA); font-weight: bold }");
        var r = Assert.Single(rules);
        Assert.Equal("data:font/ttf;base64,AAAA", Assert.Single(r.Sources).Url); // the ';' inside url() is not a declaration end
        Assert.Equal((700, 700), (r.WeightMin, r.WeightMax));
        Assert.Equal(FontSlant.Normal, r.Slant);
        Assert.Empty(FontFace.Parse("@font-face { font-family: NoSrc; }"));    // nothing to load: not a rule
    }

    // ---- the document ----------------------------------------------------------------------------

    [Fact]
    public void A_font_face_rule_registers_the_face_for_its_declared_family()
    {
        var css = $$"""
            @font-face { font-family: "Brand Sans"; src: url("{{FileUrl(Regular)}}"); }
            @font-face { font-family: "Brand Sans"; src: url("{{FileUrl(Bold)}}"); font-weight: 700; }
            .t { font-family: "Brand Sans"; }
            """;
        using var td = new TestDoc("<p class='t'>Hello <b>bold</b></p>", css);
        var report = td.Doc.FontReport;
        var res = report.Resolutions.Where(r => r.Family == "Brand Sans").ToList();
        Assert.NotEmpty(res);
        Assert.All(res, r => Assert.Equal(FontSource.Registered, r.Source));
        Assert.All(res, r => Assert.Equal("Noto Sans", r.ResolvedFamily));
        Assert.Contains(res, r => r.Weight >= 600);   // the <b> resolved to the declared-700 face
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void Font_face_sources_are_tried_in_order()
    {
        var css = $$"""
            @font-face { font-family: Fallback; src: url(nope.woff2) format("woff2"), url("{{FileUrl(Regular)}}"); }
            .t { font-family: Fallback; }
            """;
        using var td = new TestDoc("<p class='t'>x</p>", css);
        Assert.Empty(td.Doc.FontReport.Problems);
        Assert.Contains(td.Doc.FontReport.Resolutions, r => r.Family == "Fallback" && r.Source == FontSource.Registered);
    }

    [Fact]
    public void Platform_policy_reports_what_the_machine_supplied()
    {
        using var td = new TestDoc("<p class='t'>x</p>", ".t { font-family: 'No Such Family Xyz'; }");
        var report = td.Doc.FontReport;
        var r = Assert.Single(report.Resolutions, r => r.Family == "No Such Family Xyz");
        Assert.NotEqual(FontSource.Registered, r.Source);
        Assert.False(report.IsDeterministic);
    }

    [Fact]
    public void Unloadable_font_face_is_a_problem_under_platform_and_an_error_under_registered_only()
    {
        const string css = "@font-face { font-family: Ghost; src: url(missing-font.ttf); } .t { font-family: Ghost; }";
        using (var td = new TestDoc("<p class='t'>x</p>", css))
        {
            var p = Assert.Single(td.Doc.FontReport.Problems);
            Assert.Equal("Ghost", p.Family);
            Assert.Contains("missing-font.ttf", p.Sources);
        }

        using var doc = CupriDocument.Load("<p class='t'>x</p>", css);
        doc.FontPolicy = FontPolicy.RegisteredOnly;
        var ex = Assert.ThrowsAny<Exception>(() => doc.RenderToImage(100, 100));
        Assert.Contains("Ghost", ex.Message);
    }

    [Fact]
    public void Registered_only_names_the_family_nothing_covers()
    {
        using var doc = CupriDocument.Load("<p class='t'>x</p>", ".t { font-family: 'Comic Sans MS'; }");
        doc.LoadFont(File.ReadAllBytes(Regular));
        doc.FontPolicy = FontPolicy.RegisteredOnly;
        var ex = Assert.ThrowsAny<Exception>(() => doc.RenderToImage(100, 100));
        Assert.Contains("Comic Sans MS", ex.Message);
    }

    [Fact]
    public void Registered_only_with_every_family_covered_is_deterministic()
    {
        using var doc = CupriDocument.Load("<p>plain <b>bold</b> <i>italic</i> — é ü ß</p>", "p { font-family: sans-serif; }");
        doc.LoadFonts(Fonts);
        doc.FontPolicy = FontPolicy.RegisteredOnly;
        using var _ = doc.RenderToImage(300, 100);
        var report = doc.FontReport;
        Assert.True(report.IsDeterministic, report.ToString());
        Assert.Contains("noto sans", report.RegisteredFamilies);
        Assert.Equal(0, doc.PendingLoads);
        Assert.True(doc.IsLoaded);
    }

    [Fact]
    public void LoadFont_accepts_a_source_and_a_src_string()
    {
        using var doc = CupriDocument.Load("<p>x</p>");
        doc.LoadFont(CupriSource.File(Regular));
        doc.LoadFont(FileUrl(Bold));
        using var _ = doc.RenderToImage(100, 100);
        Assert.Contains(doc.FontReport.Resolutions, r => r.Family == "sans-serif" && r.Source == FontSource.Registered);
        Assert.Throws<CupriResourceException>(() => doc.LoadFont("file:///nowhere/at/all.ttf"));
    }

    private sealed class FontApp : CupriApp
    {
        public override string Html => "<p class='t'>Hello</p>";
        public override string Css => ".t { font-family: 'Noto Sans'; font-weight: 700; }";
        public override IEnumerable<CupriSource> Fonts => [CupriSource.File(Regular), CupriSource.File(Bold)];
        public override FontPolicy FontPolicy => FontPolicy.RegisteredOnly;
    }

    [Fact]
    public void An_app_declares_its_fonts_and_policy_once_for_every_document()
    {
        using var doc = new FontApp().CreateDocument();
        Assert.Equal(FontPolicy.RegisteredOnly, doc.FontPolicy);
        using var _ = doc.RenderToImage(200, 100);
        var report = doc.FontReport;
        Assert.True(report.IsDeterministic, report.ToString());
        Assert.Contains(report.Resolutions, r => r.Family == "Noto Sans" && r.Weight == 700 && r.Source == FontSource.Registered);
    }

    // ---- the service: weight matching and policy ---------------------------------------------------

    [Fact]
    public void Declared_weight_ranges_match_by_the_css_nearest_rule()
    {
        using var fonts = new FontService();
        fonts.RegisterFont(File.ReadAllBytes(Regular), "X", 100, 400, FontSlant.Normal);
        fonts.RegisterFont(File.ReadAllBytes(Bold), "X", 600, 900, FontSlant.Normal);

        static bool IsBold(SkiaSharp.SKTypeface tf) => tf.FontStyle.Weight >= 600;
        Assert.False(IsBold(fonts.GetTypeface("X", 200)));  // in the light range
        Assert.False(IsBold(fonts.GetTypeface("X", 400)));
        Assert.False(IsBold(fonts.GetTypeface("X", 500)));  // 500 tries 400 before anything heavier
        Assert.True(IsBold(fonts.GetTypeface("X", 600)));
        Assert.True(IsBold(fonts.GetTypeface("X", 900)));
        // No italic face: the upright one stands in rather than the platform's.
        Assert.Equal("Noto Sans", fonts.GetTypeface("X", 400, FontSlant.Italic).FamilyName);
        Assert.All(fonts.Resolutions, r => Assert.Equal(FontSource.Registered, r.Source));
    }

    [Fact]
    public void Registered_only_keeps_platform_faces_out_of_glyph_fallback()
    {
        using var fonts = new FontService { Policy = FontPolicy.RegisteredOnly };
        fonts.RegisterFont(File.ReadAllBytes(Regular));
        var ex = Assert.Throws<FontNotRegisteredException>(() => fonts.GetTypeface("Comic Sans", 400));
        Assert.Equal("Comic Sans", ex.Family);
        Assert.Equal("Noto Sans", fonts.GetTypeface("sans-serif", 400).FamilyName);
        // A glyph Noto Sans lacks (CJK) stays in the primary face — tofu, but the SAME tofu everywhere.
        var runs = fonts.SplitRuns("a 漢 b", "sans-serif", 400);
        Assert.All(runs, r => Assert.Equal("Noto Sans", r.Typeface.FamilyName));
    }

    // ---- WOFF ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Woff1_unwraps_to_the_same_face(bool compressed)
    {
        var ttf = File.ReadAllBytes(Regular);
        var woff = WrapAsWoff(ttf, compressed);
        Assert.NotEqual(ttf.Length, woff.Length);

        using var a = new FontService(); a.RegisterFont(ttf);
        using var b = new FontService(); b.RegisterFont(woff);
        Assert.Equal("Noto Sans", b.GetTypeface("sans-serif", 400).FamilyName);
        Assert.Equal(a.MeasureText("sans-serif", 400, 24, "Wrapped in WOFF"), b.MeasureText("sans-serif", 400, 24, "Wrapped in WOFF"));
    }

    [Fact]
    public void Woff2_is_refused_by_name()
    {
        var bytes = new byte[48];
        "wOF2"u8.CopyTo(bytes);
        using var fonts = new FontService();
        var ex = Assert.Throws<NotSupportedException>(() => fonts.RegisterFont(bytes));
        Assert.Contains("WOFF 2", ex.Message);
    }

    [Fact]
    public void LoadFonts_notes_a_woff2_it_cannot_load_instead_of_stopping()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cupriface-fonts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Regular, Path.Combine(dir, "a.ttf"));
            var w2 = new byte[48]; "wOF2"u8.CopyTo(w2);
            File.WriteAllBytes(Path.Combine(dir, "b.woff2"), w2);
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a font");

            using var doc = CupriDocument.Load("<p>x</p>");
            doc.LoadFonts(dir);
            Assert.Contains("noto sans", doc.FontReport.RegisteredFamilies);
            var p = Assert.Single(doc.FontReport.Problems);
            Assert.Equal("b.woff2", p.Family);
        }
        finally { Directory.Delete(dir, true); }
    }

    // A WOFF 1 writer for the round-trip: the inverse of the decoder, written from the spec rather
    // than from the decoder so the two are not one function checking itself.
    private static byte[] WrapAsWoff(byte[] sfnt, bool compress)
    {
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4));
        var tables = new List<(byte[] entry, byte[] data, int origLength)>();
        var totalSfnt = 12 + numTables * 16;
        for (var t = 0; t < numTables; t++)
        {
            var entry = sfnt.AsSpan(12 + t * 16, 16).ToArray();
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(8));
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(12));
            var data = sfnt.AsSpan(offset, length).ToArray();
            if (compress)
            {
                using var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
                if (ms.Length < data.Length) data = ms.ToArray();
            }
            tables.Add((entry, data, length));
            totalSfnt += (length + 3) & ~3;
        }

        using var outMs = new MemoryStream();
        var header = new byte[44];
        "wOFF"u8.CopyTo(header);
        sfnt.AsSpan(0, 4).CopyTo(header.AsSpan(4));                       // flavor
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12), numTables);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), (uint)totalSfnt);
        outMs.Write(header);
        var dir = new byte[numTables * 20];
        outMs.Write(dir);                                                   // placeholder, patched below
        var pos = (int)outMs.Length;
        for (var t = 0; t < numTables; t++)
        {
            var (entry, data, orig) = tables[t];
            var rec = dir.AsSpan(t * 20);
            entry.AsSpan(0, 4).CopyTo(rec);                                 // tag
            BinaryPrimitives.WriteUInt32BigEndian(rec[4..], (uint)pos);
            BinaryPrimitives.WriteUInt32BigEndian(rec[8..], (uint)data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(rec[12..], (uint)orig);
            entry.AsSpan(4, 4).CopyTo(rec[16..]);                           // checksum
            outMs.Write(data);
            pos += data.Length;
            while ((pos & 3) != 0) { outMs.WriteByte(0); pos++; }
        }
        var result = outMs.ToArray();
        dir.CopyTo(result, 44);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)result.Length);
        return result;
    }
}
