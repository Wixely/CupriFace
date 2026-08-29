# WebWasm — the Showcase in a browser

The raw-WebAssembly sample. Its entire source is:

```csharp
using CupriFace.Demo;
using CupriFace.Web;

CupriWeb.Run(new ShowcaseApp());
```

plus a `wwwroot/index.html` with a `<canvas id="cupri">` and one `<script>` tag pointing at
`_content/CupriFace.Web/main.js`. Everything else — the frame loop, damage-rect blitting, pointer
/ touch / wheel / keyboard input, the touch recognizer, the ARIA mirror screen readers read, IME
composition, the clipboard, browser-decoded video, and the two font faces the wasm Skia build does
not embed — is [`CupriFace.Web`](../../src/CupriFace.Web).

That split is the point. This sample used to *be* the web host, so a second web app had to copy
~1,000 lines out of it, along with an AOT workaround for a bug in CupriFace's own code (#73).

## Run it

```bash
dotnet run --project samples/WebWasm -c Release
```

## Publish (optionally AOT)

```bash
dotnet publish samples/WebWasm/WebWasm.csproj -c Release
dotnet publish samples/WebWasm/WebWasm.csproj -c Release -p:Aot=true
```

`-p:Aot=true` is opt-in and deliberately not tied to `Configuration`: a previous AOT publish left
in `obj/` makes a later `dotnet run` render a blank canvas. The interpreter workaround that AOT
currently needs comes from the host package, so a `PackageReference` consumer gets it without
knowing it exists.
