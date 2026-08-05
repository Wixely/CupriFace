using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

public class IconSizeTests
{
    // A select renders a chevron icon (default 16px) via IconMarkup.
    private const string Html = "<body><cupri-select value=\"a\"><cupri-option value=\"a\">A</cupri-option></cupri-select></body>";

    private static float IconWidth(TestDoc t) => t.Find(n => n.Element?.HasAttribute("data-cupri-icon") == true)!.Width;

    [Fact]
    public void Icon_uses_its_per_use_default_size()
    {
        using var t = new TestDoc(Html, "", components: true);
        Assert.Equal(16f, IconWidth(t), 1); // the component's per-use default (fallback of the variable)
    }

    [Fact]
    public void Icon_size_is_overridable_via_the_css_variable_inherited()
    {
        using var t = new TestDoc(Html, "body { --cupri-icon-size: 28px; }", components: true);
        Assert.Equal(28f, IconWidth(t), 1); // set the token on an ancestor → all icons resize
    }

    [Fact]
    public void Icon_size_is_overridable_by_targeting_the_icon_class()
    {
        using var t = new TestDoc(Html, ".cupri-icon { --cupri-icon-size: 22px; }", components: true);
        Assert.Equal(22f, IconWidth(t), 1); // the base cupri-icon hook
    }
}
