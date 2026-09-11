# Working on CupriFace

CupriFace renders HTML + CSS to a pixel buffer with Skia. **There is no browser, no DOM at runtime
and no JavaScript.** Read [TOOLBOX.md](TOOLBOX.md) for the component library and
[DESIGN.md](DESIGN.md) for the architecture.

---

## Look at the UI you changed. You can, in about ten seconds.

This is the single thing that most changes the quality of work in this repo, and it is the thing
most often missed.

**The engine is forgiving on purpose.** An unsupported CSS property is ignored. An element it has no
primitive for lays out and then stays empty. Nothing throws, nothing logs, no test fails — the box
is just blank. A mistake looks exactly like a layout you have not finished, so *reasoning about the
markup will not find it*. You have to check it.

Two cheap checks, both headless — no window, no GPU, no browser, and they run anywhere `dotnet`
does:

### 1. `CupriDoctor` — before you look

```csharp
using CupriFace.Diagnostics;
var report = CupriDoctor.Check(html, css);         // or Check(html, css, app.Components)
if (!report.IsClean) Console.WriteLine(report);
```

```
error CF0030 (line 6): <img> is not something the engine draws — it lays out, and then stays empty.
  -> Use <cupri-image src="..."> — the engine has no raw <img> primitive.
warning CF0050 (line 8): CSS property 'float' is not supported and was ignored.
  -> Use flexbox (display:flex) — there is no float layout.
```

It catches unclosed tags, browser habits (`<img>`, `<script>`, `onclick=`), unknown `cupri-*` tags
with a "did you mean", controls that can never open, and silently-ignored CSS. It reads the real
engine rather than a hardcoded list, so it does not go stale.

### 2. `RenderToImage` — then actually look

```csharp
using var doc = CupriDocument.Load(html, css);     // or app.CreateDocument() for a CupriApp
doc.Refresh();
using (doc.RenderToImage(w, h)) { }                // throwaway: warms layout + images
using var img = doc.RenderToImage(w, h, bgColor);
using var data = img.Encode(SKEncodedImageFormat.Png, 95);
using (var f = File.Create("ui.png")) data.SaveTo(f);
```

Then **open `ui.png` with the Read tool and look at it.** That is the whole point — you can see
your own output. A screenshot settles in one glance what a paragraph of reasoning about flexbox
cannot.

### Driving it

Render, click, render again — state changes are visible too, not just static layout:

```csharp
var box = HitTesting.AbsoluteBox(node);            // node from doc.Root
doc.DispatchClick(box.X + box.W / 2f, box.Y + box.H / 2f);
// also: DispatchKey(text, EditKey, KeyMods), DispatchPointerMove/Up
```

### Gotchas

- `doc.Refresh()` before the first render, and throw away one frame before capturing — layout and
  images are warm only after a render. `tools/Screenshots` discards a first frame for this reason.
- `doc.LoadFonts(dir)` if the image will be compared across machines, or you are comparing whichever
  sans-serif each box happens to have.
- Rendering at 2x means laying out at the logical size and scaling the canvas — **not**
  `RenderToImage(w*2, h*2)`, which lays out a viewport twice as wide and gives a different
  screenshot rather than a sharper one.
- `tools/Screenshots` is a worked 12-page example if you need surfaces, dark mode or 2x capture.

### For a running window

`CUPRIFACE_FRAME_DUMP=out.png` dumps what the window actually presented, read back from the render
target — ground truth when a live window looks wrong but the document seems right.

For the WASM/browser host, drive the canvas with the Playwright MCP.

---

## Other things worth knowing

- **Trimming and AOT fail silently here.** A trimmed build can lose hardware GL or the accessibility
  bridge and still render, so it looks fine. Check the console line naming the renderer before
  concluding anything about a published binary.
- **`PublishSingleFile` needs `IncludeAllContentForSelfExtract`** — Silk.NET finds SDL2/GLFW through
  `Assembly.Location`, which is dead inside a bundle. See `samples/DpiProbe/DpiProbe.csproj`.
- **Release notes are written in the same commit as the change**, under `## Unreleased` in
  [RELEASE-NOTES.md](RELEASE-NOTES.md). CI splices the section into the GitHub release.
- **Prefer `LibraryImport` over `DllImport`** (AOT), and mark Windows-only types
  `[SupportedOSPlatform("windows")]`.
- Every snapshot sample writes a PNG and exits, headless — `samples/HelloBox`, `samples/HtmlView`,
  `samples/Interactive` and ~9 others are runnable references for all of the above.
