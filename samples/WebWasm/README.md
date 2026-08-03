# CupriFace — Web / WASM host (no Blazor)

Runs the **same** `ShowcaseApp` the desktop Viewer runs, in a browser. The managed engine
renders the whole UI to a CPU Skia surface and the thin JS glue (`wwwroot/main.js`) blits the
pixels to a `<canvas>` and forwards input. There is **no browser engine and no JavaScript in
the UI** — only the ~40-line canvas/input shim required to reach a `<canvas>` from WASM
(DESIGN.md §9.1).

## Run it

```bash
dotnet run --project samples/WebWasm/WebWasm.csproj -c Release
# then open the printed http://127.0.0.1:<port> in a browser
```

Or publish a static, deployable site:

```bash
dotnet publish samples/WebWasm/WebWasm.csproj -c Release -o out
# serve out/wwwroot with any static file server
```

## How it works

- **Program.cs** (`Interop`) exposes `[JSExport]` methods: `Init`, `RenderFrame` (Present
  scale + `@keyframes` + the once-a-second Diagnostics re-bind, mirroring the desktop draw
  loop), and input — `PointerDown/Move/Up`, `Wheel`, `KeyChar`, `EditKeyPress`. All route
  through the exact same `CupriDocument` dispatch the desktop hosts use.
- **main.js** boots the .NET runtime, runs a `requestAnimationFrame` loop calling
  `RenderFrame`, and forwards pointer/wheel/keyboard. Named keys map to `EditKey` codes (Tab,
  arrows, Enter, Escape, …); printable characters go to `KeyChar`. So text input, keyboard
  focus/Tab order, scrolling, overlays and the live diagnostics all work in the browser.
- **Native Skia** for the browser comes from `SkiaSharp.NativeAssets.WebAssembly`; the
  desktop natives bundled in the engine are ignored here. `WasmBuildNative=true` links it in.

The desktop Viewer, the Blazor host (`samples/Web`), and this raw-WASM host all run the
identical `ShowcaseApp` — "export to the web" is just recompiling the app against a web host.
