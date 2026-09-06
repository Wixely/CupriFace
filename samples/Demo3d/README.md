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
| Android | [`samples/AndroidViewer/Teapot3dSurface.cs`](../AndroidViewer/Teapot3dSurface.cs) | **painted** — GLES 3.0 on the host's own `SKGLSurfaceView` context, handed over as a texture |

One shader source serves all three: `#version 330 core` for desktop, `#version 300 es` for Android
*and* the browser — because WebGL2 is GLES 3.0. The three hosts differ in how a GL context is
obtained and how finished pixels reach the screen, never in the rendering itself.

> **Those three files were 543 code lines between them and are now 91.** Everything that was
> integration — acquiring a context per platform, sizing a framebuffer, the texture handoff, the
> readback flip, the state reset, the on-screen gate — moved into the optional
> [`CupriFace.Gl`](../../src/CupriFace.Gl/) package, which is host-agnostic. The drawing itself is
> one shared [`TeapotContent`](TeapotContent.cs) implementing `IGlContent`.
>
> Of the 91 that remain, **56 are the desktop's offscreen-context implementation** — a hidden 1×1
> Silk.NET window supplied through `IGlOffscreenContext`, which is a capability rather than glue and
> is why the package takes it as a factory instead of a dependency. The actual per-host wiring is 17
> lines on Android and 18 in the browser: which model, which clear colour, where the log goes.

## Wiring it into an app

At the **composition root**, never in the shared app class — the rule video already follows, because
each host decides for itself whether 3D is wired at all:

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
  directional light and a constant ambient term. `experiments/PACKAGING-GL.md` sizes what "basic 3D"
  would actually cost from here, and where it stops being basic.
- **`Gl` is still a static mutable table** — one global context, wrong the moment there are two (a
  window and an offscreen target at once). It stays that way because this demo genuinely has one
  context and can afford it. The difference is that it is now the SAMPLE'S choice: `CupriFace.Gl`
  publishes no table of its own, only `GlContext.GetProcAddress`, so an app that needs an instanced
  one builds it without the package standing in the way.
- **Lane chosen automatically.** The package draws on the host's own context and hands the engine a
  texture where there is one (desktop GL window, Android); falls back to a private context and a
  readback where there is not (software window, headless), which costs ~1.47 ms per frame to move a
  512x512 frame; and host-composites in a browser. `ShowcaseModel.Lane3d` reports which, as a fact
  from the viewport rather than an inference.

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
