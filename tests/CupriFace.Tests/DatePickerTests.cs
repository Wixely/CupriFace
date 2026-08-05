using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

public class DatePickerTests
{
    private sealed class Model { public string Date { get; set; } = "2026-08-15"; public bool Open { get; set; } }

    private const string Html =
        "<body><div style='padding:20px'><cupri-datepicker value=\"{{Date}}\" open=\"{{Open}}\"></cupri-datepicker></div></body>";

    private static RenderNode? Popup(TestDoc t) => t.Find(n => n.Element?.ClassList.Contains("cupri-dp-popup") == true);
    private static RenderNode? Day(TestDoc t, string iso) => t.Find(n => n.Element?.ClassList.Contains("cupri-dp-day") == true && n.Element.GetAttribute("data-set-value") == iso);
    private static bool TitleHas(TestDoc t, string s) => t.Find(n => n.IsText && n.Text?.Contains(s) == true) is not null;

    [Fact]
    public void Trigger_opens_the_calendar_showing_the_selected_month()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 380);
        Assert.Null(Popup(t));                                   // closed initially

        t.ClickMatch(n => n.Element?.ClassList.Contains("cupri-dp-trigger") == true);
        Assert.True(m.Open);
        Assert.NotNull(Popup(t));
        Assert.True(TitleHas(t, "August 2026"));

        var sel = t.Find(n => n.Element?.ClassList.Contains("cupri-dp-day") == true && n.Element.ClassList.Contains("selected"));
        Assert.NotNull(sel);
        Assert.Equal("2026-08-15", sel!.Element!.GetAttribute("data-set-value")); // the 15th is marked selected
    }

    [Fact]
    public void Clicking_a_day_sets_the_value_and_closes()
    {
        var m = new Model { Open = true };
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 380);
        t.ClickNode(Day(t, "2026-08-20")!);
        Assert.Equal("2026-08-20", m.Date);
        Assert.False(m.Open);                                    // day pick closes the popup
        Assert.Null(Popup(t));
    }

    [Fact]
    public void Next_and_prev_page_months_in_place_without_closing()
    {
        var m = new Model { Open = true };
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 380);

        // ›  → same day in the next month, popup stays open (data-set-keep).
        t.ClickMatch(n => n.Element?.ClassList.Contains("cupri-dp-nav") == true && n.Element.GetAttribute("data-set-value") == "2026-09-15");
        Assert.Equal("2026-09-15", m.Date);
        Assert.True(m.Open);
        Assert.NotNull(Popup(t));
        Assert.True(TitleHas(t, "September 2026"));

        // ‹  → back to August.
        t.ClickMatch(n => n.Element?.ClassList.Contains("cupri-dp-nav") == true && n.Element.GetAttribute("data-set-value") == "2026-08-15");
        Assert.Equal("2026-08-15", m.Date);
        Assert.True(m.Open);
        Assert.True(TitleHas(t, "August 2026"));
    }
}
