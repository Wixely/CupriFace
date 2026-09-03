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
indices, uvs, and an 838 KB embedded JPEG — rendered with metallic-roughness PBR on each host by
**one compiled renderer**, not three implementations that agree:

| host | GL | how a function address is found | who decoded the JPEG | mean rgb over model pixels |
|------|----|--------------------------------|----------------------|-----------------------------|
| **web** (NativeAOT-LLVM) | WebGL2 / GLES 3.0, Chromium | symbols are static; `emscripten_GetProcAddress` | Skia → RGBA | **95.8, 91.3, 89.3** |
| **desktop** (Windows) | GL 3.3, NVIDIA GTX 1060 | `opengl32` has GL 1.1 only; `wglGetProcAddress` | Skia → **BGRA** | **97.3, 92.5, 90.5** |
| **android** (emulator) | GLES 3.0, SwiftShader | `libGLESv3.so` exports them; **`dlsym`** | BitmapFactory → **ARGB** | **93.8, 90.7, 88.0** |

`shared/Gltf.cs`, `shared/GlRenderer.cs` and `shared/SceneRenderer.cs` are linked into every leg. Each
host file now contains **no GL calls at all** — only "get a context", "here is where addresses come
from", and "here is how this platform decodes an image". That is the portability result: the
difference between hosts shrank to a single lambda.

The three means agree within about **3.5 of 255**. Wider than the 0.6 the earlier Lambert renderer
managed, and the reason is not sloppiness: PBR's specular term is view-dependent, and the three
viewports are not the same shape (480×480 twice, 1080×2400 on the phone), so the camera frames the
model slightly differently and the highlight lands in a different place. A view-independent shader
had no way to disagree; this one does, and 3.5/255 is the size of that disagreement.

`dlsym` rather than `eglGetProcAddress` on Android is deliberate. Some EGL implementations return a
non-null stub for *any* name, which makes a missing entry point look present and then crash on
call — the same trap CupriFace's own GL loader documents for `glXGetProcAddressARB`.

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
draw 0.09 ms    readback 0.83 ms    to-SKImage 0.92 ms
```

**Moving the frame costs roughly twenty times the rendering.** That is the number that decides
whether the zero-copy path is worth building — a texture-backed `SKImage` over a shared context,
which would need the engine to expose its `GRContext`. Now arguable with, rather than guessed at.

### …and inside a CupriFace WEB document too, also unchanged

The desktop approach cannot transfer: CupriFace's web hosts render to an `SKBitmap` and present
through `putImageData`, so there is **no GPU context to share**. The web takes the engine's *other*
lane instead — host compositing, the same one `<cupri-video>` uses. `GlProbe.WebHost` is a
NativeAOT-LLVM CupriFace app whose surface returns `HostComposited => true`, so the engine punches a
transparent hole at the element's box, and a real WebGL canvas sits underneath it.

Verified in Chromium by reading the engine's own canvas rather than by looking at it:

```
engine canvas alpha INSIDE  the hole = 0      (genuinely punched through)
engine canvas alpha OUTSIDE the hole = 255    (opaque everywhere else)
underlay canvas 308x308 at 32,122, z-index 0, beneath the engine canvas at z-index 1
```

Everything it needs was already public, so **the engine is unchanged here as well**:
`ISurfaceSource.HostComposited`, `CupriApp.Transparent` (which selects the straight-alpha present a
hole requires), `doc.Root` / `RenderNode.SurfaceKey` to find the element, and
`HitTesting.ScreenBox` to learn where layout put it. `Painter.cs`'s comment on that branch already
named the case: *"a HOST-COMPOSITED surface… future 3D viewports"*.

**One build-config line is required**, and it is worth knowing because its failure points elsewhere:
the app must link with `-sMAX_WEBGL_VERSION=2`. Without it `emscripten_webgl_create_context`
**silently downgrades** a version-2 request to WebGL1 rather than refusing it, and the first symptom
is `ERROR: unsupported shader version` from a `#version 300 es` shader — a diagnosis three steps from
the cause. The probe now asserts the version string at runtime so the context is blamed, not the
shader.

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
(Measured before the PBR/multi-primitive rewrite; the shader is bigger now, the C# barely.)

## How correctness was checked

Not by eye. The model's texture is a paint-splatter image, on which a swapped colour channel is
invisible — so the render was compared against the source asset's own statistics:

| | R | G | B |
|---|---|---|---|
| source texture mean | 131 | 104 | 107 |
| predicted (× 0.5 `baseColorFactor` × 0.771 mean lighting) | 50.5 | 40.1 | 41.2 |
| measured, web | 50.5 | 40.1 | 41.8 |

One agreement pinned three things at once: channel order, `baseColorFactor` being multiplied in rather
than ignored, and the lighting term.

Those figures are from the **Lambert** renderer this replaced, and are kept because that is when the
check was decisive — a closed-form prediction is no longer practical now the shader tonemaps and
gamma-corrects. What guards it since is the cross-host agreement above: three GL implementations
landing within 3.5/255 of each other cannot all be wrong about channel order in the same direction.

## Four ways these probes lied, and what fixed them

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

**An Android asset went missing after a "safe" repair.** Flattening backslashes to forward slashes
in the project files changed how MSBuild resolved `Link="Assets/teapot.glb"`, nesting the asset at
`assets/Assets/teapot.glb`; `AssetManager.Open("teapot.glb")` then threw a FileNotFoundException whose
message is just the filename, which reads like a missing asset rather than a misplaced one. The Link
no longer carries a subdirectory.

**A build was reported working that had never built.** The web probe's project file was patched by a
script whose `\\` collapsed to `\`, turning `..\assets\teapot.glb` into `..` + BEL + `ssets` + TAB +
`eapot.glb`; MSBuild rejected it with `MSB4025`. The build was backgrounded, the completion
notification was read as success, and the output was not. Every project file now uses **forward
slashes**, which no shell or patch script can mangle that way.

## What none of this shows

- **No image-based lighting.** The BRDF is real Cook-Torrance metallic-roughness, but the environment
  term is a flat ambient constant standing in for IBL. Without an environment map a metal has nothing
  to reflect, so pure metals go dark — visible rather than hidden, and the next thing this would need.
- **No animation, no skinning**, and no camera or lights taken from the file.
- The scene walk handles multiple nodes, meshes and primitives with per-primitive materials, but
  every primitive is drawn every frame: no culling, no sorting, no instancing, and alpha blending
  modes are ignored.
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
