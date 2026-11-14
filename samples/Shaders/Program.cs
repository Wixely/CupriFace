using CupriFace;
using CupriFace.Diagnostics;
using CupriFace.Samples.Shaders;
using CupriFace.Shell;
using SkiaSharp;

// SkSL shaders on elements and on the whole page. See ShadersApp for what each one shows.
//
//   (default)         -> a real window: the banner and the card animate, the spotlight follows
//                        the pointer, the switch shades the whole page
//   CUPRI_HEADLESS=1  -> no window: check the sample's own markup, then write three PNGs on the
//                        CPU raster backend — which is the backend the web host draws with, so it
//                        is also the proof that none of this needs a GPU

if (Environment.GetEnvironmentVariable("CUPRI_HEADLESS") is { Length: > 0 })
    return Headless.Run();

DesktopHost.Run(new ShadersApp());
return 0;

internal static class Headless
{
    private const int W = 960, H = 640;

    public static int Run()
    {
        var probe = new ShadersApp();
        var report = CupriDoctor.Check(probe.Html, probe.Css, CupriDoctor.DesktopAndPhone, model: probe.Model);
        if (!report.IsClean)
        {
            Console.Error.WriteLine("[Shaders] the sample's own markup is not clean:");
            Console.Error.WriteLine(report.ToString());
            return 1;
        }
        Console.WriteLine("[Shaders] CupriDoctor: clean");

        using var plain = new ShadersApp().CreateDocument();
        using (plain.RenderToImage(W, H)) { }
        if (!plain.HasActiveAnimations)
        {
            Console.Error.WriteLine("[Shaders] FAIL: a shader reading iTime is on screen and the document asks for no frames.");
            return 1;
        }

        // Two moments of the same document: the banner and the card must both have moved.
        plain.DispatchPointerMove(700, 330);
        plain.Animate(0.0);
        using var t0 = plain.RenderToImage(W, H, probe.Background);
        plain.Animate(1.5);
        using var t1 = plain.RenderToImage(W, H, probe.Background);
        Save(t0, "shaders-t0.png");
        Save(t1, "shaders-t1.png");

        var moved = ImageDiff.Compare(t0, t1);
        Console.WriteLine($"[Shaders] t=0 -> t=1.5: {moved}");
        if (moved.IsIdentical)
        {
            Console.Error.WriteLine("[Shaders] FAIL: time passed and nothing changed.");
            return 1;
        }

        // The page shader, switched on the way the window's switch does it: a flag on the model.
        using var crt = new ShadersApp(crt: true).CreateDocument();
        using (crt.RenderToImage(W, H)) { }
        crt.Animate(1.5);
        using var shaded = crt.RenderToImage(W, H, probe.Background);
        Save(shaded, "shaders-crt.png");

        Console.WriteLine("[Shaders] PASS");
        return 0;
    }

    private static void Save(SKImage img, string name)
    {
        var path = Path.Combine(Environment.CurrentDirectory, name);
        using var data = img.Encode(SKEncodedImageFormat.Png, 95);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        Console.WriteLine($"[Shaders]   -> {path}");
    }
}
