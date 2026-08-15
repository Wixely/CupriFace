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
| **MewUI** | The opposite answer to the same complaint about XAML: fluent C# markup and the smallest possible NativeAOT binary, vs HTML/CSS and run-anywhere | [mewui.md](mewui.md) |
| **Electron** | The comparison the project was founded on — keep HTML and CSS, delete the browser. What that costs, and when it's worth it | [electron.md](electron.md) |

Planned next (no documents yet): Tauri, Blazor Hybrid, Flutter.

*Last reviewed against the repository in August 2026, after the Android host
landed (four accessibility bridges, engine-level touch and IME composition).*

## Ground rules for these documents

- Claims about CupriFace come from this repository — the docs
  ([DESIGN.md](../DESIGN.md), [TOOLBOX.md](../TOOLBOX.md)), the test suite, and
  measured numbers from the samples, stated with their conditions.
- Claims about other projects describe their *published, stable* feature set,
  not their roadmaps — and version-sensitive statements name the version they
  were checked against.
- Feature tables mark maturity honestly: CupriFace is young, and several of its
  rows say so.
