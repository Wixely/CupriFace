using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

// Static file server for a PUBLISHED wasm build:
//
//     dotnet run --project samples/WebWasm/tools/Serve -- <wwwroot-dir> [port]
//
// The WASM SDK's dev server only serves `dotnet run` output, so the AOT and NativeAOT-LLVM launch
// configs need this to point a browser at their publish directories. It replaces a Node script of
// the same shape — this project's claim is that a UI needs no JavaScript engine, and its own dev
// loop should not quietly depend on one either.

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 5199;

if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"No such directory: {root}");
    Console.Error.WriteLine("Publish first — the launch tasks do this for you.");
    return 1;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.ClearProviders();                 // the one line below is the only output wanted
var app = builder.Build();

// application/wasm is the load-bearing one: served as octet-stream the browser refuses to
// stream-instantiate the module and falls back (or fails outright). The rest are the extensions a
// .NET wasm publish actually emits.
var types = new FileExtensionContentTypeProvider();
types.Mappings[".wasm"] = "application/wasm";
types.Mappings[".dat"] = "application/octet-stream";
types.Mappings[".blat"] = "application/octet-stream";
types.Mappings[".symbols"] = "text/plain";
types.Mappings[".pdb"] = "application/octet-stream";
types.Mappings[".mjs"] = "text/javascript";

var files = new PhysicalFileProvider(root);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = files,
    ContentTypeProvider = types,
    // A publish directory carries extensionless and unknown-suffix payloads; refusing to serve
    // them would look exactly like a corrupt build.
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
});

// The pattern the VS Code serverReadyAction matches, and what a human needs to see.
Console.WriteLine($"Serving {root} on http://127.0.0.1:{port}");
app.Run();
return 0;
