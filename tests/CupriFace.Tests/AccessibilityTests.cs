using Xunit;

namespace CupriFace.Tests;

public class AccessibilityTests
{
    private sealed class Model { public bool On { get; set; } = true; public int Volume { get; set; } = 60; }

    [Fact]
    public void Aria_html_mirrors_roles_labels_and_states()
    {
        const string html = """
            <body>
              <h1>Dashboard</h1>
              <cupri-button>Save</cupri-button>
              <cupri-switch checked="{{On}}">Notifications</cupri-switch>
              <cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider>
            </body>
            """;
        using var t = new TestDoc(html, "", new Model(), components: true, width: 400, height: 300);
        var aria = t.Doc.BuildAriaHtml(400, 300);

        Assert.Contains("role=\"heading\"", aria);
        Assert.Contains("Dashboard", aria);
        Assert.Contains("role=\"button\"", aria);
        Assert.Contains("Save", aria);                          // button reads its label
        Assert.Contains("role=\"switch\"", aria);
        Assert.Contains("aria-checked=\"true\"", aria);         // switch state
        Assert.Contains("role=\"slider\"", aria);
        Assert.Contains("aria-valuenow=\"60\"", aria);          // slider value/range
        Assert.Contains("aria-valuemin=\"0\"", aria);
        Assert.Contains("aria-valuemax=\"100\"", aria);
        Assert.Contains("tabindex=\"0\"", aria);                // focusable controls are reachable
    }

    [Fact]
    public void Aria_html_updates_when_the_model_changes()
    {
        var m = new Model { On = false };
        using var t = new TestDoc("<body><cupri-switch checked=\"{{On}}\">X</cupri-switch></body>", "", m, components: true);
        Assert.Contains("aria-checked=\"false\"", t.Doc.BuildAriaHtml(400, 300));

        m.On = true;
        t.Doc.Refresh();
        Assert.Contains("aria-checked=\"true\"", t.Doc.BuildAriaHtml(400, 300));
    }
}
