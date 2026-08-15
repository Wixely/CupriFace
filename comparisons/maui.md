# CupriFace vs .NET MAUI

This is the comparison that only became fair in August 2026, when CupriFace
grew a real Android host. Before that, "CupriFace vs MAUI" was a category
error — one of them ran on phones and the other did not. Now both put a .NET
UI on an Android device, which makes the *actual* difference visible, and it
is a big one:

**MAUI draws your UI with the platform's own controls. CupriFace draws your UI
itself, identically, everywhere.**

Everything below follows from that single sentence. It decides what your app
looks like, how accessibility works, which platforms you can reach, what you
can test without a device, and how large the thing you ship is.

*Version note: MAUI statements were checked against .NET MAUI in .NET 10 (the
current LTS line). MAUI moves with the .NET release train, so version-sensitive
rows say so.*

## At a glance

| | **CupriFace** | **.NET MAUI** |
|---|---|---|
| Kind of software | UI **engine** — documents in, pixels out, into any surface | **Application framework** — owns the app model, lifecycle, windows, platform APIs |
| Authoring | **HTML + CSS** files + a plain C# model | **XAML** or C# markup; MVVM by convention |
| What draws a control | CupriFace itself (Skia), one code path everywhere | The **platform's native control**, via a per-platform handler |
| Look & feel | **Pixel-identical on every target** — your design, unmediated | **Platform-idiomatic** — a Button is a real Android/iOS/WinUI button |
| Binding | `{{Path}}` on any POCO; controls write back; no INPC | `INotifyPropertyChanged` / compiled bindings / MVVM ceremony |
| Desktop | Windows, macOS, **Linux** | Windows (WinUI 3), Mac Catalyst — **no Linux** |
| Mobile | **Android** (gate-proven on an emulator every CI run) | **Android and iOS**, both first-class and mature |
| Browser | **First-class**: same app class → `<canvas>`, no server, no WebView | **None** — Blazor Hybrid embeds a WebView, which is the opposite direction |
| Platform APIs | **None** — it is a renderer, not an app platform | **Extensive** (Essentials): sensors, geolocation, permissions, file/media pickers, secure storage, connectivity |
| Android runtime | **CoreCLR, mandated** (see below — a Mono codegen defect forced it) | **Mono** by default; CoreCLR-on-Android is newer and opt-in |
| Accessibility | Portable semantics tree + **four hand-built bridges** (UIA, AT-SPI, NSAccessibility, TalkBack), each gated in CI by a real AT client | **Inherited from the native controls** — mature and free, plus `SemanticProperties` |
| Testing | **Headless-first**: 369 tests click, type, fling and pixel-assert with no device or display | Device/emulator UI testing (Appium, .NET MAUI UITest); unit tests cover view-models, not views |
| Android app size | ~20.9 MB APK (arm64, the phone sample, measured) | Broadly comparable for a small app; varies with trimming and linker settings |
| Control set | 69 `<cupri-*>` elements, `role`/`aria-*` baked in | Native controls + a large first- and third-party ecosystem |
| Tooling | Plain text files, any editor; no designer or previewer | XAML Hot Reload, .NET Hot Reload, VS/VS Code tooling, previewers |
| Ecosystem & support | Small, young, one repo | **Microsoft first-party**, LTS servicing, Syncfusion/Telerik/DevExpress, Community Toolkit |
| Embedding | Core capability: `RenderToPixels` into any RGBA buffer | Not the shape — the framework hosts your app |
| Maturity | Pre-1.0, moving fast; a documented CSS subset | Shipping and supported since 2022, Xamarin.Forms lineage before that |

## The fork: native controls vs one renderer

MAUI's model is the one Xamarin.Forms established and it is a good one: you
describe a `Button`, and on Android MAUI creates an `AppCompatButton`, on iOS a
`UIButton`, on Windows a WinUI `Button`. A handler translates your abstract
control into the platform's real one.

What that buys is substantial and mostly invisible until you lose it:

- The control **behaves the way that platform's users expect** — ripple effects
  on Android, the right scroll physics and rubber-banding on iOS, the correct
  focus visuals on Windows.
- **Accessibility comes with it.** TalkBack already knows how to read an
  `AppCompatButton`. VoiceOver already knows a `UIButton`. You inherit decades
  of platform work for free.
- **Text input is the platform's**, so IMEs, autocorrect, dictation, password
  managers and CJK composition all work because you are using the real widget.
- OS updates that restyle native controls carry your app along.

CupriFace's model is the opposite, and it is the same bet [Flutter](flutter.md)
makes: draw
everything yourself, so the UI is **identical everywhere**, because it is
literally the same code producing the same pixels from the same HTML and CSS.

What *that* buys:

- **The design ships intact.** No per-platform surprises, no "why is the
  Android date picker a different shape", no handler customisation to make one
  platform match the mockup.
- **One place to fix a bug.** A layout defect is in the engine, not in three
  handlers with three different platform behaviours behind them.
- **The web is a target, not a WebView.** The same app class renders to a
  `<canvas>`, because "the platform" was never load-bearing.
- **Headless testing is trivial**, because there is no platform underneath to
  stand up. See below — this is the difference with the largest day-to-day
  consequences.

The honest cost of CupriFace's side: everything the platform used to give you
free, this project had to build — and can only claim what it has actually
built. Which brings us to the most interesting axis.

## Accessibility: free and mature vs hand-built and gated

MAUI wins the default case decisively. You use native controls, so screen
readers already understand them; `SemanticProperties.Description` and friends
refine what is already working. On iOS and Android especially, this is years of
platform investment you get for nothing.

CupriFace had to build the whole thing. There is a portable semantics tree in
the engine, and **four separate bridges** carry it to each platform's
assistive-technology protocol: UIA on Windows, AT-SPI on Linux, NSAccessibility
on macOS, and an `AccessibilityNodeProvider` for TalkBack on Android. Each is
young. What makes them defensible is how they are verified: every one has a
**blocking CI gate driven by a real AT client** — FlaUI over the channel
Narrator uses, pyatspi over Orca's, pyobjc over VoiceOver's, uiautomator over
TalkBack's — asserting that named controls appear with real screen rectangles,
that activating from the AT side genuinely changes the model, and that a tap
computed from the *client's own* reported geometry lands on the right control.

Two honest observations from opposite directions:

- **MAUI's is better today**, and if your product's accessibility requirement
  is "must work well for real screen-reader users on iOS and Android
  tomorrow," the mature platform-native path is the safer answer. CupriFace's
  bridges have automated proof but no human screen-reader pass on record.
- **CupriFace's is more auditable.** The semantics are one tree in portable
  code, so "what will a screen reader see" is a headless unit test rather than
  a question about three platforms' handler behaviour. That tree caught a real
  bug in this project's own gate work: a virtualised list container was naming
  itself with every materialised row concatenated — a reader would have heard
  twenty rows read aloud on focus. It was found by a test, fixed in one place,
  and fixed everywhere at once.

## Testing: the difference you feel every day

MAUI views need a platform. A `Button` is not a `Button` until a handler has
made one, which means view-level testing is device or emulator testing —
Appium, .NET MAUI UITest, a running app on real hardware. In practice, MAUI
teams unit-test view-models and accept that the views themselves are covered by
slower, flakier end-to-end automation.

CupriFace's engine does not know whether a window exists. Its 369 tests build
real documents, click, type, drag, **fling with momentum**, compose text with a
simulated IME, and assert on both state and pixels — in milliseconds, in CI, on
any OS, with no display and no device.

That is not merely faster. It changes what gets tested at all. This repository's
Android work is the proof: the phone sample's tap targets are calibrated
headlessly at the emulator's exact dp geometry, so a layout regression fails in
five seconds on a developer's machine rather than twenty minutes later on an
emulator — and when it does reach the emulator, the CI gate drives the real APK
with adb and reads back logcat markers.

## The Android runtime story (a first-hand data point)

This one is unusual, and worth stating precisely because it is easy to
overstate.

CupriFace on Android **mandates CoreCLR** (`UseMonoRuntime=false`, pinned for
every consumer by the package's build targets). It has to: on Mono 10.0.11 the
engine crashes at startup with a native SIGSEGV, and the device tombstone
decodes to a string's *content* word being dereferenced as a string
*reference* — a codegen defect, caught red-handed. The forensics, including the
four-variant matrix that isolated it and an upstream-ready report, are in
[`samples/AndroidProbe/MONO-CRASH.md`](../samples/AndroidProbe/MONO-CRASH.md).

The fair reading: this is a defect our engine's code triggers, not a claim that
Mono is broken for everyone — MAUI ships enormous numbers of working apps on
Mono. But it does illustrate a structural difference. MAUI's default Android
runtime is Mono; CupriFace deliberately chose the other one and pins it, and
that choice is enforced by the package rather than left to the app author.

## "But I want HTML and CSS in MAUI" — Blazor Hybrid

This is the natural objection, and it deserves a direct answer, because
`BlazorWebView` is genuinely the closest thing MAUI has to CupriFace's
authoring model: Razor components, real CSS, running inside a MAUI app on every
MAUI platform.

The difference is what renders them. Blazor Hybrid puts a **WebView** in your
app — WebView2 on Windows, WKWebView on iOS/Mac, Android System WebView on
Android. That means:

- **You are shipping a browser engine again**, with its per-platform version
  differences and its own update story (the Android System WebView is updated by
  the user's device, not by you — so your UI's rendering engine varies across
  your install base).
- There is a **JavaScript interop boundary** between your C# and the DOM.
- You get **all of CSS**, which CupriFace genuinely does not — this is Blazor
  Hybrid's real advantage and it is not small.

CupriFace's whole premise is the same trade the Electron comparison makes:
[keep HTML and CSS, delete the browser](electron.md). Against Blazor Hybrid the
argument is narrower but identical in shape — no WebView, no JS boundary, one
renderer whose behaviour is identical on every platform and pinned by headless
tests, at the cost of a CSS subset.

## Where MAUI is simply ahead

An honest list, and it is the longer one:

- **iOS.** MAUI ships it. CupriFace does not have an iOS host at all. If you
  need iPhones, this comparison is already over.
- **Platform APIs.** MAUI Essentials gives you sensors, geolocation,
  permissions, secure storage, file and media pickers, connectivity,
  notifications. CupriFace has *none* of this — it renders UI; everything else
  is yours to write or find.
- **First-party support.** Microsoft builds it, ships it on the .NET release
  train, services it under LTS, and answers issues. CupriFace is one repository.
- **Native look, native behaviour** — including the platform conventions
  users don't consciously notice until they are wrong.
- **Text input at world scale**, because it is the platform's own: IMEs,
  autocorrect, dictation, password managers.
- **Ecosystem.** Syncfusion, Telerik, DevExpress, the Community Toolkit, a huge
  body of samples, courses and Stack Overflow answers.
- **Tooling.** XAML/`.NET` Hot Reload, previewers, mature debugging and
  profiling on device.
- **Maturity.** Shipping since 2022, with the Xamarin.Forms lineage behind it.
  CupriFace's Android host is weeks old.

## Where CupriFace is genuinely stronger

- **Linux and the browser.** MAUI has neither. CupriFace runs on Linux desktop
  (with a real AT-SPI screen-reader bridge) and in a browser tab on a `<canvas>`
  with no WebView and no server — the same app class as everywhere else.
- **Pixel-identical UI everywhere**, authored in HTML and CSS that a web
  designer can edit without opening an IDE, themed by swapping CSS variables,
  restyled without a rebuild.
- **Headless-first testing** of the actual UI, not just the view-models.
- **Render into anything.** `RenderToPixels` fills any RGBA buffer — a game
  texture, another renderer, a server-side image. MAUI expects to own the app.
- **No INPC ceremony.** The model is a plain object; controls write back to it;
  the engine rebinds and repaints while preserving scroll, focus and drag state.
- **A small, auditable dependency surface** — four MIT packages — versus a
  first-party framework with a deep platform stack under it.

## Choosing

**Choose .NET MAUI when:**

- You need **iOS** — this alone decides most mobile projects.
- You want your app to **look and behave natively** on each platform rather
  than identically across them.
- You need **platform APIs** (sensors, permissions, pickers, notifications) as
  part of the framework rather than as your problem.
- **Mature accessibility on mobile today** is a hard requirement, especially
  where a human screen-reader pass has to hold up.
- You want **first-party backing, LTS servicing and a commercial ecosystem**.
- Your team writes XAML and MVVM, or is coming from Xamarin.Forms.

**Choose CupriFace when:**

- Your targets include **Linux, or the browser** — MAUI reaches neither.
- You want **one design rendering identically everywhere**, authored in HTML
  and CSS, with all behaviour in C#.
- You want the **UI itself under fast headless tests**, not just view-models.
- You need to render UI **into something you own** — a game, a render loop, an
  offscreen buffer, a server.
- You want **no WebView and no JavaScript** anywhere in the stack, but you want
  the web's authoring model.
- Binding plain POCOs with zero ceremony matters more to you than a deep native
  control catalogue.
- You do not need iOS *yet* — and you are comfortable being an early adopter of
  a pre-1.0 engine.

## The honest summary

MAUI is an **application framework** that renders with the platform's controls;
CupriFace is a **UI engine** that renders with its own. MAUI's model gives you
native fidelity, native accessibility, native text input and a first-party
platform underneath — and constrains you to the platforms Microsoft ships
handlers for: no Linux, no browser. CupriFace's model gives you one UI that is
identical everywhere it runs, testable without a device, embeddable in anything
that owns a pixel buffer — and asks you to accept a CSS subset, a young
ecosystem, and (today) no iOS.

The deciding question is rarely about rendering technology. It is: **do you
want your app to look like the platform, or to look like your design?** If the
answer is "like the platform" — or if you need an iPhone build — MAUI is the
correct choice and this document should not talk you out of it. If the answer
is "like my design, everywhere, including a browser tab and a Linux desktop,"
then a framework built on per-platform native handlers is working against you,
and that is the gap CupriFace fills.
