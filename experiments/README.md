# experiments

**Not part of the product.** Nothing here is referenced by `CupriFace.slnx`, built by CI, or shipped
in any package. These are feasibility probes, kept for the same reason `samples/AndroidProbe` is: the
answer to "can we?" is worth more written down than remembered, and a probe that has been deleted
proves nothing later.

The question: **could CupriFace host a 3D renderer, given that NativeAOT-LLVM wasm is a hard
requirement?** That requirement is what ruled Stride out — its web export waits on Silk.NET 3.0
shipping browser bindings, then a migration to it, then a shader rewrite, and NativeAOT is an open
issue there even on desktop.

## The answer: yes, on all three hosts

The same `teapot.glb` — interleaved accessors at stride 32, a two-node scene graph, `UNSIGNED_INT`
indices, uvs, and an 838 KB embedded JPEG — rendered, lit and textured on each:

| host | GL | how the symbols arrive | who decoded the JPEG | mean rgb over model pixels |
|------|----|------------------------|----------------------|-----------------------------|
| **web** (NativeAOT-LLVM) | WebGL2 / GLES 3.0, Chromium | Emscripten's are **static**; `DirectPInvoke` binds at link time | Skia → RGBA | **50.5, 40.1, 41.8** |
| **desktop** (Windows) | GL 3.3, NVIDIA GTX 1060 | `opengl32` has GL 1.1 only; the rest are **`wglGetProcAddress` function pointers** | Skia → **BGRA** | **51.1, 40.4, 42.0** |
| **android** (emulator) | GLES 3.0, SwiftShader | `libGLESv3.so` **exports** them; a plain `DllImport` binds | BitmapFactory → **ARGB** | **50.8, 40.3, 42.0** |

Three GL implementations, three ways of obtaining a function address, three image decoders with three
different channel layouts — and the rendered mean colour agrees to within **0.6 of 255**.

That agreement is the actual finding. It says the portable half really is portable: `shared/Gltf.cs`
is linked into all three and the GL call sequence is the same in each. What differs is confined to
how an address is obtained, and one `#version` line (desktop needs `330 core` where web and Android
both take `300 es`, since WebGL2 *is* GLES 3.0).

**So the seam a portable renderer needs is small**, and none of it requires a bindings package.
The reason Silk.NET's absent browser bindings block Stride does not apply to a renderer that uses no
bindings.

## How correctness was checked

Not by eye. The model's texture is a paint-splatter image, on which a swapped colour channel is
invisible — so the render was compared against the source asset's own statistics:

| | R | G | B |
|---|---|---|---|
| source texture mean | 131 | 104 | 107 |
| predicted (× 0.5 `baseColorFactor` × 0.771 mean lighting) | 50.5 | 40.1 | 41.2 |
| measured, web | 50.5 | 40.1 | 41.8 |

One agreement pins three things at once: channel order, `baseColorFactor` being multiplied in rather
than ignored, and the lighting term. The per-host decoder differences above are exactly what would
have produced a silent red/blue swap had any of them been assumed rather than converted.

The pixel assertions are shaped so a false pass is hard: **distinct luminance levels** (a flat
silhouette fails it — only real per-vertex normals through the accessor stride give a gradient),
**distinct red levels** (load-bearing once a texture is bound, because a flat-shaded teapot already
varies in luminance and would otherwise pass with the texture silently ignored), and **pixels changed
when the camera orbits** (a 3D scene rather than a picture).

## The boundary worth keeping

The loader hands out **encoded** image bytes; the host decodes. A renderer that owns a JPEG decoder
has taken on a codec dependency it never needed, and **every host already has one** — Skia on two of
these, Android's own BitmapFactory on the third, which needed no extra package at all.

## What none of this shows

- **Lambert plus ambient, not PBR.** Enough to prove normals and uvs survived; calling it PBR would
  be a lie.
- One mesh, one draw call. No animation, no skinning, no camera or lights from the file.
- **No CupriFace integration.** The two lanes it would use already exist — a texture-backed
  `SKImage` through `ISurfaceSource` where a `GRContext` exists (desktop GL, Android), and
  host-composited hole-punching on the web, where CupriFace renders to an `SKBitmap` via
  `putImageData` and has no GPU context at all. Neither is built.
- **Payload is not measured honestly yet.** The web probe's 7.67 MB wasm is inflated by linking
  `libSkiaSharp.a` itself for the decode; inside CupriFace, Skia is already linked, so the marginal
  cost should be far lower. Reasoned, not measured.
- Desktop was proven on one GPU (NVIDIA) and Android on SwiftShader, not on real mobile silicon.

## Running them

```
# web
dotnet publish experiments/GlProbe.Web -c Release -o out/glprobe
dotnet run --project tools/Serve -- out/glprobe 5299      # then open /index.html

# desktop  (add --show for a visible window)
dotnet run --project experiments/GlProbe.Desktop -c Release

# android
dotnet publish experiments/GlProbe.Android -c Release -r android-x64 -o out/glprobe-android
adb install -r out/glprobe-android/com.cupriface.glprobe-Signed.apk
adb logcat -s glprobe:I
```

Each prints the same statistics and a `PASS`/`FAIL` line. The desktop leg exits **2**, not 1, when it
cannot get a GL context at all — that is an environment fact, not a failure of the code, and this
repo already knows GL-less machines are common (virtualised GPUs, RDP, CI runners).

`assets/teapot.glb` is a supplied test asset, not original work — check its provenance before it is
used anywhere that ships.
