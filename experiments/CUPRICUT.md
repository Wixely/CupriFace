# CupriCut: which of it is the engine's?

[hyperframes](https://github.com/heygen-com/hyperframes) is "write HTML, render video, built for
agents": HTML + CSS compositions with `data-start` / `data-duration` on elements, "seekable"
animation adapters, and a headless Chrome that seeks each frame into FFmpeg for a deterministic MP4.
The proposal is the same product on this engine — a frame-by-frame renderer that agents can drive
and, more importantly, *look at* — as its own MCPSharp-style repository, **CupriCut**.

`PACKAGING-GL.md` is the standard: a question, a measurement, a split, and a decision. The
measurement came first.

---

## Measured before any of this was written

A proof of concept outside the repo rendered the Showcase's Motion page — looping `@keyframes` plus
transitions, the same page a host paints — through `Animate(t)` → `RenderToPixels`, 940×720, CPU,
headless, on a laptop:

| claim | result |
|---|---|
| a frame at `t` re-rendered after seeking away | **byte-identical** |
| the same `t` from a **separate document instance** | **byte-identical** |
| render only | **135 fps** — 7.4 ms/frame |
| 3 s / 90 frames to MP4, raw RGBA piped into ffmpeg (libx264) | **0.74 s**, 181 KB; `ffprobe`: h264, 30 fps, 3.000 s, 90 frames |
| PNG sequence | 45.6 ms/frame — the **encoder** is the cost, not the render |

Determinism is structural, not engineered. The engine's animation clock is a parameter: `Animate(t)`
drives keyframes, transitions, toasts, reorder easing, fling and overscroll, and the only
`Stopwatch` in the engine is for profiling timings. hyperframes has to build "seekable" adapters and
seek a browser to approximate what this gets because time is an argument. That is the whole pitch,
and it is true today with no engine change.

---

## The split, and why it decides everything

The rule is the one `PACKAGING-GL.md` settled on for the 3D: **if a host would want it, it is the
engine's; if only a frame-by-frame renderer wants it, it is Cut's.** Applied to every element the
product needs:

| element | home | why |
|---|---|---|
| **Installable fonts** — `@font-face`, files and directories, TTF/OTF/WOFF/WOFF2, weight axes, a strict "registered faces only" policy | **CupriFace** | Every host needs fonts an app ships; the web hosts already embed two. Cross-machine determinism *requires* the strict policy, and a renderer cannot bolt it on from outside |
| Explicit time — `Animate(t)`, transitions, keyframes | CupriFace (exists) | Unchanged; the thing that makes any of this possible |
| Frame capture — `RenderToPixels`, `Render(canvas)` at scale | CupriFace (exists) | `tools/Screenshots` already renders at 2× this way |
| "Everything is loaded" — a settle signal before the first frame | CupriFace (small addition) | The Screenshots tool warms with a throwaway render and *hopes*; a renderer needs to know images and fonts have arrived. Hosts want it too |
| Video seek-to-time | CupriFace, **phase 2** | The seek slider exists, so the player seeks; a renderer needs the *frame at t* rather than playback. Deferred on purpose |
| **Composition timeline** — `data-start` / `data-duration` / tracks, what is on stage at `t` | **CupriCut** | Time as a *product* is a renderer's concern; a desktop window has no scene clock. App-level, over the engine's model/binding — no engine change |
| **Scripted interaction** — click at 1.2 s, type at 2.0 s | CupriCut | Rides `DispatchClick` and the typing APIs; the engine already takes input without a window. Something a Chrome pipeline cannot do |
| The frame loop, PNG sequences, the ffmpeg pipe, alpha output | CupriCut | ffmpeg never enters the engine |
| MCP server, config, tools, packaging, Docker, the agent skill | CupriCut | House style, below |

Two things fall out of the table. Fonts are the one piece of *engine* work, and they are worth doing
first because every host benefits whether or not Cut ever ships. And nothing Cut needs is a
`CupriDocument` internal: it consumes the `CupriFace` package exactly as Khalkos3D does, so the
engine stays free of timelines and encoders.

---

## The engine work: fonts

What exists: `FontService.RegisterFont(byte[])` and `CupriDocument.LoadFont(byte[])`. Registered
faces win over platform lookup, the first registered family becomes the generic-sans target, and
`WebFonts.props` embeds Noto Sans for the two web hosts. Faces are keyed by
(family, bold ≥ 600, italic) — four styles per family, nearest-style fallback.

What is missing, in the order it should land:

1. **`@font-face`.** The CSS-native way in, and the one an agent writing HTML expects:
   `font-family`, `src: url(…) format(…)` (multiple sources, first readable wins), `font-weight`
   (single or range), `font-style`. `CssParser.ParseInto` already skips every at-rule but `@media`
   at one line; this is that line. Sources resolve through `SourceResolver` — embedded / file /
   `data:` / policied https — **the same pipeline images and video use**, so an app that ships a
   font next to its logo loads both the same way.
2. **Loading convenience.** `LoadFont(CupriSource)`, `LoadFonts(directory)`, and `CupriApp.Fonts`
   as the declarative list an app can enumerate; Cut's `--fonts` flag is one line over it.
3. **Formats.** TTF/OTF/TTC are what `SKTypeface.FromData` reads. **WOFF 1** is zlib per table and
   `ZLibStream` is in the box — half a day. **WOFF 2** is Brotli (`BrotliDecoder`, also in the box)
   *plus* reconstruction of the transformed `glyf`/`loca` tables — a real decoder, one to two days,
   and the second thing to ship, not the first. A font that will not decode must fail with its
   name, not fall silently to Noto.
4. **Matching fidelity.** Four styles per family is enough for a demo and not for a design system.
   Key by weight bucket (100–900) and stretch; nearest-weight per the CSS algorithm. Variable fonts
   need `SKFontArguments` variation coordinates — *verify* SkiaSharp 3.116 exposes them before
   promising the axis.
5. **The strict policy.** `FontPolicy.RegisteredOnly`: a family that would resolve to the platform
   is an error naming the family, not a substitution. This is what Cut sets, and what makes a
   render identical on a Windows laptop and a Linux runner. It is also what the web host should
   run under, where "the platform" is one monospace face.
6. **A report.** `doc.FontReport()` — every family the stylesheet asked for and which face answered
   (registered / platform / default). Cut's `lint` is this plus a verdict.

Proof, in the repo's usual shape: unit tests for parse, resolution, strict failure and WOFF 1;
`tools/Screenshots` moved onto registered fonts, so the committed images stop depending on what the
committing machine had installed; and a **cross-platform determinism gate** — the same text sample
rendered under the strict policy on the win / linux / macOS Viewer jobs, hashes compared. That gate
is the claim Cut will make, tested where Cut cannot test it.

---

## The product: CupriCut

**The house style, taken from `GithubMCPSharp`:** a standalone Streamable-HTTP MCP server on
`ModelContextProtocol.AspNetCore` 1.3.0 / net10.0; `CupriCut.json` with `Cut:` / `Server:` /
`Serilog:` sections, layered under `appsettings` and `.Local` files, env overrides under a
`CUPRICUT_` prefix, command line last; `Hosting/` (password middleware, console icon),
`Configuration/`, `Services/`, `Tools/` as `[McpServerToolType]` classes whose feature toggles are
enforced by the service throwing; console app or Windows Service; `Directory.Build.props` with
`Deterministic` and CI builds; a release matrix of win-x64 / linux-x64 × framework-dependent /
self-contained zips; a multi-arch image on ghcr on tag. **One departure, stated:** the image must
carry ffmpeg. The zips do not; `Cut:FfmpegPath` names it and `probe` says whether it is there.

The safety posture translates directly. A GitHub server is read-only by default with repository
allow-lists; a renderer *writes files*, so the analogue is `Cut:OutputRoot` (the only directory a
tool may write under), `Cut:CompositionRoots` (the only directories it may read compositions and
fonts from), `Cut:MaxFrames` / `MaxPixels` (a runaway request is a disk full), and
`Cut:EnableVideo` (off → no ffmpeg, PNG only).

**Tools** — the agent's loop is *look, adjust, look*, so the first tool is the one that returns a
picture:

| tool | does |
|---|---|
| `render_frame(composition, t, width, height, scale)` | one PNG at `t` — ~7 ms; the preview loop |
| `contact_sheet(composition, times[] \| every, columns)` | N frames tiled into one PNG, timestamped. An agent reads one image and sees a whole motion |
| `render_frames(…, fps, from, to)` | the image list |
| `render_video(…, fps, from, to, codec, alpha)` | raw RGBA into ffmpeg; `alpha` selects a transparent clear + an alpha-capable codec |
| `inspect(composition)` | the timeline: tracks, elements and their windows, fonts requested, images referenced, duration |
| `lint(composition)` | the determinism verdict: platform-resolved fonts, unloaded sources, wall-clock content; hyperframes has the same verb |
| `list_fonts()` / `probe()` | what is registered; whether ffmpeg answers |

A thin `cupricut` CLI with the same verbs over the same services, because a CI job is not an MCP
client.

**The composition format** is plain HTML + CSS in the engine's documented subset, plus timeline
attributes. Adopt hyperframes' names — `data-start`, `data-duration`, `data-track` — deliberately:
agents already know them, and the skills that teach them transfer. Cut's timeline layer keeps
elements out of the document until their window opens and removes them when it closes, through the
ordinary model/binding, so a `@keyframes` on an element starts when the element appears. Interaction
steps are a second track: `data-cut-click="1.2"`, or a JSON sidecar of `(t, action)` pairs.

---

## Risks worth naming before starting

- **The keyframe clock reference.** `Animate(t)` is absolute time. An element that appears at
  `t = 2` must run its animation from its *own* zero, not the composition's. The timeline layer can
  own that by creating the element at its start and offsetting — or the engine may already stamp
  creation time. *Verify first*: it is the one place the "determinism for free" claim could need an
  engine change, and it is a one-hour experiment.
- **First-frame readiness.** Frame 0 rendered before an image or font arrives is wrong and
  deterministic, which is worse than wrong and flaky. The settle signal is a prerequisite, not a
  refinement.
- **The PNG path is the slow path.** 45 ms/frame at 940×720; roughly 100 ms at 1080p. Fine for a
  contact sheet, ten seconds for a 3-second sequence. Encode on a thread pool; the video path never
  pays it.
- **WOFF 2** is a decoder, not a decompression call. Ship TTF/OTF/WOFF 1 first and say so.
- **Cross-machine text.** HarfBuzz and Skia are pinned by the engine, so shaping and rasterising
  match across OSes once fonts do — but *that is a claim until the gate exists*. Build the gate in
  the fonts phase, before Cut promises anything.
- **Interaction timelines and layout.** A click at `t` that opens a dialog changes every later frame;
  the timeline must replay from zero on every seek, or cache per `t`. Replay is correct and cheap at
  135 fps; cache is an optimisation for later.
- **Scope creep toward hyperframes' ecosystem** — block catalog, Lambda rendering, a hosted
  playground. None of that is the engine advantage. The advantage is a renderer that needs no
  browser and an agent that can look at frame 45 in seven milliseconds.

---

## Sizing

| piece | estimate |
|---|---|
| Fonts in CupriFace — `@font-face`, sources, loading API, weight matching, strict policy, report, WOFF 1, tests | 3–4 days |
| the cross-platform text gate | half a day, once the strict policy exists |
| WOFF 2 | 1–2 days, separately |
| CupriCut v1 — skeleton in house style, `render_frame`, `contact_sheet`, `render_frames`, `render_video`, `probe`, CLI, Docker with ffmpeg | 4–5 days (the render loop is the proof of concept) |
| timeline layer + `inspect` + `lint` | 3–4 days, after the keyframe-clock experiment |
| interaction track | 2 days |
| phase 2 — video seek in the engine, audio mux in Cut | not sized; deferred |

---

## Suggested staging

1. **Fonts, in this repository**, released as a CupriFace version — every host gains it whether or
   not the rest follows. The strict policy and the cross-platform gate are part of this stage, not
   the next.
2. **The keyframe-clock experiment**, one hour, before the repository exists: does an element
   created at `t = 2` animate from zero? The answer shapes the timeline layer.
3. **CupriCut, the repository**, house-style skeleton plus the frame tools and the video pipe —
   the proof of concept with a server around it. `render_frame` and `contact_sheet` first, because
   they are what an agent uses to iterate.
4. **The timeline and interaction tracks**, `inspect`, `lint`, the agent skill.
5. **Phase 2**: video seek, WOFF 2, audio.

---

## The decisions this needs

- ~~The name.~~ **Decided: CupriCut.** "Cut" is the film word — a cut, the final cut — so it says
  video without saying animation tool, carries none of Flash's baggage, and describes the
  product: a composition on a timeline is what an editor cuts. The one connotation to correct is
  editing existing footage, which becomes true the day phase 2 lands video seeking.
- **Server-only, or server plus CLI.** The house style is a server; a renderer is also a build
  step. This document assumes both over one set of services.
- **Composition attribute names.** hyperframes' `data-start` / `data-duration` for agent
  familiarity, or the engine's own. This document recommends theirs.
- **Whether fonts wait for the rest.** They should not: they are the engine's to do and the only
  stage with no open question.
