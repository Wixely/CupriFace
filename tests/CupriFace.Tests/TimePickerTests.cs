using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

public class TimePickerTests
{
    private sealed class Model { public string Time { get; set; } = "09:30"; public bool Open { get; set; } }

    private const string Html =
        "<body><div style='padding:20px'><cupri-timepicker value=\"{{Time}}\" open=\"{{Open}}\"></cupri-timepicker></div></body>";

    private static RenderNode? Popup(TestDoc t) => t.Find(n => n.Element?.ClassList.Contains("cupri-tp-popup") == true);
    private static RenderNode? Opt(TestDoc t, string iso) => t.Find(n => n.Element?.ClassList.Contains("cupri-tp-opt") == true && n.Element.GetAttribute("data-set-value") == iso);

    private static void Reveal(RenderNode option)
    {
        var column = option.Parent!;
        column.ScrollY = Math.Clamp(option.Y - (column.Height - option.Height) / 2f,
            0, column.MaxScrollY);
    }

    [Fact]
    public void Opens_showing_the_selected_hour_and_minute()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 320);
        Assert.Null(Popup(t));

        t.ClickMatch(n => n.Element?.ClassList.Contains("cupri-tp-trigger") == true);
        Assert.True(m.Open);
        Assert.NotNull(Popup(t));

        // 09 and 30 are the selected options.
        var selected = t.Find(n => n.Element?.ClassList.Contains("cupri-tp-opt") == true
            && n.Element.ClassList.Contains("selected")
            && n.Element.GetAttribute("data-set-value") == "09:30");
        Assert.NotNull(selected);
    }

    [Fact]
    public void Clicking_outside_closes_the_time_popup()
    {
        var m = new Model { Open = true };
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 320);
        Assert.NotNull(Popup(t));
        t.Click(350, 312);
        Assert.False(m.Open);
        Assert.Null(Popup(t));
    }

    [Fact]
    public void Picking_hour_then_minute_updates_the_parts_and_stays_open()
    {
        var m = new Model { Open = true };
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 320);

        var hour = Opt(t, "14:30")!;
        Reveal(hour);
        t.ClickNode(hour);                  // pick hour 14 (keeps minute 30)
        Assert.Equal("14:30", m.Time);
        Assert.True(m.Open);             // data-set-keep → stays open
        Assert.NotNull(Popup(t));

        var minute = Opt(t, "14:45")!;
        Reveal(minute);
        t.ClickNode(minute);                // pick minute 45 (keeps hour 14)
        Assert.Equal("14:45", m.Time);
        Assert.True(m.Open);
    }
}
