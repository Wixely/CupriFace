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

// An empty first argument is a caller bug worth naming: an unexpanded ${workspaceFolder} or a
// shell variable that did not survive, which otherwise surfaces as GetFullPath throwing.
if (args.Length > 0 && string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("The directory argument is empty — check the path passed by the caller.");
    return 1;
}

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

// Which directory this instance is serving, so a SECOND instance can ask before deciding what to do
// about an occupied port. No publish emits this name, so it cannot shadow a real asset.
app.MapGet(RootProbePath, () => root);

// Bind BEFORE announcing. The "Serving" line is what the VS Code background problemMatcher waits
// for, so printing it first meant a server that then failed to bind still told the task it was
// ready — and the launch proceeded against whatever was already on the port.
try
{
    await app.StartAsync();
}
catch (IOException ex) when (IsAddressInUse(ex))
{
    // A VS Code background task routinely outlives the debug session that started it, so the
    // previous run's server is usually still on this port. That used to be fatal: this process
    // died with an unhandled AddressInUseException, the task exited non-zero, and VS Code refused
    // to launch at all with "preLaunchTask terminated with exit code 1".
    //
    // If the incumbent is serving the SAME directory it is not in the way — the publish that just
    // ran wrote into that directory, so its content is current and reusing it is correct. Anything
    // else is a genuine clash and has to be loud, because silently debugging against a stale root
    // is the worst outcome available here.
    var incumbent = await AskServedRoot(port);

    if (incumbent is null)
    {
        Console.Error.WriteLine($"Port {port} is in use by something that is not this server.");
        Console.Error.WriteLine($"Stop it, or serve {root} on a different port.");
        return 1;
    }

    if (!PathsMatch(incumbent, root))
    {
        Console.Error.WriteLine($"Port {port} is already serving a DIFFERENT directory:");
        Console.Error.WriteLine($"  in use : {incumbent}");
        Console.Error.WriteLine($"  wanted : {root}");
        Console.Error.WriteLine("Stop that server before launching, or the browser would load the wrong build.");
        return 1;
    }

    // Same line the successful path prints, because the launch task's matcher waits for it.
    Console.WriteLine($"Serving {root} on http://127.0.0.1:{port}");
    Console.WriteLine("(reusing the server already on this port — same directory, left by an earlier run)");
    return 0;
}

Console.WriteLine($"Serving {root} on http://127.0.0.1:{port}");
await app.WaitForShutdownAsync();
return 0;

static bool IsAddressInUse(Exception ex)
{
    for (var e = ex; e is not null; e = e.InnerException!)
        if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse })
            return true;
    return false;
}

// Null when nothing answers, or when what answers is not one of these servers.
static async Task<string?> AskServedRoot(int port)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var body = await http.GetStringAsync($"http://127.0.0.1:{port}{RootProbePath}");
        return string.IsNullOrWhiteSpace(body) ? null : body.Trim();
    }
    catch
    {
        return null;
    }
}

static bool PathsMatch(string a, string b) =>
    string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

public partial class Program
{
    internal const string RootProbePath = "/__cupriface-serve-root";
}
