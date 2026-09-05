# Should the 3D become a package?

`PROMOTING.md` scoped moving the probes into the engine, and its retrospective is the reason this
document exists: last time the risk I named up front was not the one that bit. That is the standard
this needs to beat.

The question here is narrower than it looks, because two different things are currently bundled in
`samples/Demo3d` and only one of them is CupriFace's to own.

---

## Recommendation

**Ship the seam. Do not build a 3D engine. Do not start a second repository.**

Concretely: one optional package, `CupriFace.Gl`, whose whole claim is *"a correctly set-up GL
viewport bound to a CupriFace element, on every host"*. A minimal glTF viewer can ride on top later
and is scoped separately at the end — it is a different decision with a different answer.

---

## The split, and why it decides everything

| | what it is | who can build it | how bounded |
|---|---|---|---|
| **The seam** | acquiring a GL context per host, the proc-address dance, state discipline, the texture-backed `SKImage` handoff, the web underlay `<canvas>` | **Only this repo.** It depends on `SkiaWindow`, `AndroidHost` and `WebUnderlays` internals | Finite. The hosts are known and there are three of them |
| **The renderer** | glTF parsing, PBR shading, lighting, animation, culling | Anyone. three.js, Stride and Godot already have | Unbounded. There is no version where it is "done" |

The differentiator is not "CupriFace has a renderer". It is **3D composited with real HTML/CSS UI,
on five platforms including AOT wasm** — and that sentence is entirely about the seam. Nobody
chooses a UI engine for its image-based lighting.

### Why not a separate repository

- The valuable half **cannot leave**. It reaches into host internals, so a separate repo would take
  CupriFace as a dependency and permanently lag its releases — and every host change would break it
  from a distance.
- The gates would live away from the code they protect. The web underlay gate and the Android 3D
  gate both caught real defects; splitting them from the thing they guard is how that stops.
- The half that *could* leave is the half not worth maintaining.

---

## What already exists, and what it is worth

Measured, not estimated:

```
samples/Demo3d/            848 lines   Gltf (348) + GlRenderer (153) + SceneRenderer (347)
samples/Viewer/            273 code lines   desktop surface   (GPU + readback fallback)
samples/AndroidViewer/     116 code lines   Android surface   (GPU only)
samples/WebLlvm/           154 code lines   web surface       (host-composited underlay)
```

The three host surfaces share **31 identical lines** across all three, 62 between desktop and
Android, ~35 for each other pair. That is the real shape of the problem: they are not variations on
one file, they are three genuinely different integrations that happen to share asset loading. A
package cannot just merge them; it has to offer the *common contract* and let each host implement it.

For comparison, **`CupriFace.Lottie` is 302 lines** — and that number is misleading in a way worth
naming, because Lottie delegates the hard part to Skia's own Skottie. **There is no Skottie for 3D.**
Whatever ships here, this repo maintains in full, forever, across three drivers.

---

## What must change before it is a package

These are not polish. Each is something a consumer would hit and could not work around.

| # | today | why a package cannot ship it | evidence |
|---|---|---|---|
| 1 | **`Gl` is a static mutable table** of 54 entry points | One global context. Wrong the moment an app has a window *and* an offscreen target — and a library cannot dictate that an app has only one | already flagged in `samples/Demo3d/README.md` |
| 2 | **Fixed 512×512 render size** | The viewport must follow the element's device resolution. A 3× phone upscales ~2× | seen on a real handset |
| 3 | **State reset is documented, not enforced** | A consumer *cannot debug* the bound-sampler-object failure. It has to be impossible to hit, not written down | cost a full debugging session; invisible on one driver, obvious on another |
| 4 | No disposal or resize contract | GL objects leak on element removal; a resized element keeps a stale framebuffer | — |
| 5 | Error surface is a `string Status` | A consumer needs to branch on "no GL here", not parse prose | — |
| 6 | Each host surface hardcodes one key, one model, one size | It is a demo shape, not an API | — |
| 7 | The web leg needs `<DirectPInvoke Include="emscripten" />` **in the consumer's csproj** | Must be injected via `buildTransitive`. **Lottie never needed this** — it would be the first optional package that configures the consumer's build | `samples/WebLlvm/WebLlvm.csproj:75` |

Item 7 is the one most likely to be underestimated. `CupriFace.Web.NativeAot` already ships
`buildTransitive` props and is the precedent, but it means this package must ship MSBuild that
applies only to wasm consumers and must not disturb desktop or Android ones.

---

## Risks worth naming before starting

`PROMOTING.md` named "regressing video" as the risk and was wrong — video never broke in a browser.
What actually cost time was **ordering** and **inherited state**, neither of which appeared in the
estimate. So the honest list here leans on that experience rather than on what feels dangerous.

- **Driver divergence is the real risk, and it is invisible locally.** Every rendering defect in this
  work so far passed CI, passed 800+ unit tests, and looked correct on one desktop driver. Two were
  found only by a person running it on a phone. A package multiplies that surface by every consumer's
  hardware, and *we will not have their machine*. This argues for shipping the seam (small, testable)
  rather than the renderer (large, driver-sensitive).
- **The emulator cannot stand in.** The Android gate answers `SwiftShader` — software GL. It proves
  the code path and not the driver, which is exactly the axis that breaks.
- **A package is a promise about the `Gl` table.** Once public, its shape is API. Item 1 must be
  fixed *before* the first release, not after.
- **Scope creep is the failure mode with no error message.** "Basic 3D needs" is not a specification.
  The first three issues will be animation, skinning and IBL, and each is reasonable in isolation.

---

## Sizing

| | |
|---|---|
| Items 1–7 above | **1–2 weeks**, and the estimate is least reliable on 1 and 7 |
| Public API design, docs, samples | days |
| Gates for the package itself, per host | days |
| **A glTF viewer worth depending on** | **months**, and it competes with libraries that have had years |

---

## What would make this a bad idea

Stated plainly, because a scoping document that only argues for the work is a sales pitch:

- **If nobody has asked for it twice.** The trigger here was one report of agents not finding hybrid
  zoom — a *discoverability* problem, which was fixed by naming four constructors. That is weak
  evidence for a 3D package. One person wanting to show a model is not a package's worth of demand.
- **If the maintenance is not wanted.** This is a permanent commitment to three GL drivers on five
  platforms, and the bugs will arrive as "it looks wrong on my phone" with no reproduction.
- **If the sample already suffices.** `samples/Demo3d` is copyable today, ~1,900 lines, MIT, and a
  consumer who copies it can change it. That is a legitimate answer, and cheaper than a package.

**The middle path, if unsure:** keep the renderer as a copyable sample and ship only
`CupriFace.Gl` — the part that is genuinely impossible to write outside this repo. It is the
smallest thing that removes the real barrier, and it does not commit anyone to owning a renderer.

---

## The decision this needs

1. Is there demand beyond one request? (If not, stop here — the sample is the answer.)
2. Seam only, or seam plus a minimal viewer?
3. Who owns "it looks wrong on my phone" for the next two years?

Questions 1 and 3 are not engineering questions, which is why this document stops at them.
