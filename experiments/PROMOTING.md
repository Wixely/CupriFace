# Promoting this into main

> **Status: A1, A2, A3 and the demo have landed.** This document is kept as written, because the
> interesting part is now the comparison — what a careful read of the code predicted, against what
> integration actually cost. Scroll to **[What it actually cost](#what-it-actually-cost)** for the
> difference. The Android leg is still open.

The probes answered "can we?". This is "what would it take to ship?", scoped by reading the code
rather than by estimating. Two pieces, and they are worth separating:

- **A — the GL surface seam.** *Any app can render OpenGL into a CupriFace element, on any host.*
  Small, mostly re-pointing code that already works, and it is the capability only CupriFace can give
  anyone.
- **B — a glTF/PBR renderer.** Months, and it competes with three.js and Stride at a job most
  CupriFace apps will never need.

**Ship A. Leave B optional, or never.** Downstream users get the novel thing; the engine does not
take on owning a 3D renderer.

---

## A1 — Generalise the web underlay

### What already exists, and is better than the probe's version

`WebVideo.SyncRects` is the part worth inheriting. Per painted frame it sends each underlaid element:

- its box in **device** pixels,
- **clip insets** against every `overflow` ancestor, re-expressed as a CSS `inset()` clip-path,
- whether it is visible at all,
- the `object-fit` keyword,
- and the **2×3 transform matrix** of the engine's own transform chain, with the translation solved
  so a CSS `matrix()` with `transform-origin: 0 0` lands on the same pixels the painted hole did.

`GlProbe.WebHost` does *none* of that — it syncs a plain box, so it breaks the moment the page
scrolls, the element sits in an `overflow` container, or anything transforms. **Generalising is not
new work; it is pointing finished work at a second kind of element.**

### The split that makes it small

`IWebBridge`'s video surface divides cleanly, and only one half is needed:

| video-specific — leave alone | generic — this is the underlay seam |
|---|---|
| `VideoOpen`, `VideoOpenBytes`, `VideoClose` | |
| `VideoPlay`, `VideoPause`, `VideoSeek` | |
| `VideoMuted`, `VideoVolume`, `VideoLoop` | |
| | `VideoRect(id, x, y, w, h, clip×4, visible, fit, a,b,c,d,e,f)` |

`js_video_rect` in `imports.js` is **already element-agnostic**: it looks an id up in a map and sets
`left/top/width/height/objectFit/clipPath/transform`. Nothing in its body knows what a video is.

### What actually changes

| file | change | new or moved? |
|---|---|---|
| `IWebBridge.cs` | add `UnderlayOpen(id, kind)` / `UnderlayClose(id)`; rename `VideoRect` → `UnderlayRect` | ~5 lines new |
| `WebVideo.cs` | lift `SyncRects` + `Find` out to a shared underlay syncer that walks **any** `HostComposited` surface, not only `Players` | moved, not rewritten |
| `WebHostCore.cs` | `_video?.SyncRects(...)` becomes a call over all host-composited surfaces; widen the straight-alpha condition (`_transparent \|\| _video?.AnyReady`) to "any underlay is live" | ~10 lines |
| `imports.js` | `js_video_rect` → `js_underlay_rect`, body unchanged but the map lookup; add `js_underlay_open` | ~10 lines |
| `main.js` | `videoOpen` gains a sibling that creates a `<canvas>` instead of a `<video>`; `videos` map becomes `underlays` | ~15 lines |
| `Web.Mono` | the same two JS files exist there and must move together | mirror |

**Both web hosts must change together.** `CupriFace.Web.Mono` and `CupriFace.Web.NativeAot` each
carry their own `main.js`/`imports.js`, and the repo's own history says a feature wired into one and
not the other is a bug waiting for a user.

### Risks worth naming before starting

- **Regressing video.** Everything here is load-bearing for `<cupri-video>`, which has a browser gate.
  The refactor must keep the video path byte-identical in behaviour; the existing web-touch gates are
  the safety net and should be run before and after.
- **The straight-alpha condition.** `_transparent || (_video?.AnyReady ?? false)` currently decides
  whether the present path converts to straight alpha. Get this wrong and holes stop being
  transparent — or every app pays a full-frame conversion it does not need.
- **Two hosts, one behaviour.** See above.

## A2 — Expose the GPU context on desktop and Android

The probe owns a private GL context and pays a readback: **draw 0.09 ms, transfer ~1.6 ms** — moving
the frame costs about twenty times rendering it. Handing a surface producer the engine's own GL
context and `GRContext` enables a texture-backed `SKImage` and deletes that.

Not required for A1, and worth doing second: the seam is only useful once something is drawing
through it.

## A3 — Two smaller things

- **`-sMAX_WEBGL_VERSION=2`** must flow through the `CupriFace.Web.*` `buildTransitive` props.
  Without it `emscripten_webgl_create_context` **silently downgrades** to WebGL1 and the first symptom
  is `ERROR: unsupported shader version` from a `#version 300 es` shader — blaming the shader, three
  steps from the cause.
- **A real per-frame hook for surface producers.** `GlProbe.WebHost` renders from the `Ticking`
  property getter, which the file admits is a shortcut. A library cannot ship that.

---

## The demo, and where it goes

**In `ShowcaseApp`, wired at the composition root** — exactly how video already works, and the
`samples/Viewer/Program.cs` comment states the rule:

> *"Video attaches HERE, at the composition root — never in the shared app class, which the wasm host
> also compiles (it must not drag desktop codecs into the browser build)… Without it the video card
> shows its poster with disabled controls."*

That gives the demo three properties for free:

1. **`DemoApp` pays nothing.** It gets markup — a `data-cupri-surface` element with a
   `data-cupri-image` poster — and no reference to any renderer. The web payload of `WebWasm`,
   `WebLlvm` and `AndroidViewer` is unchanged unless their composition root opts in.
2. **Graceful degradation is already implemented.** The painter falls through to the poster when a
   surface has no frames, which is how a video shows its poster today. A host without 3D wired shows
   a still image and a line of text; nothing breaks.
3. **It runs everywhere the Showcase runs**, on whichever hosts opt in.

### What the page should show

The point is the compositing, not the teapot — that is what nothing else on the web can do:

- the model **under** live engine UI, with **translucent** panels over it (measured: `rgba(…,0.45)`
  → alpha 114, `opacity: 0.5` → alpha 127, against 255 for an opaque one),
- a **rounded** hole, since `ClearHole` takes the element's `border-radius`,
- ordinary CSS around it — text wrapping beside it, the card scrolling with the page,
- and a caption saying which lane this host used, because it differs: **painted** into the display
  list on desktop and Android, **host-composited** through a punched hole on the web.

That last line is the demo. "The same app, two compositing strategies, chosen by what the host can
do" is a more interesting claim than "here is a teapot".

### Honest caveat for the showcase

Until A1 lands, the web underlay does not follow scroll, clips or transforms — so a Showcase *page*
that scrolls would visibly break on the web. Either land A1 first, or ship the demo desktop-only at
first and add the web once the underlay is generalised.

---

## What downstream users would get

A surface they can render into, with the host difference handled for them:

```csharp
doc.Surfaces.Register("viewport", new MyGlSurface(...));   // already exists today
```

What is missing for that to be a *library* rather than a probe:

- **`Gl` is a static mutable table** — one global context. Wrong the moment there are two (a window
  and an offscreen target). Real refactor.
- No disposal or resize contract, no error surface, no multi-context story.
- The renderer half (B) has no IBL, animation, skinning, culling, sorting or alpha blend modes.

## Sizing

- **A1** — days. Mostly moving `SyncRects` and widening two interfaces; the risk is regressing video,
  not the new code.
- **A2 + A3** — days each.
- **The demo** — days, once A1 is in.
- **B** — months, and probably should not be CupriFace's job.


---

## What it actually cost

The estimate above was "days, mostly moving `SyncRects`; the risk is regressing video, not the new
code". **The direction was right and the risk was named wrongly.** Video never broke in a browser —
the one video regression was caught by an existing unit test within minutes of writing it (gating the
underlay walk on `HostComposited` pinned every player at its first laid-out box). Everything that
actually cost time was somewhere this document did not look: in **ordering**, and in the difference
between a probe on a dedicated page and a demo inside a real tabbed app.

| what | predicted | what happened |
|---|---|---|
| Move `SyncRects` | the work | ~an hour, and it went in clean |
| Regressing video | **the** risk | never happened; a unit test caught the one attempt |
| `-sMAX_WEBGL_VERSION=2` | listed as "smaller" (A3) | correct, and it was one line |
| A canvas's **drawing buffer** | not mentioned | CSS-sized right, rendering into the 300x150 default and stretched — invisible to every managed test |
| `Ticking` semantics | not mentioned | a constant `true` stopped the host ever idling; surfaced as a **keyboard** failure |
| Startup ordering | not mentioned | three separate bugs, below |

### The three ordering bugs, which are the real lesson

None of these are visible from reading the code. All three needed the thing to be *running inside a
real app*, which is exactly what a probe on its own page cannot provide.

1. **A stale selector.** After moving to the seam, `Size()` asked about the new underlay id while the
   context request still named the probe's hand-rolled `#gl3d`. The canvas existed and was correctly
   sized; only context creation failed, and it reported as "no WebGL2 context" — three steps from the
   cause. One `const` now defines it once.

2. **The desktop GL thread raced the host's window.** Two concurrent `glfwInit` calls on Windows
   collide with "Class already exists", and the loser is whoever is second. When that was the *host*,
   the whole app silently fell back to the SDL software window — a demo quietly downgrading the
   application it is demonstrating. Fixed by deferring the thread until the first `Ticking` poll,
   which by definition means the host is already rendering. A fixed sleep would have hidden it.

3. **A retry budget that expired on another tab.** The web surface waited a bounded number of frames
   for its canvas — fine on a dedicated page, wrong in the Showcase, where the element does not exist
   until its section is opened, which may be minutes or never. "Absent" is a normal state with no
   timeout, not a slow failure.

### `Ticking` is a contract, and it was not read carefully enough

`ISurfaceSource.Ticking` is folded into the document's "something is animating" signal. Returning a
bare `true` looks harmless for a surface that never hands the engine a frame — and stops a
render-on-demand host **ever idling**. Nothing looked wrong: the paint count stayed flat, because
there was no damage to paint. What broke was tabbing to text fields, so an IME would have opened at
the page origin. Six bisects were needed to get there, because every plausible suspect was innocent:
registering a surface is fine, matching an element is fine, loading the model is fine, and every
declarative property is fine — only a surface that *ticks* broke it.

The first fix was wrong in an instructive way: gating on "did the painter ask me for
`HostComposited`?" looks exact, and is not. The display list is rebuilt every tick to compute damage,
so the painter consults surfaces inside `display:none` sections too, and the guard never expired.
`RenderNode.LaidOut` is the discriminator — the one the video path already relies on for the same job.

### What the gate is worth

`tests/WebTouchGate/UnderlayTests.cs` was written to protect the refactor and immediately found a bug
the refactor had *shipped*. It was also checked for the ability to fail: with the backing-store fix
removed it reports "drawing buffer is 300x150 but its box is 320x320" — the bug, in the words that
name the cause. Both properties matter, and this repo's history says the second is the one usually
skipped.

### Still open

- ~~**A2 — share the engine's `GRContext`.**~~ **Done.** `IGpuSurfaceSource.RenderOnGpu(GRContext)`
  is called on the render thread with the host's context current, before anything is recorded, and
  the registry calls `ResetContext()` afterwards — the step this document named as the reason raw GL
  could not be issued on Skia's context. Measured on the same machine: the readback path spends
  **1.47 ms per frame** moving a 512x512 frame (`glReadPixels` 0.61 + to-`SKImage` 0.86); the
  zero-copy path spends **none**, at 8 us of CPU to submit the draw. Note what that number is not:
  nothing syncs, so it does not say rendering became free — the GPU still does the work. The claim
  is about transport.
- **A3's second half — a real per-frame hook.** Both surfaces still drive rendering from a property
  getter and both say so. A library cannot ship that.
- ~~**Android.**~~ **Done, and two of the three reasons given here were wrong.**

  - *"`AndroidHost.Push(CupriApp)` has no `configure` overload"* — it did not need one. `SwapApp`
    already calls the host-level `_configure` for every document it builds, including the ones a
    push creates, so `CupriActivity.ConfigureDocument` reaches the pushed Showcase. That was
    visible in the code the whole time; I read the `Push` signature and stopped.
  - *"an offscreen EGL context is more than the probe's `GLSurfaceView` did"* — unnecessary once A2
    landed. The Android host renders through `SKGLSurfaceView`, which owns a real `GRContext`, so
    the phone takes the same zero-copy path as the desktop GL window: draw on the host's context,
    hand over a texture. The Android surface has no private context and no readback in it at all.
  - *"no device attached to verify against"* — this one was real, and is answered by the gate rather
    than by trusting the build. The android job now starts the Showcase on the 3D page
    (`am start --es section 3d`) and asserts on the driver's own `GL_VERSION` and then on a frame
    count, because a surface that fails to initialise leaves the viewport showing its panel, which
    looks deliberate and would pass any "did it launch" check.
