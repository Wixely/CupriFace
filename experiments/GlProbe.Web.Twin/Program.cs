using System.Reflection;
using SkiaSharp;

// THE TWIN. Identical to GlProbe.Web in every respect that costs bytes — same NativeAOT-LLVM
// settings, same Skia link, same embedded teapot.glb — except that it contains no glTF loader, no GL
// bindings and no renderer.
//
// Its only job is to be subtracted. The Lottie package's web cost was measured this way ("the same
// app with and without the package"), and the claim it replaces here is the one the README currently
// admits is reasoned rather than measured: what does 3D actually add to a wasm payload, once the
// Skia that decodes its textures is already being linked anyway?

internal static partial class Twin
{
    private static int Main()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith("teapot.glb", StringComparison.OrdinalIgnoreCase));
        using var s = name is null ? null : asm.GetManifestResourceStream(name);
        Console.WriteLine($"twin: asset {(s is null ? "missing" : $"{s.Length:n0} bytes")}");

        // Touch Skia so the linker keeps the same surface area the real probe forces it to keep.
        // Without this the trimmer would drop the decoder and the subtraction would flatter 3D by
        // charging it for Skia as well.
        using var bmp = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var img = SKImage.FromBitmap(bmp);
        using var enc = img.Encode(SKEncodedImageFormat.Png, 90);
        Console.WriteLine($"twin: skia alive, {enc?.Size ?? 0} bytes encoded");
        return 0;
    }
}
