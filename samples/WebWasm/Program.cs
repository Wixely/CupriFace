using System.Runtime.InteropServices.JavaScript;
using CupriFace;
using CupriFace.Demo;
using SkiaSharp;

// Raw .NET WebAssembly host — no Blazor. The engine renders the shared SettingsApp to a
// CPU Skia surface; the thin JS glue (main.js) blits the pixels to a <canvas> and forwards
// clicks. This is the "thin JS glue over a canvas" model from DESIGN.md §9.1.
Console.WriteLine("[CupriFace] WASM runtime started.");

public partial class Interop
{
    private static CupriDocument? _doc;
    private static SKColor _background;

    /// <summary>Create the shared app's document once.</summary>
    [JSExport]
    internal static void Init()
    {
        var app = new SettingsApp();
        _doc = app.CreateDocument();
        _background = app.Background;
    }

    /// <summary>Render one frame and hand the RGBA pixels to JS for <c>putImageData</c>.</summary>
    [JSExport]
    internal static void RenderFrame(int width, int height)
    {
        if (_doc is null || width <= 0 || height <= 0) return;

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(_background);
        _doc.Render(canvas, width, height);
        canvas.Flush();

        Present(bitmap.Bytes, width, height);
    }

    /// <summary>Route a canvas click through the same hit-test/dispatch as desktop; repaint if handled.</summary>
    [JSExport]
    internal static void Click(double x, double y, int width, int height)
    {
        if (_doc is not null && _doc.DispatchClick((float)x, (float)y))
            RenderFrame(width, height);
    }

    // JS side (module "cupri") copies the pixels into the 2D canvas.
    [JSImport("present", "cupri")]
    internal static partial void Present(byte[] rgba, int width, int height);
}
