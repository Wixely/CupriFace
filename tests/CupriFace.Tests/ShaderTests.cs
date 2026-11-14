using CupriFace.Diagnostics;
using CupriFace.Paint;
using CupriFace.Style;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>shader()</c>: an SkSL program painted as a background, or run over what an element drew.
/// Every test renders on the CPU raster backend, which is the one the web host and the software
/// fallback use — so a shader that works here is not relying on a GPU being present.
/// </summary>
public class ShaderTests
{
    // Left half red, right half blue, split at a fixed x in the SHADER's coordinates — which is
    // what makes it a test of where those coordinates start.
    private const string Split = """
        uniform float3 iResolution;
        half4 main(float2 p) { return p.x < 20.0 ? half4(1, 0, 0, 1) : half4(0, 0, 1, 1); }
        """;

    private const string Body = "<body><div class='b'></div></body>";

    private static TestDoc Doc(string css, string html = Body, Action<CupriDocument>? setup = null,
                               int width = 200, int height = 160) =>
        new(html, css, width: width, height: height, beforeFirstLayout: setup);

    private static bool Near(SKColor c, byte r, byte g, byte b, int tol = 12) =>
        Math.Abs(c.Red - r) <= tol && Math.Abs(c.Green - g) <= tol && Math.Abs(c.Blue - b) <= tol;

    [Fact]
    public void A_background_shader_paints_the_box_in_the_boxs_own_coordinates()
    {
        using var t = Doc("body{margin:0;background:#fff} .b{width:80px;height:60px;margin:30px;background-image:shader(split)}",
            setup: d => d.Shaders.Register("split", Split));
        using var bmp = t.Render();
        // The box starts at x=30, so the shader's x=20 is the page's x=50.
        Assert.True(Near(bmp.GetPixel(40, 60), 255, 0, 0), $"{bmp.GetPixel(40, 60)}");
        Assert.True(Near(bmp.GetPixel(60, 60), 0, 0, 255), $"{bmp.GetPixel(60, 60)}");
        Assert.True(Near(bmp.GetPixel(15, 60), 255, 255, 255), "outside the box is untouched");
        Assert.True(Near(bmp.GetPixel(120, 60), 255, 255, 255), "outside the box is untouched");
    }

    [Fact]
    public void A_shader_declared_in_the_stylesheet_needs_no_code()
    {
        var css = """
            @shader stripes {
                // a brace in a comment } must not end the block
                half4 main(float2 p) {
                    if (p.y < 10.0) { return half4(0, 1, 0, 1); }
                    return half4(0, 0, 0, 1);
                }
            }
            body{margin:0;background:#fff}
            .b{width:80px;height:60px;background-image:shader(stripes)}
            """;
        using var t = Doc(css);
        using var bmp = t.Render();
        Assert.True(Near(bmp.GetPixel(40, 5), 0, 255, 0), $"{bmp.GetPixel(40, 5)}");
        Assert.True(Near(bmp.GetPixel(40, 30), 0, 0, 0), $"{bmp.GetPixel(40, 30)}");
        // …and the rule AFTER the block still applied, which is the CSS parser stepping over it.
        Assert.Equal(80f, t.FindClass("b").Width);
    }

    [Fact]
    public void A_background_shader_follows_the_border_radius()
    {
        using var t = Doc("body{margin:0;background:#fff} .b{width:80px;height:80px;border-radius:40px;background:shader(split)}",
            setup: d => d.Shaders.Register("split", Split));
        using var bmp = t.Render();
        Assert.True(Near(bmp.GetPixel(2, 2), 255, 255, 255), "the corner is outside the circle");
        Assert.True(Near(bmp.GetPixel(10, 40), 255, 0, 0), $"{bmp.GetPixel(10, 40)}");
    }

    [Fact]
    public void Uniforms_come_from_the_use_then_from_code()
    {
        const string tint = "uniform half4 tint; half4 main(float2 p) { return tint; }";
        var css = """
            body{margin:0;background:#fff}
            div{width:50px;height:50px}
            .a{background-image:shader(tint)}
            .b{background-image:shader(tint, tint #00ff00)}
            .c{background-image:shader(tint, tint 0 0 1 1)}
            """;
        using var t = Doc(css, "<body><div class='a'></div><div class='b'></div><div class='c'></div></body>",
            d =>
            {
                d.Shaders.Register("tint", tint);
                d.Shaders.SetUniform("tint", "tint", 1, 0, 0, 1);
            }, height: 200);
        using var bmp = t.Render();
        Assert.True(Near(bmp.GetPixel(25, 25), 255, 0, 0), $"code: {bmp.GetPixel(25, 25)}");
        Assert.True(Near(bmp.GetPixel(25, 75), 0, 255, 0), $"css colour: {bmp.GetPixel(25, 75)}");
        Assert.True(Near(bmp.GetPixel(25, 125), 0, 0, 255), $"css numbers: {bmp.GetPixel(25, 125)}");

        // Changing it from code repaints, with no Refresh.
        t.Doc.Shaders.SetUniform("tint", "tint", 1, 1, 0, 1);
        using var after = t.Render();
        Assert.True(Near(after.GetPixel(25, 25), 255, 255, 0), $"{after.GetPixel(25, 25)}");
    }

    [Fact]
    public void A_filter_shader_is_handed_what_the_element_drew()
    {
        // Swap red and blue: the output can only be right if iChannel0 really is the content, and
        // only in the right place if both coordinate spaces start at the element's corner.
        const string swap = """
            uniform shader iChannel0;
            half4 main(float2 p) { return iChannel0.eval(p).bgra; }
            """;
        var css = """
            body{margin:0;background:#fff}
            .b{width:80px;height:60px;margin:30px;background:#ff0000;filter:shader(swap)}
            .i{width:20px;height:20px;background:#00ff00}
            """;
        using var t = Doc(css, "<body><div class='b'><div class='i'></div></div></body>",
            d => d.Shaders.Register("swap", swap));
        using var bmp = t.Render();
        Assert.True(Near(bmp.GetPixel(90, 70), 0, 0, 255), $"red became blue: {bmp.GetPixel(90, 70)}");
        Assert.True(Near(bmp.GetPixel(40, 40), 0, 255, 0), $"the child stayed green, in place: {bmp.GetPixel(40, 40)}");
        Assert.True(Near(bmp.GetPixel(10, 10), 255, 255, 255), "outside the element is untouched");
    }

    [Fact]
    public void A_filter_shader_can_move_pixels()
    {
        // Mirror left-to-right. The green child sits at the left of the box and must come out on
        // the right — the case damage tracking has to be conservative about.
        const string mirror = """
            uniform shader iChannel0;
            uniform float3 iResolution;
            half4 main(float2 p) { return iChannel0.eval(float2(iResolution.x - p.x, p.y)); }
            """;
        var css = """
            body{margin:0;background:#fff}
            .b{width:100px;height:40px;background:#000;filter:shader(mirror)}
            .i{width:20px;height:40px;background:#00ff00}
            """;
        using var t = Doc(css, "<body><div class='b'><div class='i'></div></div></body>",
            d => d.Shaders.Register("mirror", mirror));
        using var bmp = t.Render();
        Assert.True(Near(bmp.GetPixel(10, 20), 0, 0, 0), $"{bmp.GetPixel(10, 20)}");
        Assert.True(Near(bmp.GetPixel(90, 20), 0, 255, 0), $"{bmp.GetPixel(90, 20)}");
    }

    [Fact]
    public void A_shader_on_body_covers_the_whole_page_including_overlays()
    {
        const string invert = """
            uniform shader iChannel0;
            half4 main(float2 p) { half4 c = iChannel0.eval(p); return half4(c.a - c.rgb, c.a); }
            """;
        var css = """
            body{margin:0;background:#fff;filter:shader(invert)}
            .b{width:50px;height:50px;background:#ff0000}
            .fixed{position:fixed;right:0;bottom:0;width:40px;height:40px;background:#ffffff}
            """;
        using var t = Doc(css, "<body><div class='b'></div><div class='fixed'></div></body>",
            d => d.Shaders.Register("invert", invert));
        using var bmp = t.Render();
        Assert.True(Near(bmp.GetPixel(25, 25), 0, 255, 255), $"{bmp.GetPixel(25, 25)}");
        // Below the body's own box (it is only as tall as its content) and inside a fixed overlay,
        // which paints in the top layer, outside the body's subtree.
        Assert.True(Near(bmp.GetPixel(180, 140), 0, 0, 0), $"{bmp.GetPixel(180, 140)}");
    }

    [Fact]
    public void A_pasted_shadertoy_runs_with_its_y_axis_up()
    {
        // Shadertoy's fragCoord starts at the bottom left: uv.y is 1 at the TOP of the image.
        const string toy = """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                vec2 uv = fragCoord / iResolution.xy;
                fragColor = vec4(uv.y > 0.5 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 0.0, 1.0), 0.0);
            }
            """;
        using var t = Doc("body{margin:0} .b{width:100px;height:100px;background:shader(toy)}",
            setup: d => d.Shaders.Register("toy", toy));
        Assert.True(t.Doc.Shaders.Get("toy")!.Shadertoy);
        using var bmp = t.Render();
        Assert.True(Near(bmp.GetPixel(50, 20), 255, 0, 0), $"top: {bmp.GetPixel(50, 20)}");
        Assert.True(Near(bmp.GetPixel(50, 80), 0, 0, 255), $"bottom: {bmp.GetPixel(50, 80)}");
    }

    private const string Pulse = """
        uniform float iTime;
        half4 main(float2 p) { return half4(fract(iTime), 0, 0, 1); }
        """;

    [Fact]
    public void A_shader_that_reads_the_clock_keeps_the_host_drawing_and_follows_Animate()
    {
        using var t = Doc("body{margin:0} .b{width:50px;height:50px;background:shader(pulse)}",
            setup: d => d.Shaders.Register("pulse", Pulse));
        // Both gates: the one hosts redraw on, and the one they call Animate on.
        Assert.True(t.Doc.HasActiveAnimations);
        Assert.True(t.Doc.HasActiveTransitions);

        Assert.True(t.Doc.Animate(0.25));
        using var a = t.Render();
        Assert.True(t.Doc.Animate(0.75));
        using var b = t.Render();
        Assert.InRange(a.GetPixel(25, 25).Red, 55, 72);
        Assert.InRange(b.GetPixel(25, 25).Red, 183, 199);
    }

    [Fact]
    public void A_shader_that_reads_no_clock_asks_for_no_frames()
    {
        using var t = Doc("body{margin:0} .b{width:50px;height:50px;background:shader(split)}",
            setup: d => d.Shaders.Register("split", Split));
        Assert.False(t.Doc.HasActiveAnimations);
        Assert.False(t.Doc.Animate(1.0));
    }

    [Fact]
    public void An_animated_shader_damages_its_own_box_not_the_viewport()
    {
        const string wobble = """
            uniform shader iChannel0;
            uniform float iTime;
            half4 main(float2 p) { return iChannel0.eval(p + float2(sin(iTime) * 3.0, 0)); }
            """;
        var css = """
            body{margin:0;background:#fff}
            .b{width:60px;height:40px;margin:50px;background:#f00;filter:shader(wobble)}
            """;
        using var t = Doc(css, "<body><div class='b'><span>hi</span></div></body>",
            d => d.Shaders.Register("wobble", wobble), width: 400, height: 300);
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        t.Doc.Animate(0.1);
        t.Doc.RenderIncremental(surface.Canvas, 400, 300, SKColors.White);
        t.Doc.Animate(0.2);
        var damage = t.Doc.RenderIncremental(surface.Canvas, 400, 300, SKColors.White);
        Assert.NotNull(damage);
        Assert.True(damage!.Value.Width < 80 && damage.Value.Height < 60, $"{damage}");
        Assert.True(damage.Value.Left >= 46 && damage.Value.Top >= 46, $"{damage}");
    }

    [Fact]
    public void The_pointer_reaches_iMouse_relative_to_the_element_and_repaints()
    {
        const string spot = """
            uniform float4 iMouse;
            half4 main(float2 p) { return distance(p, iMouse.xy) < 8.0 ? half4(1, 0, 0, 1) : half4(0, 0, 0, 1); }
            """;
        using var t = Doc("body{margin:0} .b{width:100px;height:100px;margin:20px;background:shader(spot)}",
            setup: d => d.Shaders.Register("spot", spot));
        Assert.True(t.Doc.DispatchPointerMove(70, 60), "a move over a pointer shader is a change");
        using var bmp = t.Render();
        Assert.True(Near(bmp.GetPixel(70, 60), 255, 0, 0), $"{bmp.GetPixel(70, 60)}");
        Assert.True(Near(bmp.GetPixel(40, 100), 0, 0, 0), $"{bmp.GetPixel(40, 100)}");
    }

    [Fact]
    public void A_keyframe_animates_a_uniform()
    {
        const string level = "uniform float level; half4 main(float2 p) { return half4(level, 0, 0, 1); }";
        var css = """
            @keyframes up { from { filter: shader(level, level 0) } to { filter: shader(level, level 1) } }
            body{margin:0}
            .b{width:50px;height:50px;animation:up 2s linear forwards}
            """;
        using var t = Doc(css, setup: d => d.Shaders.Register("level", level));
        t.Doc.Animate(1.0);
        using var bmp = t.Render();
        Assert.InRange(bmp.GetPixel(25, 25).Red, 118, 138);
    }

    [Fact]
    public void A_shader_that_does_not_compile_throws_from_code_and_is_reported_from_css()
    {
        using var t = Doc("body{margin:0}");
        var ex = Assert.Throws<ShaderCompileException>(() => t.Doc.Shaders.Register("bad", "half4 main(float2 p) { return nope; }"));
        Assert.Contains("nope", ex.CompileError);
        Assert.False(t.Doc.Shaders.TryRegister("bad", "not sksl", out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Null(t.Doc.Shaders.Get("bad"));

        var css = "@shader broken { half4 main(float2 p) { return nope; } } .b{width:10px;height:10px;background:shader(broken)}";
        var report = CupriDoctor.Check(Body, css);
        var finding = Assert.Single(report.Findings, f => f.Code == "CF0054");
        Assert.Contains("broken", finding.Message);
        Assert.Contains("nope", finding.Message);
    }

    [Fact]
    public void A_shader_name_that_refers_to_nothing_paints_the_element_unshaded_and_is_reported()
    {
        var css = "body{margin:0;background:#fff} .b{width:50px;height:50px;background:#ff0000;filter:shader(rippel)}";
        using var t = Doc(css);
        using var bmp = t.Render();
        Assert.True(Near(bmp.GetPixel(25, 25), 255, 0, 0), $"{bmp.GetPixel(25, 25)}");

        var report = CupriDoctor.Check(Body, "@shader ripple { half4 main(float2 p) { return half4(0); } } " + css);
        var finding = Assert.Single(report.Findings, f => f.Code == "CF0054");
        Assert.Contains("rippel", finding.Message);
        Assert.Contains("ripple", finding.Fix);
    }

    [Fact]
    public void A_document_using_shaders_correctly_is_clean()
    {
        var css = """
            @shader glow { uniform float3 iResolution; half4 main(float2 p) { return half4(p.x / iResolution.x, 0, 0, 1); } }
            body{margin:0;background:#fff}
            .b{width:50px;height:50px;background-image:shader(glow)}
            """;
        var report = CupriDoctor.Check(Body, css);
        Assert.DoesNotContain(report.Findings, f => f.Code is "CF0054" or "CF0050" or "CF0051" or "CF0031");
    }

    [Fact]
    public void Shader_arguments_parse_numbers_lengths_percentages_and_colours()
    {
        var use = ShaderUse.Parse("shader(ripple, strength 0.5, centre 10px 50%, tint #ff000080)")!;
        Assert.Equal("ripple", use.Name);
        Assert.Equal(new ShaderArg("strength", 0.5f, 0, 0, 0, 1), use.Args[0]);
        Assert.Equal(new ShaderArg("centre", 10f, 0.5f, 0, 0, 2), use.Args[1]);
        Assert.Equal(4, use.Args[2].Count);
        Assert.Equal(1f, use.Args[2].A);
        Assert.Equal(128 / 255f, use.Args[2].D, 3);
        Assert.Equal(use, ShaderUse.Parse("shader(ripple, strength 0.5, centre 10px 50%, tint #ff000080)"));
        Assert.Null(ShaderUse.Parse("linear-gradient(red, blue)"));
    }
}

[CollectionDefinition("skia handle table", DisableParallelization = true)]
public class SkiaHandleTableCollection;

/// <summary>
/// A shader frame must leave nothing for the finalizer. On desktop a wrapper nobody disposed is
/// only garbage; on NativeAOT-LLVM in a browser there is no finalizer thread, so it is finalized
/// inside whatever allocation started the collection — and when that is SkiaSharp's own handle
/// table, the finalizer asks for a lock the table already holds and the page dies with a
/// LockRecursionException.
///
/// <para>Alone in its collection because the table it counts is process-wide.</para>
/// </summary>
[Collection("skia handle table")]
public class ShaderHandleLeakTests
{
    // SkiaSharp's table of live wrappers. An undisposed one stays in it until it is finalized, so
    // growth across frames is the leak, measured without waiting for a collection.
    private static int LiveSkData()
    {
        var table = typeof(SKObject).Assembly.GetType("SkiaSharp.HandleDictionary", throwOnError: true)!;
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var instances = (Dictionary<IntPtr, WeakReference>)table.GetField("instances", flags)!.GetValue(null)!;
        var gate = table.GetField("instancesLock", flags)!.GetValue(null)!;
        gate.GetType().GetMethod("EnterReadLock")!.Invoke(gate, null);
        try { return instances.Values.Count(w => w.Target is SKData); }
        finally { gate.GetType().GetMethod("ExitReadLock")!.Invoke(gate, null); }
    }

    [Fact]
    public void An_animated_shader_frame_leaves_nothing_for_the_finalizer()
    {
        const string pulse = """
            uniform float iTime;
            half4 main(float2 p) { return half4(fract(iTime), 0, 0, 1); }
            """;
        const string wobble = """
            uniform shader iChannel0;
            uniform float iTime;
            half4 main(float2 p) { return iChannel0.eval(p + float2(sin(iTime) * 3.0, 0)); }
            """;
        var css = """
            body{margin:0;background:#fff}
            .b{width:60px;height:40px;background:shader(pulse)}
            .f{width:60px;height:40px;background:#f00;filter:shader(wobble)}
            """;
        using var t = new TestDoc("<body><div class='b'></div><div class='f'>hi</div></body>", css, width: 200, height: 160,
            beforeFirstLayout: d => { d.Shaders.Register("pulse", pulse); d.Shaders.Register("wobble", wobble); });
        using var surface = SKSurface.Create(new SKImageInfo(200, 160));
        t.Doc.Animate(0.0);
        t.Doc.RenderIncremental(surface.Canvas, 200, 160, SKColors.White);

        var before = LiveSkData();
        const int frames = 40;
        for (var i = 1; i <= frames; i++)
        {
            t.Doc.Animate(i / 60.0);
            Assert.NotNull(t.Doc.RenderIncremental(surface.Canvas, 200, 160, SKColors.White));
        }
        var grown = LiveSkData() - before;
        // Two shaders a frame: the leak this guards against is 80 here, not one or two.
        Assert.True(grown < frames / 4, $"{grown} SKData left undisposed by {frames} shader frames");
    }
}
