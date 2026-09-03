using CupriFace.Demo;
using CupriFace.Media;
using CupriFace.Media.Decoding;
using CupriFace.Samples.Viewer;
using CupriFace.Shell;

// Desktop host showing the full Showcase (controls, overlays, layout, motion — tabbed).
// The identical ShowcaseApp runs in the browser via the web hosts (samples/WebWasm, samples/WebLlvm).
//
// Video attaches HERE, at the composition root — never in the shared app class, which the wasm
// host also compiles (it must not drag desktop codecs into the browser build). The WebM backend
// wires only when the cupricodecs native library is present (packaged apps carry it in
// runtimes/<rid>/; running from source, drop a codecs.yml artifact into native/<rid>/).
// Without it the video card shows its poster with disabled controls.
// `--app mobile` runs the phone-first sample in a phone-shaped window — the fast dev loop for
// mobile UI work (edit, F5, no emulator), and the proof the SAME app runs on desktop unchanged.
if (args.SkipWhile(a => a != "--app").Skip(1).FirstOrDefault() == "mobile")
{
    DesktopHost.Run(new MobileApp());
    return;
}

var section = args.SkipWhile(a => a != "--section").Skip(1).FirstOrDefault(); // e.g. --section images
DesktopHost.Run(new ShowcaseApp(section), doc =>
{
    if (NativeDecoders.Available)
        doc.UseVideo(new WebmVideoBackend(new NativeDecoders(), SdlAudioSink.TryCreate()));

    // The 3D viewport attaches here for the same reason video does — ShowcaseApp is shared with the
    // browser and Android hosts and must not reference a desktop GL stack. Returns null (and the
    // page shows its poster) on a machine with no usable OpenGL; the Showcase is not a 3D app.
    Teapot3dSurface.TryAttach(doc, Console.WriteLine);
});
