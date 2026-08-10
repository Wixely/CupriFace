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
are thin adapters: the same app class renders into a desktop window, a browser
`<canvas>` via WebAssembly, or any RGBA buffer you hand it (a game texture, a
server-side PNG). If your mental model of a UI is *"a document I style with
CSS, driven by a C# object"*, CupriFace is that model with the browser removed.

## Documents

| Compared with | One-liner | Document |
|---|---|---|
| **Avalonia** | XAML application framework vs HTML/CSS rendering engine — the closest .NET neighbour, and the most instructive contrast | [avalonia.md](avalonia.md) |

Planned next (no documents yet): Electron/WebView2 hybrids, Blazor Hybrid,
.NET MAUI, Flutter.

## Ground rules for these documents

- Claims about CupriFace come from this repository — the docs
  ([DESIGN.md](../DESIGN.md), [TOOLBOX.md](../TOOLBOX.md)), the test suite, and
  measured numbers from the samples, stated with their conditions.
- Claims about other projects describe their *published, stable* feature set,
  not their roadmaps — and version-sensitive statements name the version they
  were checked against.
- Feature tables mark maturity honestly: CupriFace is young, and several of its
  rows say so.
