using System;
using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>Tests for the small markup controls: rating, segmented, pagination, search.</summary>
public class QuickControlsTests
{
    private static int Count(TestDoc t, Func<RenderNode, bool> match)
    {
        var n = 0;
        void Walk(RenderNode r) { if (match(r)) n++; foreach (var c in r.Children) Walk(c); }
        Walk(t.Root);
        return n;
    }

    // ---- Rating --------------------------------------------------------------
    private sealed class RatingModel { public int Score { get; set; } }

    [Fact]
    public void Rating_click_sets_value_and_fills_stars()
    {
        var m = new RatingModel { Score = 3 };
        using var t = new TestDoc(
            "<body><div style='padding:20px'><cupri-rating value=\"{{Score}}\" max=\"5\"></cupri-rating></div></body>",
            "", m, components: true, width: 300, height: 160);

        bool IsStar(RenderNode r) => r.Element?.ClassList.Contains("cupri-rating-star") == true;
        bool IsEmpty(RenderNode r) => r.Element?.ClassList.Contains("cupri-rating-empty") == true;

        Assert.Equal(5, Count(t, IsStar));
        Assert.Equal(2, Count(t, r => IsStar(r) && IsEmpty(r)));       // 3 filled, 2 empty
        Assert.Equal("3", t.FindRole("slider").Element!.GetAttribute("aria-valuenow"));

        t.ClickMatch(n => n.Element?.GetAttribute("data-set-value") == "5");
        Assert.Equal(5, m.Score);
        Assert.Equal(0, Count(t, r => IsStar(r) && IsEmpty(r)));       // all filled

        t.ClickMatch(n => n.Element?.GetAttribute("data-set-value") == "2");
        Assert.Equal(2, m.Score);
        Assert.Equal(3, Count(t, r => IsStar(r) && IsEmpty(r)));       // 2 filled, 3 empty
    }

    // ---- Segmented -----------------------------------------------------------
    private sealed class SegModel { public string View { get; set; } = ""; }

    private const string SegHtml = """
        <body><div style='padding:20px'>
          <cupri-segmented value="{{View}}">
            <cupri-segment value="grid">Grid</cupri-segment>
            <cupri-segment value="list">List</cupri-segment>
            <cupri-segment value="table">Table</cupri-segment>
          </cupri-segmented>
        </div></body>
        """;

    [Fact]
    public void Segmented_defaults_to_first_and_switches_on_click()
    {
        var m = new SegModel(); // empty → defaults to first segment visually
        using var t = new TestDoc(SegHtml, "", m, components: true, width: 340, height: 140);

        RenderNode Seg(string v) => t.Find(n => n.Element?.GetAttribute("data-set-value") == v)!;
        bool Active(RenderNode r) => r.Element?.ClassList.Contains("active") == true;

        Assert.True(Active(Seg("grid")));         // first is active when unset
        Assert.False(Active(Seg("list")));

        t.ClickNode(Seg("list"));
        Assert.Equal("list", m.View);
        Assert.True(Active(Seg("list")));
        Assert.False(Active(Seg("grid")));
    }

    // ---- Pagination ----------------------------------------------------------
    private sealed class PageModel { public int Page { get; set; } = 1; }

    [Fact]
    public void Pagination_windows_pages_and_navigates()
    {
        var m = new PageModel { Page = 1 };
        using var t = new TestDoc(
            "<body><div style='padding:16px'><cupri-pagination page=\"{{Page}}\" pages=\"10\"></cupri-pagination></div></body>",
            "", m, components: true, width: 460, height: 120);

        bool IsPage(RenderNode r) => r.Element?.ClassList.Contains("cupri-page") == true;
        bool IsEll(RenderNode r) => r.Element?.ClassList.Contains("cupri-page-ell") == true;

        Assert.Equal(6, Count(t, IsPage));   // 1 2 3 4 5 … 10 — fixed 7-slot window (6 numbers + 1 gap)
        Assert.Equal(1, Count(t, IsEll));    // one … gap before the last page

        // Prev is disabled at page 1 (no setter); Next targets page 2.
        Assert.Null(t.Find(n => n.Element?.GetAttribute("data-set-value") == "0"));
        t.ClickMatch(n => n.Element?.GetAttribute("data-set-value") == "2");
        Assert.Equal(2, m.Page);

        // Jump to the last page, then Prev targets 9 and Next is gone.
        t.ClickMatch(n => n.Element?.GetAttribute("data-set-value") == "10");
        Assert.Equal(10, m.Page);
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("data-set-value") == "9")); // prev → 9
        Assert.Null(t.Find(n => n.Element?.GetAttribute("data-set-value") == "11"));   // no next past last
    }

    [Fact]
    public void Pagination_width_is_constant_across_pages()
    {
        // The whole point: as the current page moves, the control must not change width (no shifting).
        var m = new PageModel { Page = 1 };
        using var t = new TestDoc(
            "<body><div style='padding:16px'><cupri-pagination page=\"{{Page}}\" pages=\"20\"></cupri-pagination></div></body>",
            "", m, components: true, width: 640, height: 120);

        float WidthAt(int p)
        {
            m.Page = p; t.Doc.Refresh(); t.Layout();
            return t.FindClass("cupri-pagination").Width;
        }

        var w1 = WidthAt(1);
        Assert.Equal(w1, WidthAt(2), 3);    // near start
        Assert.Equal(w1, WidthAt(10), 3);   // middle
        Assert.Equal(w1, WidthAt(19), 3);   // near end
        Assert.Equal(w1, WidthAt(20), 3);   // last
    }

    // ---- Search --------------------------------------------------------------
    private sealed class SearchModel { public string Query { get; set; } = ""; }

    [Fact]
    public void Search_shows_clear_only_when_filled_and_clears_on_click()
    {
        var m = new SearchModel { Query = "shoes" };
        using var t = new TestDoc(
            "<body><div style='padding:20px'><cupri-search value=\"{{Query}}\" placeholder=\"Find…\"></cupri-search></div></body>",
            "", m, components: true, width: 360, height: 140);

        Assert.NotNull(t.Find(n => n.Element?.ClassList.Contains("cupri-search-clear") == true));

        t.ClickMatch(n => n.Element?.ClassList.Contains("cupri-search-clear") == true);
        Assert.Equal("", m.Query);
        // Once empty the clear button is gone and the placeholder text is shown.
        Assert.Null(t.Find(n => n.Element?.ClassList.Contains("cupri-search-clear") == true));
        Assert.NotNull(t.Find(n => n.Element?.ClassList.Contains("cupri-tf-ph") == true));
    }
}
