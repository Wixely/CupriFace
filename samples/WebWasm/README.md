# CupriFace — Web / WASM host (no Blazor)

Runs the **same** `ShowcaseApp` the desktop Viewer runs, in a browser. The managed engine
renders the whole UI to a CPU Skia surface and the thin JS glue (`wwwroot/main.js`) blits the
pixels to a `<canvas>` and forwards input. There is **no browser engine and no JavaScript in
the UI** — only the ~40-line canvas/input shim required to reach a `<canvas>` from WASM
(DESIGN.md §9.1).

## Run it

```bash
# Fastest — AOT publish (compiles the managed code to WASM; ~3-4 min build):
dotnet publish samples/WebWasm/WebWasm.csproj -c Release -p:Aot=true -o out
# then serve out/wwwroot with any static file server that sends .wasm as application/wasm

# Faster build, slower app — plain publish (jiterpreter only):
dotnet publish samples/WebWasm/WebWasm.csproj -c Release -o out

# Dev-loop only (SLOW — pure interpreter, see Performance below):
dotnet run --project samples/WebWasm/WebWasm.csproj -c Release
```

> **If the page is blank or the browser reports an SRI `integrity` error** (`Failed to find a
> valid digest in the 'integrity' attribute for … main.<hash>.js`), the build's static-asset
> fingerprints went stale — usually from switching between `dotnet publish` and `dotnet run`,
> which share `obj/`. Fix: delete `samples/WebWasm/obj`, `bin`, and `publish*`, then rebuild
> (VS Code: run the **clean-webwasm** task), and hard-reload the browser. The two VS Code
> launches don't normally collide (dev = Debug → `obj/Debug`, AOT = Release → `obj/Release`);
> mixing extra `dotnet build`/`run -c Release`/`publish` on the command line is what stales it.

## Performance — why `dotnet run` is slow, and what to do

A Chrome CPU profile of the `dotnet run` build showed **~68 % of all time in
`mono_interp_exec_method`** — i.e. the .NET engine code (layout, binding, style, paint) was
running in the **Mono interpreter**, not compiled. Everything else was noise (Skia raster
5.7 %, GC 3.3 %, exceptions 2.7 %). Interpreted managed code is ~10× slower than compiled, and
the engine does real compute per interaction, so the dev server feels very laggy.

Options, in order of impact:

1. **AOT publish** (`-p:Aot=true`) — compiles the managed code to native WASM. The earlier
   boot failure (`RuntimeError: function signature mismatch`) was root-caused with a symbol
   map: Mono's AOT emits one mismatched indirect call inside the CupriFace assembly
   (`SliderComponent.Expand` → interface dispatch). The fix keeps **CupriFace interpreted**
   (`_AOT_InternalForceInterpretAssemblies`) while CoreLib, AngleSharp, Regex, the JS-interop
   layer etc. are AOT-compiled — those were the bulk of the interpreter time in the profile.
   Verified booting + painting end-to-end under a Node host (`tools/node-host.mjs`).
2. **Plain publish** enables the **jiterpreter** (partial JIT for hot interpreter loops) that
   the dev server does not.
3. **Reduce per-interaction work.** Each click/keystroke rebuilds the whole document; the
   engine already caches CSS parsing and samples diagnostics at ≤1 Hz. True incremental
   updates (patching only what changed instead of a full rebuild) are the next big lever.

## Diagnostics

- `main.js` mirrors boot progress + console into a hidden `<pre id="bootlog">` and paints any
  boot/render error onto the canvas — failures are visible without dev tools, and headless
  `--dump-dom` can read the log.
- `tools/node-host.mjs` boots a published build under Node (no browser):
  `node --experimental-wasm-eh tools/node-host.mjs <path-to>/wwwroot/_framework`
  (drop a `{"type":"module"}` package.json into `_framework` first). Prints each boot step,
  paints one frame, and times a few interactions.
- AOT publishes emit `dotnet.native.js.symbols` (in `obj/.../wasm/for-publish/`): map a crash
  frame `wasm-function[N]` to its real name with `grep '^N:' dotnet.native.js.symbols`.

## How it works

- **Program.cs** (`Interop`) exposes `[JSExport]` methods: `Init`, `Tick` (render-on-demand —
  paints only after input, on the ≤1 Hz Diagnostics re-bind, or throttled while something
  animates), and input — `PointerDown/Move/Up`, `Wheel`, `KeyChar`, `EditKeyPress` (each marks
  the doc dirty). All route through the exact same `CupriDocument` dispatch the desktop hosts use.
- **main.js** boots the .NET runtime, runs a `requestAnimationFrame` loop calling `Tick`, and
  forwards pointer/wheel/keyboard. Named keys map to `EditKey` codes (Tab, arrows, Enter,
  Escape, …); printable characters go to `KeyChar`. So text input, keyboard focus/Tab order,
  scrolling, overlays and the live diagnostics all work in the browser.
- **Native Skia** for the browser comes from `SkiaSharp.NativeAssets.WebAssembly`; the
  desktop natives bundled in the engine are ignored here. `WasmBuildNative=true` links it in.

The desktop Viewer, the Blazor host (`samples/Web`), and this raw-WASM host all run the
identical `ShowcaseApp` — "export to the web" is just recompiling the app against a web host.
