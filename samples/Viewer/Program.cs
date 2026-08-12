using CupriFace.Demo;
using CupriFace.Media;
using CupriFace.Media.Decoding;
using CupriFace.Shell;

// Desktop host showing the full Showcase (controls, overlays, layout, motion — tabbed).
// The identical ShowcaseApp runs in the browser via the web hosts (samples/WebWasm, samples/WebLlvm).
//
// Video attaches HERE, at the composition root — never in the shared app class, which the wasm
// host also compiles (it must not drag desktop codecs into the browser build). The WebM backend
// wires only when the cupricodecs native library is present (packaged apps carry it in
// runtimes/<rid>/; running from source, drop a codecs.yml artifact into native/<rid>/).
// Without it the video card shows its poster with disabled controls.
var section = args.SkipWhile(a => a != "--section").Skip(1).FirstOrDefault(); // e.g. --section images
DesktopHost.Run(new ShowcaseApp(section), doc =>
{
    if (NativeDecoders.Available)
        doc.UseVideo(new WebmVideoBackend(new NativeDecoders(), SdlAudioSink.TryCreate()));
});
