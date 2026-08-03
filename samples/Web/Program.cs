using CupriFace.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// CupriFace WASM host (DESIGN.md §9 / M9). Boots the .NET WASM runtime and mounts the
// root component, which renders the engine into an <SKCanvasView> (Skia → canvas).
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
await builder.Build().RunAsync();
