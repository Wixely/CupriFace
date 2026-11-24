using CupriFace.Components;
using CupriFace.Components.Controls;
using CupriFace.Diagnostics;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <see cref="ComponentRegistry.Of"/>, the registry for a build that should carry only the controls it
/// uses (#325). What it promises: the controls named behave and look exactly as they do in
/// <see cref="ComponentRegistry.Default"/>, nothing else is registered, and a control left out is
/// reported rather than silently left as an empty tag. That the others are then absent from a
/// NativeAOT binary is checked against a real publish in CI (the minimal-build job), not here.
/// </summary>
public class MinimalRegistryTests
{
    private const string Page = """
        <body style="padding:16px; display:flex; flex-direction:column; gap:12px">
          <cupri-slider min="0" max="10" value="4"></cupri-slider>
          <cupri-switch checked="true"></cupri-switch>
        </body>
        """;

    [Fact]
    public void Of_registers_only_the_controls_named_on_the_builtin_layer()
    {
        var registry = ComponentRegistry.Of(new SliderComponent(), new SwitchComponent());

        Assert.Equal(["cupri-slider", "cupri-switch"], registry.Tags.Order());
        Assert.All(registry.Registrations, r => Assert.Equal(ComponentLayer.BuiltIn, r.Layer));
    }

    [Fact]
    public void Of_refuses_a_tag_named_twice()
    {
        var ex = Assert.Throws<ComponentConflictException>(
            () => ComponentRegistry.Of(new SliderComponent(), new SliderComponent()));
        Assert.Contains("cupri-slider", ex.Message);
    }

    [Fact]
    public void A_control_looks_the_same_from_Of_as_from_Default()
    {
        using var full = Render(ComponentRegistry.Default());
        using var minimal = Render(ComponentRegistry.Of(new SliderComponent(), new SwitchComponent()));

        var diff = ImageDiff.Compare(full, minimal, tolerance: 0);
        Assert.True(diff.IsIdentical, diff.ToString());
    }

    [Fact]
    public void Libraries_and_replacements_still_layer_on_top()
    {
        var registry = ComponentRegistry.Of(new SliderComponent());
        registry.Replace(new SliderComponent());
        registry.Add(new SwitchComponent());

        Assert.Equal(ComponentLayer.App, registry.Registrations.Single(r => r.Tag == "cupri-slider").Layer);
        Assert.Equal(ComponentLayer.App, registry.Registrations.Single(r => r.Tag == "cupri-switch").Layer);
    }

    [Fact]
    public void A_control_left_out_is_reported_not_left_as_an_empty_tag()
    {
        var report = CupriDoctor.Check(Page, null, ComponentRegistry.Of(new SliderComponent()));

        var f = Assert.Single(report.Findings, x => x.Code == "CF0020");
        Assert.Contains("cupri-switch", f.Message);
    }

    private static SKImage Render(ComponentRegistry registry)
    {
        using var doc = CupriDocument.Load(Page, "").UseComponents(registry);
        doc.Refresh();
        using (doc.RenderToImage(320, 160)) { }
        return doc.RenderToImage(320, 160, SKColors.White);
    }
}
