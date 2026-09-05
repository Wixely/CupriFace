# Demo3d — the Showcase's 3D viewport

A small OpenGL renderer used by the Showcase's **3D** page. It is a **sample**, not a feature:
CupriFace ships no 3D renderer and has no plans to. What this demonstrates is the *seam* — that a
live picture from outside the engine composites correctly with ordinary UI, on every host.

```
Gltf.cs           a GLB reader: buffers, accessors (interleaved byteStride included), materials
GlRenderer.cs     `Gl`, a function-pointer table filled from whatever proc-address source the host has
SceneRenderer.cs  Cook-Torrance PBR (GGX / Smith / Schlick) over one shader source
teapot.glb        4,032 triangles, one base-colour texture (see Provenance below)
```

## Why it has no host dependency

No windowing, no Skia, no engine reference — GL calls through function pointers and a byte reader.
That is what lets the *identical* code compile for `net10.0`, Android and `browser-wasm`; a
Silk.NET or SkiaSharp reference here would break the wasm leg immediately. Each host's composition
root owns the hundred-odd lines that acquire a GL context and publish frames, because that is the
part that genuinely differs:

| host | where | how the pixels reach the screen |
|---|---|---|
| desktop | [`samples/Viewer/Teapot3dSurface.cs`](../Viewer/Teapot3dSurface.cs) | **painted** — GL into an FBO on the host's own context, handed over as a texture (`IGpuSurfaceSource`); readback only if the host has no GPU |
| browser | [`samples/WebLlvm/Web3dSurface.cs`](../WebLlvm/Web3dSurface.cs) | **host-composited** — a transparent hole, with a WebGL2 canvas underneath |
| Android | not wired | paints nothing; the panel behind shows and the page says so |

The same shader source serves both: `glslEs: false` emits `#version 330 core`, `true` emits
`#version 300 es`.

## Wiring it into an app

At the **composition root**, never in the shared app class — the rule video already follows, because
the shared class is compiled by the browser and Android hosts too and must not drag a desktop GL
stack into them:

```csharp
DesktopHost.Run(new ShowcaseApp(), doc => Teapot3dSurface.TryAttach(doc));
```

The app itself contributes one element and no reference to any of this:

```html
<div data-cupri-surface="showcase3d"></div>
```

A host that wires nothing paints nothing there, so whatever is behind the element shows. Add
`data-cupri-image="…"` and it shows that instead, until frames arrive — the same fallback a
`<cupri-video>` poster uses, and not a case anyone had to write.

The Showcase deliberately does **not** set one: it briefly carried the video demo's poster, which is
a play button, so a 3D viewport spent its first half-second advertising a control it does not have.
A poster is a still of what is coming; the wrong still is worse than none.

## What it is not

- **Not an engine.** No IBL, animation, skinning, culling, sorting, or alpha blend modes. One
  directional light and a constant ambient term.
- **`Gl` is a static mutable table** — one global context, wrong the moment there are two (a window
  and an offscreen target at once). A library would need this instanced.
- **No disposal or resize contract**, and no error surface beyond a status string.
- **Zero-copy on desktop where the host has a GPU**, and a readback everywhere else. The sample
  implements `IGpuSurfaceSource`, so on the GL window it draws on the host's own context and hands
  the engine a texture-backed `SKImage` — no readback, no row flip, no re-upload. On a host with no
  `GRContext` (a software window) it falls back to the old private-context path, which costs
  ~1.47 ms per frame to move a 512x512 frame. That fallback is not dead weight: it is what a
  software window uses, and what a web or Android host would.

## Two things that cost real time, so they are written down

**A surface must answer `Ticking` honestly.** It is folded into the document's "something is
animating" signal, so a permanently-true `Ticking` stops a render-on-demand host ever idling. It
looks harmless for a surface that never hands the engine a frame, and the paint count stays flat
because there is no damage — but the host spins for ever. It surfaced as a *keyboard* failure:
tabbing stopped reaching text fields. Gate on `RenderNode.LaidOut`, not on "did the painter ask me
for `HostComposited`?" — the display list is rebuilt every tick to compute damage, so the painter
consults surfaces inside `display:none` sections too.

**On the web the hole erases the element's own background.** `ClearHole` uses `BlendMode.Src`, so it
replaces everything painted at that box — including the CSS `background` behind the viewport. On
desktop the model is drawn *over* that background and picks it up for free. Clear the underlay to
the backdrop colour, or identical markup renders near-black on desktop and white in the browser.

## Provenance

`teapot.glb` is the repo owner's own work — mesh from the 3ds Max teapot primitive, base-colour
texture authored alongside it — so it ships with the samples without a third-party licence to track.

It is embedded in `samples/Viewer` and `samples/WebLlvm` only. `Demo3d` is `IsPackable=false` and no
library in `src/` references it, so it appears in the standalone Showcase downloads attached to
releases and in no published package.

## Reproducing the measurements

The probes these files came from are in [`experiments/`](../../experiments/README.md), with
`pwsh experiments/verify.ps1` to rebuild every leg and report what actually passed.
