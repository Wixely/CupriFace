using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Style;
using Xunit;

namespace CupriFace.Tests;

public class ComboboxTests
{
    private sealed class Model { public string City { get; set; } = ""; }

    private const string Html = """
        <body><div style='padding:20px'>
          <cupri-combobox value="{{City}}" placeholder="City">
            <cupri-option value="London">London</cupri-option>
            <cupri-option value="Paris">Paris</cupri-option>
            <cupri-option value="Lisbon">Lisbon</cupri-option>
          </cupri-combobox>
        </div></body>
        """;

    // The popup exists but is display:none until the field is focused; this finds it only when shown.
    private static RenderNode? Popup(TestDoc t) =>
        t.Find(n => n.Element?.ClassList.Contains("cupri-cb-popup") == true && n.Style.Display != DisplayType.None);

    private static int OptionCount(TestDoc t)
    {
        var c = 0;
        void Walk(RenderNode n) { if (n.Element?.ClassList.Contains("cupri-cb-option") == true) c++; foreach (var k in n.Children) Walk(k); }
        if (Popup(t) is { } p) Walk(p);
        return c;
    }

    private static RenderNode? Option(TestDoc t, string value) => t.Find(n => n.Element?.GetAttribute("data-set-value") == value);

    [Fact]
    public void Dropdown_hidden_until_focus_then_shows_all_options()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 400, height: 300);
        Assert.Null(Popup(t));                       // hidden initially

        t.ClickNode(t.FindRole("textbox"));          // focus the field
        Assert.NotNull(Popup(t));
        Assert.Equal(3, OptionCount(t));             // all suggestions
    }

    [Fact]
    public void Typing_filters_the_suggestions()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 400, height: 300);
        t.ClickNode(t.FindRole("textbox"));
        t.Type("lon");                               // live-commits to City, re-filters

        Assert.Equal("lon", m.City);                 // free-text: what you type is the value
        Assert.NotNull(Option(t, "London"));         // matches "lon"
        Assert.Null(Option(t, "Paris"));             // filtered out
        Assert.Equal(1, OptionCount(t));
    }

    [Fact]
    public void Picking_a_suggestion_sets_the_value_and_closes()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 400, height: 300);
        t.ClickNode(t.FindRole("textbox"));
        t.Type("li");
        t.ClickNode(Option(t, "Lisbon")!);           // pick a suggestion

        Assert.Equal("Lisbon", m.City);              // value written
        Assert.Null(Popup(t));                       // field blurred → dropdown closed
    }

    [Fact]
    public void Arrow_keys_highlight_and_enter_picks_the_highlighted_suggestion()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 400, height: 300);
        t.ClickNode(t.FindRole("textbox"));                  // focus → dropdown (London, Paris, Lisbon)

        t.Key(EditKey.Down);                                 // highlight first (London)
        var hi = t.Find(n => n.Element?.HasAttribute("data-highlight") == true);
        Assert.Equal("London", hi?.Element?.GetAttribute("data-set-value"));

        t.Key(EditKey.Down);                                 // move to Paris
        Assert.Equal("Paris", t.Find(n => n.Element?.HasAttribute("data-highlight") == true)?.Element?.GetAttribute("data-set-value"));

        t.Key(EditKey.Enter);                                // commit the highlighted one
        Assert.Equal("Paris", m.City);
        Assert.Null(Popup(t));                               // closed
    }

    [Fact]
    public void No_match_shows_the_empty_row()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 400, height: 300);
        t.ClickNode(t.FindRole("textbox"));
        t.Type("zzz");
        Assert.Equal(0, OptionCount(t));
        Assert.NotNull(t.Find(n => n.Element?.ClassList.Contains("cupri-cb-empty") == true));
    }
}
