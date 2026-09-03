using System.Reflection;
using SkiaSharp;

// THE TWIN. Identical to GlProbe.Web in every respect that costs bytes — same NativeAOT-LLVM
// settings, same Skia link, same embedded teapot.glb — except that it contains no glTF loader, no GL
// bindings and no renderer. Its only job is to be subtracted, the way the Lottie package's web cost
// was measured ("the same app with and without the package").
//
// It must exercise EXACTLY the Skia surface the real probe does and no more. The first version of
// this file called SKImage.Encode to "keep Skia alive", which linked the PNG ENCODER that the real
// probe never touches — and made the twin BIGGER than the thing it was the baseline for, giving a
// negative cost for adding 3D. An impossible sign is a useful kind of wrong: it cannot be argued
// with. Decoding, and only decoding, is what the real probe asks of Skia.

internal static partial class Twin
{
    private static int Main()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith("teapot.glb", StringComparison.OrdinalIgnoreCase));
        if (name is null) { Console.WriteLine("twin: asset missing"); return 1; }

        using var s = asm.GetManifestResourceStream(name)!;
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        Console.WriteLine($"twin: asset {bytes.Length:n0} bytes");

        // The same call the real probe makes on the texture it pulls out of the glb. Handing it the
        // whole container returns null — a glb is not an image — which is fine and is the point: the
        // DECODER is linked either way, and nothing else of Skia is.
        using var decoded = SKBitmap.Decode(bytes);
        Console.WriteLine($"twin: skia decode returned {(decoded is null ? "null (expected)" : $"{decoded.Width}x{decoded.Height}")}");
        return 0;
    }
}
