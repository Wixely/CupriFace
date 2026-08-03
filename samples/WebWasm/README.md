# CupriFace — Web / WASM host (no Blazor)

Runs the **same** `ShowcaseApp` the desktop Viewer runs, in a browser. The managed engine
renders the whole UI to a CPU Skia surface and the thin JS glue (`wwwroot/main.js`) blits the
pixels to a `<canvas>` and forwards input. There is **no browser engine and no JavaScript in
the UI** — only the ~40-line canvas/input shim required to reach a `<canvas>` from WASM
(DESIGN.md §9.1).

## Run it

```bash
# Publish (recommended — enables the jiterpreter; MUCH faster than `dotnet run`):
dotnet publish samples/WebWasm/WebWasm.csproj -c Release -o out
# then serve out/wwwroot with any static file server that sends .wasm as application/wasm

# Or, for quick dev iteration (SLOW — pure interpreter, see Performance below):
dotnet run --project samples/WebWasm/WebWasm.csproj -c Release
```

> If you switch between `dotnet publish` and `dotnet run`, delete `samples/WebWasm/obj` and
> `bin` in between — a prior AOT/publish can leave artifacts in the shared `obj/` that make a
> later `dotnet run` render a blank canvas.

## Performance — why `dotnet run` is slow, and what to do

A Chrome CPU profile of the `dotnet run` build showed **~68 % of all time in
`mono_interp_exec_method`** — i.e. the .NET engine code (layout, binding, style, paint) was
running in the **Mono interpreter**, not compiled. Everything else was noise (Skia raster
5.7 %, GC 3.3 %, exceptions 2.7 %). Interpreted managed code is ~10× slower than compiled, and
the engine does real compute per interaction, so the dev server feels very laggy.

Options, in order of impact:

1. **Publish instead of `dotnet run`.** `dotnet publish -c Release` (no extra flags) enables
   the **jiterpreter** (a partial JIT for hot interpreter loops) that the dev server does not.
   This is the easy win and renders correctly.
2. **Reduce per-interaction work.** Each click/keystroke rebuilds the whole document; the
   engine already caches CSS parsing and samples diagnostics at ≤1 Hz. True incremental
   updates (patching only what changed instead of a full rebuild) are the next big lever.
3. **Full AOT** (`<RunAOTCompilation>true</RunAOTCompilation>`, publish only) is the largest
   win (~5–15×) in principle, **but currently fails to boot** in this SkiaSharp/HarfBuzz-heavy
   app with `RuntimeError: function signature mismatch` — an AOT-codegen/native-interop
   incompatibility that needs dedicated investigation before it can be enabled. Not stripping
   IL (`WasmStripILAfterAOT`) does not fix it.

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
