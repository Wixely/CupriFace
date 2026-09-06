using System.Numerics;
using System.Runtime.InteropServices;
using CupriFace.Gl;
using Khalkos3D;
using Khalkos3D.Formats;
using Khalkos3D.Gl;
using SkiaSharp;

namespace CupriFace.Samples.Khalkos;

/// <summary>
/// A <see cref="Khalkos3D"/> scene drawn through CupriFace's GL seam.
///
/// <para><b>This is the whole integration, and its size is the point.</b> CupriFace's
/// <see cref="IGlContent"/> hands over a proc-address function and a frame size;
/// <see cref="GlRenderer"/> wants exactly a proc-address function and a frame size. Neither library
/// knows the other exists and neither needed changing — the seam was designed to take a renderer,
/// and the renderer was designed to be handed a context.</para>
///
/// <para>Everything host-shaped stays on CupriFace's side: acquiring the context, sizing the target
/// to the element's device box, the texture handoff on desktop and Android, the underlay canvas in a
/// browser. Everything geometry-shaped stays on Khalkos3D's: parsing, materials, camera, shading.
/// Nothing in between had to be invented.</para>
/// </summary>
public sealed class Khalkos3dContent : IGlContent
{
    private readonly Scene _scene;
    private readonly Action<string> _log;
    private readonly RenderSettings _settings;
    private GlRenderer? _renderer;

    private Khalkos3dContent(Scene scene, Action<string> log, Vector4? clearColor)
    {
        _scene = scene;
        _log = log;
        _settings = new RenderSettings
        {
            // CupriFace.Gl has already cleared to the host's chosen colour before Render is called,
            // and clearing again here would erase whatever it put behind the model. Two libraries
            // each politely leaving the clear to the other is exactly the sort of thing that shows up
            // as a black viewport, so both sides say who owns it.
            ClearColor = null,
            Up = scene.Up,
            Environment = Khalkos3D.Environment.Studio,
        };
        _ = clearColor;
    }

    /// <summary>What the driver called itself, once a context has come up. Empty before then.</summary>
    public string Renderer => _renderer?.Renderer ?? "";

    /// <summary>The GL version string the driver reported.</summary>
    public string Version => _renderer?.Version ?? "";

    /// <summary>Which shader dialect the engine compiled for this context — the one thing that
    /// genuinely differs between a desktop, a phone and a browser.</summary>
    public string Dialect => _renderer?.Dialect.ToString() ?? "";

    /// <summary>Frames drawn.</summary>
    public long Frames { get; private set; }

    /// <summary>
    /// Load the demo model, or return null. Never throws: a host whose asset is missing shows the
    /// element's poster, which is CupriFace's existing behaviour for a surface with no frames.
    /// </summary>
    public static Khalkos3dContent? FromEmbeddedAsset(Action<string>? log = null, Vector4? clearColor = null)
    {
        log ??= _ => { };
        try
        {
            var assembly = typeof(Khalkos3dContent).Assembly;
            using var stream = assembly.GetManifestResourceStream("teapot.glb");
            if (stream is null) { log("the teapot asset is not embedded"); return null; }
            var glb = new byte[stream.Length];
            stream.ReadExactly(glb);

            var scene = GltfReader.Read(glb, new GltfOptions { DecodeImage = DecodeWithSkia });
            foreach (var note in scene.Report.Notes) log(note.ToString());

            return new Khalkos3dContent(scene, log, clearColor);
        }
        catch (Exception ex)
        {
            log($"the model could not be read ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>Skia decodes; the engine only ever sees RGBA. Khalkos3D bundles no codec on purpose,
    /// so this delegate is the whole of what a caller supplies — and a CupriFace app already has
    /// Skia, which is the argument for the seam being a delegate rather than a dependency.</summary>
    private static ImageData? DecodeWithSkia(byte[] encoded)
    {
        using var decoded = SKBitmap.Decode(encoded);
        if (decoded is null) return null;
        using var rgba = decoded.Info.ColorType == SKColorType.Rgba8888
            ? decoded.Copy() : decoded.Copy(SKColorType.Rgba8888);
        var pixels = new byte[rgba.Width * rgba.Height * 4];
        Marshal.Copy(rgba.GetPixels(), pixels, 0, pixels.Length);
        return new ImageData(pixels, rgba.Width, rgba.Height);
    }

    /// <inheritdoc/>
    public bool Initialise(GlContext gl)
    {
        // The proc-address function comes straight across. CupriFace resolved it per host — WGL,
        // dlsym on libGLESv3, emscripten — and Khalkos3D neither knows nor needs to know which.
        _renderer = GlRenderer.Create(gl.GetProcAddress, out var error);
        if (_renderer is null) { _log($"the engine would not start: {error}"); return false; }

        // Both sides decide the shader dialect by asking the driver rather than by guessing from the
        // platform, so this line is worth logging: if they ever disagreed, that would be the bug.
        _log($"ready GL_VERSION={_renderer.Version} GL_RENDERER={_renderer.Renderer} " +
             $"dialect={_renderer.Dialect} cupriface={gl.Dialect}");
        return true;
    }

    /// <inheritdoc/>
    public void Render(GlContext gl, in GlFrame frame)
    {
        if (_renderer is null) return;

        // Orbit against the wall clock so the model turns at the same rate on a host drawing sixty
        // frames a second and one drawing fifteen. frame.Width and Height are DEVICE pixels, already
        // sized to the element's box by CupriFace, so the aspect is right without asking anything.
        var camera = Camera.Frame(
            _scene.Bounds, _scene.Up,
            yaw: 0.6f + (float)frame.ElapsedSeconds * 0.6f,
            pitch: 0.35f,
            zoom: 0.9f,
            aspect: frame.Height > 0 ? (float)frame.Width / frame.Height : 1f);

        _renderer.Draw(_scene, camera, frame.Width, frame.Height, _settings);
        Frames++;
    }

    /// <inheritdoc/>
    public void Shutdown(GlContext gl)
    {
        _renderer?.Dispose();
        _renderer = null;
    }
}
