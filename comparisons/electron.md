# CupriFace vs Electron

This is the comparison CupriFace was founded on. The project's own one-line
description — *"an Electron alternative that does not embed a web browser or a
JavaScript engine"* — is a claim about this document, so it deserves the most
sceptical treatment of the set.

The premise is narrow and specific. Electron's insight was correct and has been
vindicated by a decade of shipped software: **HTML and CSS are an excellent way
to describe a user interface**, and the people who can write them are
everywhere. CupriFace does not dispute that. It disputes the *implementation* —
that keeping the authoring model should require shipping Chromium and V8 inside
every application.

So the question this document answers is not "is Electron bad." It plainly
isn't; VS Code, Figma, Slack, Discord and Obsidian are proof. The question is:
**what do you lose when you keep HTML and CSS but delete the browser, and when
is that trade worth making?**

*Version note: Electron statements were checked in August 2026 against Electron
41 (Chromium 146, Node 24 LTS), an 8-week major-release cadence, and a support
policy covering the latest three stable majors. CupriFace statements were
re-checked against this repository in August 2026, after the Android host
landed.*

## At a glance

| | **CupriFace** | **Electron** |
|---|---|---|
| What ships | Managed .NET engine + Skia | **Chromium + Node.js + V8**, entire |
| Authoring | HTML + CSS + a C# model | HTML + CSS + JavaScript/TypeScript |
| CSS support | A real but **documented subset** | **All of it** — whatever Chromium 146 does |
| Behaviour language | **C# only** — no JS engine, ever | JavaScript/TypeScript |
| UI ↔ logic boundary | **None** — the model is a C# object you mutate directly | IPC across a process boundary; `contextIsolation`, preload scripts, serialization |
| Process model | One process | Main + renderer(s) + GPU + utility; a renderer crash is survivable |
| Download size | **23.3 MB** (measured, NativeAOT, full Showcase app) | 80–150 MB installer; 100–300 MB installed |
| Idle memory | **51 MB** (measured, steady state) | ~150–200 MB empty; 300–500 MB for a real React app |
| Cold start to window | **~310 ms** (measured, median of 4) | typically 1–3 s |
| Idle CPU | ~0% — repaints only on damage | Compositor/renderer keep working |
| Dependencies | 4 MIT packages (Skia, HarfBuzz, Silk.NET, AngleSharp) | Chromium + Node + your npm tree |
| Security surface | Small; no JS engine, no remote-code path, no npm | Chromium + V8 + Node + every transitive npm package |
| Security cadence | Patch when you choose | **Track Electron's 8-week majors**; only latest 3 supported |
| DevTools | None | **The best UI debugging tooling that exists** |
| Ecosystem | .NET/NuGet; no UI component market | npm, React/Vue/Svelte/Tailwind — colossal |
| Accessibility | ARIA roles built in; **four bridges (UIA, AT-SPI, NSAccessibility, TalkBack), each CI-gated by a real AT client**; real a11y tree on the web host | **Chromium's** — best-in-class on every platform |
| Text / i18n | HarfBuzz shaping; bidi partial; **IME composition** (engine preedit model → Android + both web hosts) | Every script, every input method, flawless |
| Media | Images; charts drawn by the engine; **WebM video** (browser-decoded on web, VP9+Opus package on desktop) | Video incl. H.264/HEVC, WebRTC, WebGL, WebGPU, PDF, audio |
| Rendering arbitrary web content | **Cannot** — by design | That's the entire point |
| Testing | **Headless-first**: 369 tests click/type/fling/pixel-assert, no display | Playwright/Spectron — real browser automation |
| Embedding | `RenderToPixels` into any RGBA buffer | Electron owns the process |
| Web deployment | Same app → `<canvas>`, 14.2 MB wasm (5.5 MB gzipped) | It *is* web tech, but Electron itself is desktop-only |
| Mobile | **Android** — same app class, ~20.9 MB APK (measured, arm64) | **None** — Electron is desktop-only; phones mean a different stack entirely |
| Track record | Young, pre-1.0 | A decade; some of the most-used desktop software on earth |

## The one idea

Electron's architecture is: *your UI is a web page, so run a web browser.* That
is a completely defensible line of reasoning, and the reason it costs what it
costs is that **a browser is not a rendering library — it is an operating
system for untrusted code.** Chromium carries a JIT compiler, a multi-process
sandbox, a network stack, a media pipeline, an extension system and a
site-isolation security model, because it must safely execute code written by
strangers.

Your application is not written by strangers. You wrote it. Almost none of that
machinery is serving your app — it is serving the threat model of the open web,
which you are not in.

CupriFace's bet is that if you delete the assumption of untrusted code, most of
the weight goes with it. What remains — parse HTML, resolve a CSS cascade, lay
out boxes, shape text, paint with Skia — is a solvable amount of engineering,
and it fits in a 23 MB download and 51 MB of RAM.

The measured consequences, all from this repository on win-x64:

| | CupriFace (Showcase) | Typical Electron app |
|---|---|---|
| Download | 23.3 MB | 80–150 MB |
| Idle RSS | 51 MB | 300–500 MB |
| Cold start | ~310 ms | 1–3 s |

That is roughly **an order of magnitude of memory** and **5–10× of start-up**,
for an app with a comparable amount of UI on screen.

## The boundary that disappears

The size numbers get quoted most, but the architectural difference matters more
day to day.

In Electron, your UI lives in a renderer process and your privileged logic
lives in the main process, and between them is an **IPC boundary you must
design**. Everything crossing it is serialized. Modern, correctly-built Electron
apps run with `contextIsolation` on and `nodeIntegration` off, which is the right
call — and it means UI code cannot simply call your logic. You write preload
scripts, expose a curated bridge API, validate IPC senders, and keep a Content
Security Policy honest. This is real, recurring design work, and getting it
wrong is how Electron apps get CVEs.

In CupriFace there is no boundary, because there is no second language and no
second process:

```csharp
public class Settings { public int Volume { get; set; } = 60; }
```

```html
<cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider>
```

Dragging the slider writes `Volume` on that object. Your business logic reads
the same object. No serialization, no bridge, no `ipcRenderer.invoke`, no
preload script, no marshalling types across a language boundary — and no
category of bug where the UI and the backend disagree about a shape.

There's a corollary for teams already on .NET: an Electron front end means your
application logic is either duplicated in TypeScript or reached through a local
server or IPC shim you now own. CupriFace's UI runs *inside* your .NET process
and calls your existing code directly.

## Security and the upgrade treadmill

This is the strongest structural argument, and it is not about CupriFace being
clever — it is about surface area.

An Electron app inherits the security posture of Chromium, V8, Node.js, every
npm package in its tree, and its own code. Electron ships a major version every
**8 weeks** and supports only the **latest three stable majors**, which means
staying patched is a standing engineering commitment measured in
upgrade-per-quarter, forever. Electron's own security documentation is explicit
that keeping current is the developer's responsibility and that older versions
are "easier targets."

CupriFace has no JavaScript engine, so the largest single class of browser
vulnerability — JIT bugs reachable from script — does not exist in it. There is
no npm dependency tree, so the npm supply-chain attack category doesn't apply.
The whole third-party surface is four MIT packages. Patching is something you
decide to do, not a cadence imposed on you.

The honest counterweight: Chromium is patched by a large, well-funded, deeply
adversarial security programme, and CupriFace is not. A vulnerability in Skia
affects both; a vulnerability in CupriFace's own parsing and layout code has
nobody but this project looking for it. "Smaller attack surface" is a real
advantage; "better-audited code" is not a claim it can make.

## What you actually give up

This is the section that decides most projects, and it is long on purpose.

- **The rest of CSS.** CupriFace implements a documented subset — a genuine one
  (cascade, flexbox, grid, transforms, animations, media queries, variables),
  but a subset. Electron gives you every property Chromium supports, on the day
  it ships. If your design depends on the modern long tail, you will hit walls
  in CupriFace that simply don't exist in Electron.
- **The npm ecosystem.** React, Vue, Svelte, Tailwind, a component library for
  every problem, and an answer on Stack Overflow for everything. CupriFace has
  69 built-in elements and NuGet. This gap is enormous and will not close.
- **DevTools.** Element inspection, live style editing, the network panel, the
  profiler, breakpoints in your UI code. It is the single best UI development
  experience in software, and CupriFace has no equivalent — its answer is
  "render headlessly in a test and assert," which is genuinely good for
  regression safety and no help at all when you're eyeballing a layout.
- **Accessibility maturity.** Chromium's a11y implementation is world-class on
  every platform, with real users behind it. CupriFace now covers four —
  UIA, AT-SPI, NSAccessibility and TalkBack — each gated in CI by a real AT
  client, but they are young: no Text pattern for editable fields, and no
  human screen-reader pass on record. The coverage gap has closed; the
  soak-time gap has not.
- **Text input at world scale.** Full bidirectional text, and IME behaviour
  hardened against every input method in the world. CupriFace's bidi is
  partial, and while it now has a real composition model (marked preedit,
  code-point-aware editing, wired to Android's IME and both web hosts), its
  CJK story rests on headless tests plus a documented manual Gboard pass —
  not on a decade of users.
- **Media.** Video playback, WebRTC, WebGL/WebGPU, PDF rendering, audio. If your
  app plays or captures media, Electron is not merely easier, it is the answer.
- **Rendering web content.** If your product must display arbitrary websites or
  third-party embeds, you need a browser. CupriFace explicitly cannot, and no
  amount of engine work will change that.
- **A decade of production hardening**, an enormous hiring pool, mature
  auto-update infrastructure, and multi-process crash isolation.

## Where Electron's reputation is unfair

For balance, because the "Electron is bloat" trope is lazier than the truth:

- **VS Code exists.** It is an Electron app, and it is fast, deeply
  accessible, extensible and beloved. Electron does not prevent excellence; it
  raises the floor cost and leaves the ceiling where your engineering puts it.
- **Chromium's rendering is a monumental achievement.** CupriFace reimplements a
  thin slice of it and will never approach its completeness. Every CSS edge case
  Chromium handles correctly represents engineering CupriFace has not done.
- **The footprint buys real things** — sandboxing, crash isolation, media, the
  entire web platform. It is not waste; it is a bundle you may or may not need.
- **Shipping to three platforms from one web codebase genuinely works**, today,
  with tooling that has been battle-tested by companies with far more at stake
  than a hobby project.

## Choosing

**Choose CupriFace when:**

- You are a **.NET shop**. Your logic is already in C#, and an Electron front
  end would mean a second language, a second ecosystem, and a bridge between
  them that you maintain forever.
- **Footprint is a product requirement** — a utility, a tray app, an agent, a
  point-of-sale or kiosk client, something running on constrained or many
  machines at once, or anything where 300 MB of RAM per instance is a real cost.
- **Startup latency is user-visible** — a tool people launch dozens of times a
  day rather than leave open.
- You want the **security surface minimised**: no JS engine, no npm tree, no
  8-week upgrade treadmill imposed by someone else's release train.
- You want UI behaviour under **fast headless tests** rather than browser
  automation.
- You need the UI to **render into something you own** — a game, a render loop,
  an offscreen buffer.
- The same UI has to reach **an Android phone** as well as the desktop and the
  browser. Electron does not go there at all; a phone means a second stack.
- Your UI is conventional application UI (forms, tables, charts, panels) that a
  CSS subset covers comfortably.

**Choose Electron when:**

- Your team writes **TypeScript and React** and their productivity in that
  ecosystem is the dominant factor. This is the most common correct answer.
- You need **web-platform capabilities**: video, WebRTC, WebGL/WebGPU, PDF, or
  rendering third-party web content.
- **Accessibility must be proven by real users today.** CupriFace's four
  bridges are automated-client-gated but young; Chromium's are battle-tested.
- You need full modern CSS with no subset caveats, or you depend on a specific
  npm UI ecosystem.
- **CJK input, complex scripts and full bidi** are first-class requirements
  (CupriFace has real IME composition now, but not Chromium's depth).
- You want DevTools, a decade of production precedent, and a large hiring pool
  more than you want a small binary.

## The honest summary

Electron's trade is: **pay ~100–300 MB and ~300–500 MB of RAM, and get the
entire web platform plus its ecosystem.** For an enormous number of products
that is not just acceptable, it is obviously correct — which is exactly why
Electron won.

CupriFace's trade is the mirror image: **give up the web platform's completeness
and its ecosystem, keep HTML and CSS as the authoring model, and get a 23 MB,
51 MB-resident, 310 ms-cold-start application that is C# all the way down.**

The deciding question is usually not about size at all. It's this: **is the web
platform load-bearing in your product, or is HTML/CSS just how you'd prefer to
describe your UI?** If it's load-bearing — media, arbitrary web content, the
npm ecosystem, the full CSS surface — Electron is not overhead, it's the
product, and CupriFace cannot replace it. If HTML and CSS are merely the
authoring model you like, then Chromium is a very large dependency to carry for
a syntax preference, and that is precisely the gap CupriFace was built for.
