using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

// Behaviour of the first-party native controls, driven the way a host does. These previously lived
// only in the samples/NativeControls + samples/Controls runners; here they run in CI.
public class NativeControlsTests
{
    [Fact]
    public void Tabs_switch_the_bound_value()
    {
        var m = new TabModel();
        const string html = """
            <body><cupri-tabs value="{{Tab}}">
              <cupri-tab id="overview" label="Overview">A</cupri-tab>
              <cupri-tab id="settings" label="Settings">B</cupri-tab>
            </cupri-tabs></body>
            """;
        using var t = new TestDoc(html, "", m, components: true);
        t.ClickMatch(n => n.Element?.GetAttribute("data-set-value") == "settings");
        Assert.Equal("settings", m.Tab);
    }

    [Fact]
    public void Accordion_header_toggles_open()
    {
        var m = new AccModel();
        const string html = "<body><cupri-accordion><cupri-accordion-item label=\"Details\" open=\"{{Open}}\">hidden</cupri-accordion-item></cupri-accordion></body>";
        using var t = new TestDoc(html, "", m, components: true);
        Assert.False(m.Open);
        t.ClickMatch(n => n.Element?.ClassList.Contains("cupri-acc-header") == true);
        Assert.True(m.Open);
    }

    [Fact]
    public void Select_opens_then_picks_an_option_and_closes()
    {
        var m = new SelectModel();
        const string html = """
            <body><cupri-select value="{{Size}}" open="{{Open}}">
              <cupri-option value="small">Small</cupri-option>
              <cupri-option value="large">Large</cupri-option>
            </cupri-select></body>
            """;
        using var t = new TestDoc(html, "", m, components: true);
        t.ClickMatch(n => n.Element?.ClassList.Contains("cupri-select-trigger") == true);
        Assert.True(m.Open);
        t.ClickMatch(n => n.Element?.GetAttribute("data-set-value") == "large");
        Assert.Equal("large", m.Size);
        Assert.False(m.Open); // picking an option dismisses the dropdown
    }

    [Fact]
    public void Textarea_types_multiple_lines()
    {
        var m = new NotesModel();
        using var t = new TestDoc("<body><cupri-textarea value=\"{{Notes}}\"></cupri-textarea></body>", "", m, components: true);
        t.ClickMatch(n => n.Element?.HasAttribute("data-multiline") == true); // focus
        t.Type("Line1");
        t.Key(EditKey.Enter);
        t.Type("Line2");
        Assert.Equal("Line1\nLine2", m.Notes);
    }

    [Fact]
    public void Tree_twist_toggles_open()
    {
        var m = new TreeModel();
        const string html = "<body><cupri-tree><cupri-tree-item label=\"Root\" open=\"{{Open}}\"><cupri-tree-item label=\"Child\"></cupri-tree-item></cupri-tree-item></cupri-tree></body>";
        using var t = new TestDoc(html, "", m, components: true);
        Assert.True(m.Open);
        t.ClickMatch(n => n.Element?.ClassList.Contains("cupri-tree-twist") == true && n.Element.HasAttribute("data-cupri-toggle"));
        Assert.False(m.Open);
    }

    [Fact]
    public void Checkbox_and_switch_toggle_their_bound_flag()
    {
        var m = new ToggleModel();
        using var t = new TestDoc("<body><cupri-checkbox checked=\"{{Checked}}\">x</cupri-checkbox><cupri-switch checked=\"{{On}}\">y</cupri-switch></body>", "", m, components: true);
        t.ClickNode(t.FindRole("checkbox"));
        Assert.True(m.Checked);
        t.ClickNode(t.FindRole("switch"));
        Assert.True(m.On);
    }

    [Fact]
    public void Number_field_beside_a_switch_does_not_toggle_the_switch()
    {
        var m = new ToggleModel();
        const string html = "<body><div style='display:flex;gap:8px'>" +
            "<cupri-switch checked=\"{{On}}\"></cupri-switch>" +
            "<cupri-number value=\"{{Count}}\" min=\"0\" max=\"10\"></cupri-number>" +
            "</div></body>";
        using var t = new TestDoc(html, "", m, components: true, width: 280, height: 120);

        t.ClickNode(t.FindRole("spinbutton"));

        Assert.False(m.On);
    }

    [Fact]
    public void Radio_group_selects_the_clicked_value()
    {
        var m = new RadioModel();
        const string html = "<body><cupri-radio group=\"{{Sel}}\" value=\"a\">A</cupri-radio><cupri-radio group=\"{{Sel}}\" value=\"b\">B</cupri-radio></body>";
        using var t = new TestDoc(html, "", m, components: true);
        t.ClickMatch(n => n.Element?.GetAttribute("role") == "radio" && n.Element.GetAttribute("value") == "b");
        Assert.Equal("b", m.Sel);
    }

    [Fact]
    public void Slider_drag_updates_the_bound_value()
    {
        var m = new SliderModel { Volume = 10 };
        using var t = new TestDoc("<body><cupri-slider min='0' max='100' value='{{Volume}}' style='width:220px;margin:24px'></cupri-slider></body>",
            "", m, width: 280, height: 90, components: true);
        var b = HitTesting.AbsoluteBox(t.FindRole("slider"));
        t.Click(b.X + b.W * 0.30f, b.Y + b.H / 2);   // press at ~30%
        t.Move(b.X + b.W * 0.80f, b.Y + b.H / 2);    // drag to ~80%
        t.Up(b.X + b.W * 0.80f, b.Y + b.H / 2);
        Assert.InRange(m.Volume, 70, 82);
    }

    private sealed class TabModel { public string Tab { get; set; } = "overview"; }
    private sealed class AccModel { public bool Open { get; set; } }
    private sealed class SelectModel { public string Size { get; set; } = "small"; public bool Open { get; set; } }
    private sealed class NotesModel { public string Notes { get; set; } = ""; }
    private sealed class TreeModel { public bool Open { get; set; } = true; }
    private sealed class ToggleModel { public bool Checked { get; set; } public bool On { get; set; } public int Count { get; set; } = 5; }
    private sealed class RadioModel { public string Sel { get; set; } = ""; }
    private sealed class SliderModel { public int Volume { get; set; } }
}
