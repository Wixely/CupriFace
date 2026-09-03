# experiments

**Not part of the product.** Nothing here is referenced by `CupriFace.slnx`, built by CI, or shipped
in any package. These are feasibility probes, kept for the same reason `samples/AndroidProbe` is: the
answer to "can we?" is worth more written down than remembered, and a probe that has been deleted
proves nothing later.

The question being answered: **could CupriFace host a 3D renderer, given that the hard requirement is
NativeAOT-LLVM wasm?** That requirement is what ruled Stride out — its web export is blocked behind
Silk.NET 3.0 shipping browser bindings, then a migration, then a shader rewrite, and NativeAOT is an
open issue even on desktop.

## GlProbe.Web

A wasm app that creates a **WebGL2** context and renders `assets/teapot.glb`, with no JavaScript, no
bindings package and no native asset of its own.

The mechanism is the finding: `[DllImport("GL", EntryPoint = "glDrawArrays")]` plus
`<DirectPInvoke Include="GL" />` becomes a direct symbol reference that **emcc's own GL library
satisfies at link time**. Emscripten already ships the GLES3→WebGL2 shim, so there is nothing to
bind and nothing to build per-RID. The reason Silk.NET's missing browser bindings block Stride does
not apply to a renderer that uses no bindings.

Measured in Chromium:

```
GL_VERSION = OpenGL ES 3.0 (WebGL 2.0 (OpenGL ES 3.0 Chromium))
glb 964,596 bytes -> 2,395 vertices, 4,032 triangles, uv=True, texture=838,121 encoded bytes
texture decoded 701x561 -> rgba8888
model pixels = 19,784 (8.6% of frame)
distinct luminance levels = 101, distinct red levels = 132
pixels changed when the camera orbited = 19,575
```

Correctness was checked against the source asset rather than by eye, because the model's texture is a
paint-splatter image on which a swapped colour channel is invisible:

| | R | G | B |
|---|---|---|---|
| source texture mean | 131 | 104 | 107 |
| predicted (× 0.5 `baseColorFactor` × 0.771 mean lighting) | 50.5 | 40.1 | 41.2 |
| measured over 19,784 model pixels | 50.5 | 40.1 | 41.8 |

That agreement pins channel order, `baseColorFactor` being multiplied in rather than ignored, and the
lighting term, all at once.

### What it does NOT show

- Lambert plus ambient, **not PBR**. Enough to prove normals and uvs survived; calling it PBR would
  be a lie.
- One mesh, one draw call. No animation, no skinning, no camera from the file.
- **The payload number is not the real one.** 7.67 MB of wasm, but only because a standalone probe
  has to link `libSkiaSharp.a` itself for the JPEG decode. Inside CupriFace, Skia is already linked
  on every host, so the marginal cost there should be near zero — reasoned, not measured.

### The boundary worth keeping

The loader hands out **encoded** image bytes and the host decodes them. A renderer that owns a JPEG
decoder has taken on a codec dependency it never needed, and every host this could run on already has
one. Skia does it here in a single call, and the renderer only ever sees RGBA.

## Running it

```
dotnet publish experiments/GlProbe.Web -c Release -o out/glprobe
dotnet run --project tools/Serve -- out/glprobe 5299
```

Then open <http://127.0.0.1:5299/index.html>. Exit code 0 and a `PASS` line means the pixels were
what the shader should have produced.

`assets/teapot.glb` is a supplied test asset, not original work — check its provenance before it is
used anywhere that ships.
