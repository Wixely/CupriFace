using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

public class PickerLayoutTests
{
    private sealed class Model { public string D { get; set; } = "2026-08-15"; public string T { get; set; } = "14:30"; }

    [Fact]
    public void Date_and_time_pickers_in_a_flex_row_do_not_overlap()
    {
        const string css = ".row{display:flex;align-items:center;gap:14px}";
        const string html = "<body><div class='row'>" +
            "<cupri-datepicker value=\"{{D}}\"></cupri-datepicker>" +
            "<cupri-timepicker value=\"{{T}}\"></cupri-timepicker></div></body>";
        using var t = new TestDoc(html, css, new Model(), components: true, width: 600, height: 200);

        var date = HitTesting.AbsoluteBox(t.FindClass("cupri-dp-trigger"));
        var time = HitTesting.AbsoluteBox(t.FindClass("cupri-tp-trigger"));
        Assert.True(time.X >= date.X + date.W, $"overlap: date [{date.X}..{date.X + date.W}], time starts at {time.X}");
    }
}
