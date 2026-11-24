using CupriFace;
using CupriFace.Diagnostics;
using CupriFace.Samples.DesktopDemo;
using CupriFace.Shell;
using SkiaSharp;

// The desktop's own Open / Save / folder dialogs, and a second window, from one small notes app.
//
//   (default)         -> a real window: Open…, Save as…, Choose folder…, New scratch window
//   CUPRI_HEADLESS=1  -> no window: check both apps' markup with the doctor, prove that with no host
//                        neither service exists (and the app says so instead of failing), and write
//                        desktopdemo.png / desktopdemo-scratch.png

if (Environment.GetEnvironmentVariable("CUPRI_HEADLESS") is { Length: > 0 })
    return Headless.Run();

DesktopHost.Run(new NotesApp());
return 0;

internal static class Headless
{
    public static int Run()
    {
        var ok = true;
        foreach (CupriApp app in new CupriApp[] { new NotesApp(), new ScratchApp(1, _ => { }) })
        {
            var report = CupriDoctor.Check(app.Html, app.Css, CupriDoctor.DesktopAndPhone, model: app.Model);
            Console.WriteLine($"[DesktopDemo] {app.GetType().Name}: {(report.IsClean ? "doctor clean" : report.ToString())}");
            ok &= report.IsClean;
        }

        // No host: both services are null, and the app reports that rather than throwing.
        var notes = new NotesApp();
        using var doc = notes.CreateDocument();
        ok &= doc.FileDialogs is null && doc.Windows is null;
        using (doc.RenderToImage(notes.Width, notes.Height)) { }
        var open = Find(doc.Root, "open")!;
        var box = CupriFace.Interaction.HitTesting.ScreenBox(open);
        doc.DispatchClick(box.X + box.W / 2, box.Y + box.H / 2);
        Save(doc, notes.Width, notes.Height, notes.Background, "desktopdemo.png");
        var said = doc.DumpTree().Contains("no file dialogs", StringComparison.Ordinal);
        Console.WriteLine($"[DesktopDemo] with no host: FileDialogs={(doc.FileDialogs is null ? "null" : "set")}, " +
                          $"Windows={(doc.Windows is null ? "null" : "set")}, says so: {said}");
        ok &= said;

        var scratch = new ScratchApp(1, _ => { });
        using var sdoc = scratch.CreateDocument();
        using (sdoc.RenderToImage(scratch.Width, scratch.Height)) { }
        Save(sdoc, scratch.Width, scratch.Height, scratch.Background, "desktopdemo-scratch.png");

        Console.WriteLine(ok ? "[DesktopDemo] PASS" : "[DesktopDemo] FAIL");
        return ok ? 0 : 1;
    }

    private static CupriFace.Dom.RenderNode? Find(CupriFace.Dom.RenderNode n, string cls)
    {
        if (n.Element?.ClassList.Contains(cls) == true) return n;
        foreach (var c in n.Children) if (Find(c, cls) is { } f) return f;
        return null;
    }

    private static void Save(CupriDocument doc, int w, int h, SKColor bg, string name)
    {
        using (doc.RenderToImage(w, h, bg)) { }
        using var img = doc.RenderToImage(w, h, bg);
        using var data = img.Encode(SKEncodedImageFormat.Png, 95);
        using var f = File.Create(Path.Combine(Environment.CurrentDirectory, name));
        data.SaveTo(f);
        Console.WriteLine($"[DesktopDemo] {name}");
    }
}
