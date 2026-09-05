using System.Runtime.InteropServices;
using CupriFace.Gl;
using SkiaSharp;

namespace CupriFace.Demo.ThreeD;

/// <summary>
/// The Showcase's teapot, as <see cref="IGlContent"/> — the drawing code, and nothing else.
///
/// <para><b>This file is the point of the package.</b> Before it there were three of these: a
/// desktop one, an Android one and a browser one, sharing 31 identical lines and differing in every
/// other respect — how a context is acquired, how big to draw, which shader dialect, whether the
/// engine gets a texture or a hole. All of that was integration, none of it was rendering, and all
/// of it now lives in <see cref="GlViewport"/>. What is left is this: load a model, compile a
/// shader, draw it. It runs unchanged on all three hosts.</para>
///
/// <para>The one host-shaped thing that remains is <see cref="GlContext.ShaderHeader"/>, and it
/// remains because it cannot be abstracted away: the same shader body needs
/// <c>#version 330 core</c> on a desktop and <c>#version 300 es</c> on a phone or in a browser. The
/// package knows which by asking the driver rather than guessing from the platform.</para>
/// </summary>
public sealed class TeapotContent : IGlContent
{
    private readonly Gltf _model;
    private readonly Action<string> _log;
    private SceneRenderer? _renderer;

    private TeapotContent(Gltf model, Action<string> log) { _model = model; _log = log; }

    /// <summary>Read the teapot out of the assembly, or return null. Never throws: a Showcase whose
    /// asset is missing shows the poster and a line of text, and is still a Showcase.</summary>
    public static TeapotContent? FromEmbeddedAsset(Action<string>? log = null)
    {
        log ??= _ => { };
        try
        {
            var asm = typeof(Gltf).Assembly;
            var name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("teapot.glb", StringComparison.OrdinalIgnoreCase));
            if (name is null) { log("the teapot asset is not embedded"); return null; }
            using var stream = asm.GetManifestResourceStream(name)!;
            var glb = new byte[stream.Length];
            stream.ReadExactly(glb);
            return new TeapotContent(Gltf.Load(glb), log);
        }
        catch (Exception ex)
        {
            log($"the teapot could not be read ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    public bool Initialise(GlContext gl)
    {
        // The sample's own entry-point table, filled from the context the package handed us.
        //
        // Still static, and that is now the SAMPLE'S choice rather than something the package
        // imposes — which is the whole of item 1. This demo has exactly one context and can afford
        // it; an app with a window and an offscreen target cannot, and is free to build an instanced
        // table from the same GetProcAddress without the package standing in its way.
        Gl.Load(gl.GetProcAddress);
        if (Gl.Missing.Count > 0)
        {
            _log($"{Gl.Missing.Count} GL entry points missing: {string.Join(", ", Gl.Missing)}");
            return false;
        }

        // Asked, not assumed. WebGL2 is OpenGL ES 3.0, so the phone and the browser want the same
        // shader and only the desktop differs.
        _renderer = new SceneRenderer(_model, glslEs: gl.Dialect == GlDialect.GlEs300);
        return _renderer.Initialise(DecodeWithSkia, _log);
    }

    /// <summary>Spin at a fixed rate against the wall clock, so the animation runs at the same speed
    /// on a host drawing 60 frames a second and one drawing 15.</summary>
    public void Render(GlContext gl, in GlFrame frame) =>
        // The no-clear overload: the viewport has already reset the state, set the viewport box and
        // cleared to the configured colour. Doing any of it again here would be a second, divergent
        // implementation of a contract that has one correct version.
        _renderer?.Draw(0.6f + (float)frame.ElapsedSeconds * 0.6f, frame.Width, frame.Height);

    public void Shutdown(GlContext gl) => _renderer?.Dispose();

    /// <summary>Skia decodes; the renderer only ever sees RGBA. The boundary that keeps the renderer
    /// free of any image library and therefore compilable for wasm — and it lives here once now,
    /// rather than being copied into each host's integration.</summary>
    private static (byte[] Pixels, int W, int H)? DecodeWithSkia(byte[] encoded)
    {
        using var decoded = SKBitmap.Decode(encoded);
        if (decoded is null) return null;
        using var rgba = decoded.Info.ColorType == SKColorType.Rgba8888
            ? decoded.Copy() : decoded.Copy(SKColorType.Rgba8888);
        var bytes = new byte[rgba.Width * rgba.Height * 4];
        Marshal.Copy(rgba.GetPixels(), bytes, 0, bytes.Length);
        return (bytes, rgba.Width, rgba.Height);
    }
}
