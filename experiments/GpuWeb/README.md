# GpuWeb — can the browser host draw on the GPU?

**Not part of the product**, like everything else in `experiments/`. A probe for one question: the
web hosts rasterise on the CPU and blit with `putImageData`; what would a GPU-backed canvas buy,
and is WebGPU or WebGL the way to get one?

## WebGPU: not with this Skia

Checked against the binaries rather than assumed. `libSkiaSharp.a` from
`SkiaSharp.NativeAssets.WebAssembly` 3.116.1 contains **no Dawn, wgpu or Graphite backend** — the
only `graphite` strings in it are parameter types on shared `SkImage` virtuals. The managed
`SkiaSharp.dll` has no Graphite type either, in 3.116.1 or in 4.151.1. WebGPU through Skia would
mean building and maintaining our own libSkiaSharp with a C API that does not exist upstream.

Running only the shaders on WebGPU, beside Skia, needs every `@shader` translated from SkSL to
WGSL. Skia's own translator (`SkSL::WGSLCodeGenerator`) is compiled into the library, but nothing
in SkiaSharp's C API reaches it. And it would speed up nothing but the shaders.

## WebGL: already in the box

The same library does carry Skia's GL backend with a WebGL interface
(`GrGLMakeNativeInterface_webgl.o`), and the SkiaSharp package already links `-s USE_WEBGL2=1`.
So `GRGlInterface.Create()` → `GRContext.CreateGl` → an `SKSurface` on framebuffer 0 works, on the
Mono runtime, with no native build of our own. The engine needs no change: it paints to whatever
`SKCanvas` it is handed, and the `@shader` blocks compile to GLSL inside Skia.

The one non-obvious piece is `gl-interop.js`: Emscripten's `GL` object is private to the module,
and it is the only thing that can register a WebGL context where libSkiaSharp's `gl*` calls will
find it.

## Measured

960x640, device pixel ratio 1, Release publish, on both web runtimes: NativeAOT-LLVM
(`../GpuWeb.Llvm`, the same link line as `samples/WebLlvm`) and Mono (this folder, interpreted,
not AOT). Firefox (Playwright's build, headless) on a GeForce GTX 980 through ANGLE/Direct3D 11.
Milliseconds per frame, GPU frames timed to completion with a one-pixel readback through Skia.

| page | LLVM, CPU | LLVM, GPU | Mono, CPU | Mono, GPU |
|---|---|---|---|---|
| `samples/Shaders` - plasma banner and wave card animating | 726 | **6.4** | 656 | 25.5 |
| the same, with the full-page CRT shader | 2145 | **7.6** | 2141 | 28.1 |
| Showcase, every pixel repainted (stateless `Render`) | 37.7 | **26.5** | 115 | 93 |
| Showcase, the same walk drawing no pixels | 20.6 | 18.2 | 79 | 79 |

Three readings:

- **The compiler does nothing for a shader on the CPU lane.** 726 ms on LLVM against 656 on Mono:
  the time is Skia's software pipeline running the shader per pixel inside wasm, which is the same
  native code under either runtime. Of the 726 ms, the engine's own split puts 724 in raster.
- **On the GPU a shader frame is 6 to 8 ms on LLVM** - over a hundred times faster than the CPU
  lane, and the page-level CRT costs about a millisecond.
- **An ordinary page gains about 30% on a full repaint** (37.7 to 26.5 ms on LLVM). What is left
  is not pixels: 18 ms of the 26.5 is layout, the display list and the rasteriser's walk, and
  only a faster engine moves that.

## A crash this found, now fixed: undisposed Skia objects on NativeAOT-LLVM

The CRT page aborted on LLVM, repeatably:

```
System.Threading.LockRecursionException: RecursiveWriteNotAllowed
   at SkiaSharp.HandleDictionary.DeregisterHandle
   at SkiaSharp.SKData.Dispose(Boolean)
   at SkiaSharp.SKNativeObject.Finalize()
   at System.Runtime.__Finalizer.RhpProcessFinalizersAndReturn()
   at SkiaSharp.HandleDictionary.RegisterHandle
   at SkiaSharp.SKCanvas.GetObject
   at CupriFace.Paint.SkiaRasterizer.Paint
```

Single-threaded wasm has no finalizer thread, so a collection that starts inside SkiaSharp's
handle table runs finalizers there, and a finalizer that needs the same table's write lock throws.
The object being finalized is an `SKData`, and it came from the shader feature:
`SKRuntimeEffect.ToShader(uniforms, children, matrix)` calls `uniforms.ToData()` internally and
never disposes the copy, so every shader drawn left one for the finalizer, every frame. A desktop
test counting SkiaSharp's handle table measured exactly that: 80 left behind by 40 frames of two
shaders. It is not a GPU problem; the CPU lane made the same objects.

**No SkiaSharp release fixes it.** 3.119.4, 4.156.0 and `main` all have the same `ToShader` and
the same non-recursive lock. So `CompiledShader` now makes the copy itself, hands it to
SkiaSharp's private overload through an `UnsafeAccessor`, and disposes it. With the fix switched
off this page aborted on 3 runs of 3; with it on, 5 of 5 ran to the end.

Two things that still hold. Any `SKObject` left for the finalizer - by the engine or by an app -
can do this on the LLVM host, and nothing but disposing it prevents that. And `?soak=N`, which
draws animated frames back to back, did **not** reproduce the crash on the unfixed build (6000
frames, 2092 collections): it takes a collection starting inside the handle table, and the
ordinary measuring run finds one where the soak does not. Use the ordinary run as the check.

## Where it went

The WebGL lane is now `CupriFace.Web.NativeAot`'s default: GPU first, the CPU whenever the GPU
cannot (README.md, "Two ways to reach a browser"). The host adds what this probe left out: making
its context current before each frame (the 3D viewport switches contexts), context loss and
restore, resize, falling back to the CPU on a fresh canvas at startup or mid-run, and saying why in
`doc.Renderer`. `samples/WebLlvmShaders` is the shader page on the real host.

## What this does not show

- **Only Firefox, only one desktop GPU.** Not Chromium, not Safari, not a phone.
- **Looked at, not compared.** The two lanes were checked by eye on three pages; no pixel diff.
- **Not a host.** No input, no ARIA mirror, no device-pixel-ratio handling, no context-loss
  recovery, and neither the transparent-overlay mode nor the video and 3D underlay holes were
  tried on a WebGL canvas.
- **The frames-per-second field is only good when the browser is delivering animation frames.**
  A visible Firefox window in a remote-desktop session stopped delivering them partway through;
  headless did not. The per-frame milliseconds do not depend on it.

## Running it

```
dotnet publish experiments/GpuWeb -c Release -o out/gpuweb
dotnet run --project tools/Serve -- <absolute path to>/out/gpuweb/wwwroot 5394
```

The LLVM leg publishes flat, with no `wwwroot`:

```
dotnet publish experiments/GpuWeb.Llvm -c Release -o out/gpuweb-llvm
dotnet run --project tools/Serve -- <absolute path to>/out/gpuweb-llvm 5395
```

Then `http://127.0.0.1:5394/index.html?gpu=1&app=shaders` — `gpu=0|1`, and `app=shaders`,
`shaders-crt` or `showcase`. Results print under the canvas and land in `window.__results`.
