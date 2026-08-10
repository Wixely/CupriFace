using CupriFace.Demo;
using CupriFace.Samples.Viewer;
using CupriFace.Shell;

// Desktop host showing the full Showcase (controls, overlays, layout, motion — tabbed).
// The identical ShowcaseApp runs in the browser via the web hosts.
//
//   dotnet run --project samples/Viewer                  # native window
//   dotnet run --project samples/Viewer -- --web         # serve it to a browser instead
//   dotnet run --project samples/Viewer -- --web --port 5000 --no-browser
//
// --web renders the SAME app server-side and streams frames to a canvas over a WebSocket, so it
// needs no WebAssembly build (that's samples/WebWasm, which ships the engine into the browser).
if (args.Contains("--web"))
{
    var port = 5180;
    var i = Array.IndexOf(args, "--port");
    if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) port = p;
    WebViewerHost.Run(new ShowcaseApp(), port, openBrowser: !args.Contains("--no-browser"));
}
else
{
    DesktopHost.Run(new ShowcaseApp());
}
