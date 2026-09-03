# experiments

**Not part of the product.** Nothing here is referenced by `CupriFace.slnx`, built by CI, or shipped
in any package. These are feasibility probes, kept for the same reason `samples/AndroidProbe` is: the
answer to "can we?" is worth more written down than remembered, and a probe that has been deleted
proves nothing later.

The question: **could CupriFace host a 3D renderer, given that NativeAOT-LLVM wasm is a hard
requirement?** That requirement is what ruled Stride out — its web export waits on Silk.NET 3.0
shipping browser bindings, then a migration to it, then a shader rewrite, and NativeAOT is an open
issue there even on desktop.

## The answer: yes, on every host, and it costs less than Lottie

The same `teapot.glb` — interleaved accessors at stride 32, a two-node scene graph, `UNSIGNED_INT`
indices, uvs, and an 838 KB embedded JPEG — rendered, lit and textured on each:

| host | GL | how the symbols arrive | who decoded the JPEG | mean rgb over model pixels |
|------|----|------------------------|----------------------|-----------------------------|
| **web** (NativeAOT-LLVM) | WebGL2 / GLES 3.0, Chromium | Emscripten's are **static**; `DirectPInvoke` binds at link time | Skia → RGBA | **50.5, 40.1, 41.8** |
| **desktop** (Windows) | GL 3.3, NVIDIA GTX 1060 | `opengl32` has GL 1.1 only; the rest are **`wglGetProcAddress` function pointers** | Skia → **BGRA** | **51.1, 40.4, 42.0** |
| **android** (emulator) | GLES 3.0, SwiftShader | `libGLESv3.so` **exports** them; a plain `DllImport` binds | BitmapFactory → **ARGB** | **50.8, 40.3, 42.0** |

Three GL implementations, three ways of obtaining a function address, three decoders with three
different channel layouts — and the rendered mean colour agrees to within **0.6 of 255**.

That agreement is the finding. `shared/Gltf.cs` and `shared/GlRenderer.cs` are linked into every leg
and the GL call sequence is the same in each; what differs is confined to how an address is obtained
and **one `#version` line** (desktop needs `330 core`, web and Android both take `300 es`, because
WebGL2 *is* GLES 3.0). **None of it requires a bindings package** — which is exactly why Silk.NET's
absent browser bindings do not block this the way they block Stride.

### It fits inside a CupriFace document with the engine unchanged

`GlProbe.CupriFace` composites the teapot into an ordinary HTML page, beside ordinary text, under
ordinary CSS. **Nothing in `src/` was touched.** `ISurfaceSource`'s own docstring already anticipated
it — *"a video player, later a 3D viewport or camera"* — and the element is a plain div wearing
`data-cupri-surface`, the same attribute a Lottie or a video carries.

The renderer runs on a **private GL context on a private thread**, which the contract explicitly
permits ("publish an immutable SKImage… from any thread"). Not on Skia's context: issuing raw GL on
the context Skia is mid-draw on corrupts its state tracking, and the remedy
(`GRContext.ResetContext`) needs a handle the engine does not expose.

The price of that choice, measured rather than assumed:

```
draw 0.08 ms    readback 0.60 ms    to-SKImage 1.10–1.67 ms
```

**Moving the frame costs roughly twenty times the rendering.** That is the number that decides
whether the zero-copy path is worth building — a texture-backed `SKImage` over a shared context,
which would need the engine to expose its `GRContext`. Now arguable with, rather than guessed at.

### What 3D actually costs on the web

Measured against a twin: the same app, same NativeAOT-LLVM settings, same Skia link, same embedded
asset, with the glTF loader, GL bindings and renderer removed. The method the Lottie package's web
cost was measured with.

| | raw | gzipped |
|---|---|---|
| **the 3D renderer** | **181 KB** | **73 KB** (+2.2%) |
| `CupriFace.Lottie`, same method, v0.15.0 | 408 KB | 119 KB (2.3%) |

**A whole 3D renderer is cheaper on the web than the Lottie package** — GL comes from Emscripten, so
there is no binding library and no native asset, and the image decoder was already being linked.

## How correctness was checked

Not by eye. The model's texture is a paint-splatter image, on which a swapped colour channel is
invisible — so the render was compared against the source asset's own statistics:

| | R | G | B |
|---|---|---|---|
| source texture mean | 131 | 104 | 107 |
| predicted (× 0.5 `baseColorFactor` × 0.771 mean lighting) | 50.5 | 40.1 | 41.2 |
| measured, web | 50.5 | 40.1 | 41.8 |

One agreement pins three things at once: channel order, `baseColorFactor` being multiplied in rather
than ignored, and the lighting term.

## Three ways these probes lied, and what fixed them

Worth keeping, because each was a check that passed while something was wrong.

**A black teapot passed a pixel test.** The integration first rendered a perfectly shaped, correctly
lit, entirely *black* teapot: `Initialise` binds the model's texture to `TEXTURE_2D`, then the host
creates its offscreen framebuffer's colour attachment, which rebinds `TEXTURE_2D` to the very texture
being drawn into. Sampling your own render target is undefined and reads black. The assertion passed
because it counted "not white page, not near-black" as model pixels — and the dark stage *behind* the
teapot classified as text, so the count was really anti-aliased edges. It now counts **saturated**
pixels, the one property page, text and stage all lack, with thresholds set from what the failure
produces rather than from what makes the check pass.

**A payload measurement came out negative.** The first twin called `SKImage.Encode` to "keep Skia
alive", linking a PNG *encoder* the real probe never uses — making the baseline bigger than its
subject and 3D appear to cost −169 KB. An impossible sign is a useful kind of wrong. The twin now
exercises exactly the decode path the real probe does, and nothing else.

**A build was reported working that had never built.** The web probe's project file was patched by a
script whose `\\` collapsed to `\`, turning `..\assets\teapot.glb` into `..` + BEL + `ssets` + TAB +
`eapot.glb`; MSBuild rejected it with `MSB4025`. The build was backgrounded, the completion
notification was read as success, and the output was not. Every project file now uses **forward
slashes**, which no shell or patch script can mangle that way.

## What none of this shows

- **Lambert plus ambient, not PBR.** Enough to prove normals and uvs survived; calling it PBR would
  be a lie.
- One mesh, one draw call. No animation, no skinning, no camera or lights from the file, and only the
  first mesh of a scene is drawn.
- **The web integration is unbuilt.** CupriFace's web hosts render to an `SKBitmap` via
  `putImageData` and have **no GPU context at all**, so the desktop approach does not transfer. The
  lane that fits there is host-composited hole-punching — exactly what `WebVideo` already does
  (`HostComposited => true`) — with a real WebGL canvas beneath a transparent hole. Not attempted.
- Desktop proven on one GPU (NVIDIA), Android on SwiftShader, neither on real mobile silicon.

## Running them

```
# web
dotnet publish experiments/GlProbe.Web -c Release -o out/glprobe
dotnet run --project tools/Serve -- out/glprobe 5299      # then open /index.html

# desktop  (add --show for a visible window)
dotnet run --project experiments/GlProbe.Desktop -c Release

# inside a CupriFace document  (--probe for the headless assertion + timings)
dotnet run --project experiments/GlProbe.CupriFace -c Release
dotnet run --project experiments/GlProbe.CupriFace -c Release -- --probe

# android
dotnet publish experiments/GlProbe.Android -c Release -r android-x64 -o out/glprobe-android
adb install -r out/glprobe-android/com.cupriface.glprobe-Signed.apk
adb logcat -s glprobe:I
```

Each prints the same statistics and a `PASS`/`FAIL` line. The desktop leg exits **2**, not 1, when it
cannot get a GL context at all — an environment fact rather than a code failure, and this repo
already knows GL-less machines are common (virtualised GPUs, RDP, CI runners).

`assets/teapot.glb` is a supplied test asset, not original work — check its provenance before it is
used anywhere that ships.
