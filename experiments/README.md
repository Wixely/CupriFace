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

`Gltf.cs`, `GlRenderer.cs` and `SceneRenderer.cs` are linked into every leg. They have since been
promoted to [`samples/Demo3d/`](../samples/Demo3d/README.md) — where the Showcase's 3D page uses
them too — and these probes compile the same single copy from there. Each
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

### What the hole can and cannot do

Measured on the engine's own canvas, not inferred:

```
transparent hole            86,242 px, box  32,123 -> 339,429
copper badge painted AFTER   7,461 px, box  46,360 -> 279,394   (entirely inside the hole's box)
```

**UI in front of the 3D works.** `ClearHole` uses `BlendMode.Src` to replace with transparent, so
anything drawn later composites on top — the badge is opaque engine pixels sitting inside the hole's
own rectangle. Paint order is the ordinary one; a later sibling occludes the 3D exactly as it would
occlude an image.

**The hole is a rounded rect, not a square.** It takes the element's `border-radius` and is
antialiased — which is why the box is 308×307 but only 86,242 of its 94,556 pixels are transparent:
the corners and the badge are not.

**And that UI can be TRANSLUCENT.** Both routes to partial alpha survive the hole and the
premultiplied-to-straight present, to within a rounding step:

| overlay painted over the hole | measured alpha | expected | pixels |
|---|---|---|---|
| opaque `#b87333` | **255** | 255 | 4,799 |
| `background: rgba(184,115,51,0.45)` | **114** | 114.75 | 4,099 |
| `opacity: 0.5` | **127** | 127.5 | 4,680 |

10,062 pixels in the hole's box carry partial alpha — real translucency at scale rather than
antialiasing fringe — and the 3D is visibly tinted through both panels. Worth testing both separately
because they take different paths: an `rgba()` fill is one command with alpha below 1, while
`opacity` wraps its subtree in `PushOpacity`. Either could have been flattened on the way out.

**What it cannot do, on the web:** the underlay is ONE canvas beneath the engine's, so the layer
order is fixed — 3D at the bottom, everything the engine paints above it. A transparent 3D object
cannot float *in front of* UI per-element, and UI painted *before* the surface is erased inside the
hole rather than showing behind the model. Putting the 3D canvas above the engine's (z-index, plus
`pointer-events:none`) would invert that globally, but it is a whole-page choice, not a per-element
one.

**Desktop and Android do not have that limit.** There the surface is a `DrawSurface` command inside
the display list, so it participates in normal per-element paint order and the `SKImage`'s alpha is
respected — a transparent, arbitrarily-shaped 3D object can sit in front of some UI and behind other
UI in the same frame. The asymmetry is the same one video already lives with, and it comes from the
web host having no GPU context rather than from anything about 3D.

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

## Does it perform?

Correctness at 4,032 triangles says nothing about viability, so the desktop leg has a `--stress` mode
that walks the instance count up and times whole frames. `glFinish` before each stop, because GL is
asynchronous and a stopwatch without it measures how fast commands are *queued*.

Three runs, GTX 1060, 480×480:

| instances | draw calls | ms/frame (3 runs) |
|-----------|-----------|--------------------|
| 1 | 1 | 0.02, 0.02, 0.02 |
| 10 | 10 | 0.06, 0.05, 0.08 |
| 50 | 50 | 0.38, 0.35, 0.26 |
| 250 | 250 | 1.01, 1.54, 1.36 |
| 1000 | 1000 | 2.34, 2.41, 2.52 |

**1,000 draw calls costs about 2.4 ms** — roughly 2.4 µs per call, leaving most of a 16 ms frame
unspent. That is the number the viability question actually turns on, and it is stable across runs.

Two honesties about this table. The middle of it is **noisy, ±40% run to run**, so only the endpoints
are worth quoting. And it is a **draw-call** measurement, not triangle throughput: the grid shows 36
instances and clips the rest, so beyond that point instances pay vertex and call cost but little
fill. Dividing 4M triangles by 2.4 ms would suggest 1.7 billion triangles a second, which a GTX 1060
cannot do and which nothing here measured.

An earlier version of this table was **discarded rather than published**: it scaled the zoom with the
instance count, so each teapot shrank as the count grew and fill fell while draw calls rose. The two
moved together and the result was non-monotonic — 100 instances "faster" than 50, 1000 "faster" than
500. Non-monotonic output is the signature of a measurement of nothing.

## Verified together

Every leg built and run from the committed state in one sweep, rather than each having worked at some
point during development — which is not the same claim, and this branch has already had one "it
builds" that never built:

| leg | how it was checked | result |
|-----|--------------------|--------|
| `GlProbe.Web` | Chromium, console + pixels | PASS — mean rgb 95.8, 91.3, 89.3 |
| `GlProbe.Desktop` | run, pixels read off the GPU | PASS — mean rgb 97.3, 92.5, 90.5 |
| `GlProbe.Android` | emulator, logcat + pixels | PASS — mean rgb 93.8, 90.7, 88.0 |
| `GlProbe.CupriFace` | headless composite, pixels | PASS — 4,240 saturated px beside 94,245 px of text/stage |
| `GlProbe.WebHost` | Chromium, hole alpha + screenshot | PASS — 3,191 saturated px inside the hole, **0** outside |
| `GlProbe.Web.Twin` | publish only | builds (it exists to be subtracted) |

Re-run in full after `SceneRenderer.cs` and `GlRenderer.cs` (now in `samples/Demo3d/`) gained the stress mode,
because those files are linked into **every** leg and "the change was additive" is the kind of
reasoning this branch has already been wrong about. Every figure above came back identical.

## Getting it into main

[PROMOTING.md](PROMOTING.md) scopes that by reading the code rather than estimating: what
generalising the web underlay actually touches (less than expected — `js_video_rect` is already
element-agnostic), what it risks (regressing `<cupri-video>`, which has its own gate), and where a
Showcase demo belongs (the composition root, exactly how video attaches, so `DemoApp` pays nothing).

## Re-establishing all of it

```
pwsh experiments/verify.ps1
```

Rebuilds every leg from what is committed and runs the ones this machine can. Legs whose
prerequisites are missing **SKIP** rather than fail — a box with no OpenGL, no attached device and no
browser still checks that everything *builds*, which is most of what silently rots. Exit code 1 if
anything genuinely failed.

The two browser legs stay **MANUAL**: driving a real browser needs Playwright or a devtools client,
and this script's job is to run anywhere. Their publish is checked (that is where an ILC or emcc link
would break), and the script prints the command to serve each one.

Why a script at all, when these probes are deliberately outside CI: every number in this file was
measured in one sitting, which is the weakest kind of evidence. This repo's own history is the
argument — v0.16.0 exists partly because an Android claim went from "measured once" to "asserted
every run".

## Running them individually

```
# web
dotnet publish experiments/GlProbe.Web -c Release -o out/glprobe
dotnet run --project tools/Serve -- out/glprobe 5299      # then open /index.html

# 3D under a CupriFace web page (host-composited hole)
dotnet publish experiments/GlProbe.WebHost -c Release -o out/glprobe-webhost
dotnet run --project tools/Serve -- out/glprobe-webhost 5299

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

`samples/Demo3d/teapot.glb` is the repo owner's own work: the mesh generated with the 3ds Max teapot
primitive and the base-colour texture authored alongside it. Recorded here because this line previously
said the opposite — "a supplied test asset, not original work" — written while it was a probe input and
nobody had asked. It is embedded in the two sample apps (`samples/Viewer`, `samples/WebLlvm`), so it
reaches the standalone Showcase downloads attached to releases; `Demo3d` is `IsPackable=false` and no
library references it, so it is in no published package.
