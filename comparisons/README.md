# Comparisons

How CupriFace relates to other ways of building a .NET UI. Each document is a
working comparison — what the two projects share, where they genuinely differ,
and which situations favour which. They are written to be honest in both
directions: every one ends with a *"choose the other one when…"* section, because
a comparison that only ever recommends CupriFace would tell you nothing.

## The one-paragraph positioning

CupriFace is a **UI engine, not an application framework**: a fully managed
.NET pipeline that parses HTML + CSS, lays it out, paints it with Skia, and
binds it to plain C# objects — no browser, no JavaScript engine, no XAML. Hosts
are thin adapters: the same app class renders into a desktop window (Windows,
macOS, Linux), an Android phone, a browser `<canvas>` via WebAssembly, or any
RGBA buffer you hand it (a game texture, a server-side PNG). If your mental
model of a UI is *"a document I style with CSS, driven by a C# object"*,
CupriFace is that model with the browser removed.

## Documents

| Compared with | One-liner | Document |
|---|---|---|
| **Avalonia** | XAML application framework vs HTML/CSS rendering engine — the closest .NET neighbour, and the most instructive contrast | [avalonia.md](avalonia.md) |
| **.NET MAUI** | Native controls per platform vs one renderer everywhere; first-party mobile with iOS, against Linux + browser and headless-testable UI | [maui.md](maui.md) |
| **Flutter** | The one project that made the *same* bet — own the renderer, identical pixels everywhere — executed at ten times the scale, in Dart instead of C# | [flutter.md](flutter.md) |
| **MewUI** | The opposite answer to the same complaint about XAML: fluent C# markup and the smallest possible NativeAOT binary, vs HTML/CSS and run-anywhere. Both projects have since reached the browser, so the dividing lines are now mobile, accessibility and tooling | [mewui.md](mewui.md) |
| **Electron** | The comparison the project was founded on — keep HTML and CSS, delete the browser. What that costs, and when it's worth it | [electron.md](electron.md) |

Planned next (no documents yet): Tauri, Blazor Hybrid.

*All five documents were reviewed in **October 2026** against CupriFace **v0.40.0**, and each names
the version of the project it compares against: Avalonia 12.1.3, .NET MAUI 10.0.110, Flutter 3.47.7,
MewUI v0.22.1, Electron 44.7.0. Shared CupriFace figures, re-measured for this pass: **2,078 tests**,
**79 `cupri-*` elements**, **973 commits**, and the sizes a v0.40.0 release actually attaches —
**20.8 MB** win-x64 single file, **21.0 MB** linux-x64, **19.0 MB** osx-arm64, **20.6 MB** Android APK.*

*The four figures the September pass could only estimate were **all re-measured for this one**, on
the same class of hardware (win-x64, NVIDIA GTX 1060, hardware GL confirmed by reading
`GL_RENDERER` back from the running process rather than assumed). All four moved; three of them
moved against CupriFace:*

| | September (v0.18.0) | October (v0.40.0, measured) |
|---|---|---|
| NativeAOT publish | 25.05 MiB, 5 files | **26.4 MiB (27.7 MB), 5 files** |
| wasm payload | 14.2 MB / 5.5 MB gz | **17.9 MB / 7.3 MB gz** |
| Idle RSS | ~130 MB | **~127 MB** (median of 3, 20 s idle) |
| Cold start to window | ~97 ms | **~1.9 s** warm, **~7.8 s** first run |

***The start-up figure did not survive contact with a stopwatch, and that retires a headline
claim.*** *~97 ms became ~1,925 ms (median of 1,853 / 1,925 / 1,933). Against Electron's typical
1–3 s that is comparable rather than "10–30× faster to a window", which is what
[electron.md](electron.md) said until this pass and no longer says. The NativeAOT build does start in
0.64 s, so the old number most likely came from that build rather than the shipped one — the same
mix-up that produced this set's earlier memory error. Memory, by contrast, held: ~127 MB against
~130 MB, so the ~2.5–4× advantage over Electron stands.*

*What moved on the CupriFace side between those two passes is most of what these documents compare
on. v0.18.0 → v0.40.0 is 27 releases (35 tags, counting a prerelease run): a game controller on all
four hosts with geometric focus navigation, 3D transforms, `clip-path`, `::before`/`::after`, inline
`<svg>`, background images, `z-index`, WOFF 2 fonts, animated `filter`, and files dragged in from
the OS. The test suite went from 818 to 2,078. **`CupriDoctor` did not exist at v0.18.0 at all** —
it arrived in v0.21.0 and is now 20 diagnostic codes, which matters most to [mewui.md](mewui.md),
where tooling was named CupriFace's weakest axis.*

*What this pass did **not** do: re-read all five documents line by line against the engine. It
refreshed every version and every figure, re-checked the gap claims each document leans on (MewUI's
accessibility, mobile and browser rows against v0.22.x; the CSS and input capabilities CupriFace
gained), and corrected what those turned up. Prose about architecture and trade-offs is carried
forward on the September pass's authority, not re-derived.*

*Three findings from the September pass are recorded in the documents rather than smoothed over: the
Windows UIA bridge **did not initialise under NativeAOT** — fixed since by moving it to
source-generated COM ([mewui.md](mewui.md#the-aot-caveat-found-while-measuring)); the download this
project shipped was far larger than it needed to be, fixed in v0.19.0 ([electron.md](electron.md));
and the idle-memory figure those documents carried for a year was measured on the software fallback,
so the memory advantage over Electron is ~2.5–4×, not the order of magnitude previously claimed.*

## Ground rules for these documents

- Claims about CupriFace come from this repository — the docs
  ([DESIGN.md](../DESIGN.md), [TOOLBOX.md](../TOOLBOX.md)), the test suite, and
  measured numbers from the samples, stated with their conditions.
- Claims about other projects describe their *published, stable* feature set,
  not their roadmaps — and version-sensitive statements name the version they
  were checked against.
- Feature tables mark maturity honestly: CupriFace is young, and several of its
  rows say so.
