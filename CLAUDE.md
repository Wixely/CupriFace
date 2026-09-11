# Working on CupriFace

CupriFace renders HTML + CSS to a pixel buffer with Skia. **There is no browser, no DOM at runtime
and no JavaScript.** Read [TOOLBOX.md](TOOLBOX.md) for the component library and
[DESIGN.md](DESIGN.md) for the architecture.

---

## Check your UI. You can, in about ten seconds.

This is the single thing that most changes the quality of work in this repo, and it is the thing
most often missed.

**The engine is forgiving on purpose.** An unsupported CSS property is ignored. An element it has no
primitive for lays out and stays empty. A binding that names nothing renders as empty text. A
container too small for its contents does not clip — the contents paint over whatever follows.
Nothing throws, nothing logs, no test fails. A mistake looks exactly like a layout you have not
finished yet, so **reasoning about the markup will not find it.** You have to check.

Everything below is headless — no window, no GPU, no browser — and runs anywhere `dotnet` does.

### 1. `CupriDoctor` — before you look

```csharp
using CupriFace.Diagnostics;
var report = CupriDoctor.Check(html, css, model: model);   // pass the model — see below
if (!report.IsClean) Console.WriteLine(report);
```

```
error   CF0060 (line 4): {{Enviroment}} does not name anything on DeployModel, so it renders as empty text.
  -> Did you mean {{Environment}}?
warning CF0070 (line 2): <div class='panel'> is 56px tall but its contents need 132px — they overflow
                         it by 76px and paint over whatever follows.
  -> Remove the fixed height and let it grow, or set overflow:scroll. It does not clip on its own.
error   CF0030 (line 6): <img> is not something the engine draws — it lays out, and then stays empty.
  -> Use <cupri-image src="..."> — the engine has no raw <img> primitive.
```

| Code | Finds |
|---|---|
| `CF0010` / `CF0011` | A tag never closed, or a close matching nothing — reported at the line it *opened* on |
| `CF0020` / `CF0021` | An unregistered `cupri-*` tag (with "did you mean"), or a control that can never open |
| `CF0030` / `CF0031` | `<img>`, `<video>`, `<svg>`, `<canvas>` and friends; anything else that produced no output |
| `CF0040` / `CF0041` | `<script>` and `onclick=` — there is no JavaScript engine |
| `CF0050` / `CF0051` | A CSS property or function that is silently ignored |
| **`CF0060`** | **A `{{path}}` that names nothing on the model** — renders as empty text, looks like missing data |
| **`CF0070`** | **Contents that do not fit a fixed-height box** — they overflow and paint over the next element |
| **`CF0071`** | **A box that laid out with no area** but has visible content inside it |
| **`CF0080`** | **Characters no installed font can draw** — they paint as empty .notdef boxes. Under-reports on macOS (its LastResort face matches everything): trust a finding, not its absence |

**Pass `model:` whenever the document has one.** `CF0060` and the box checks are skipped without
it, and those are the two that catch the quietest bugs. A binding typo is invisible in every other
way: unknown property → null → empty string → an element that renders perfectly with nothing in it.

### 2. `RenderToImage` — then actually look

```csharp
using var doc = CupriDocument.Load(html, css);     // or app.CreateDocument()
doc.Bind(model);
doc.Refresh();
using (doc.RenderToImage(w, h)) { }                // throwaway: warms layout + images
using var img = doc.RenderToImage(w, h, bgColor);
using var data = img.Encode(SKEncodedImageFormat.Png, 95);
using (var f = File.Create("ui.png")) data.SaveTo(f);
```

Then **open `ui.png` with the Read tool and look at it.** You can see your own output; a screenshot
settles in one glance what a paragraph of reasoning about flexbox cannot.

### 3. `DumpTree` — what the image cannot tell you

```csharp
Console.WriteLine(doc.DumpTree(maxDepth: 3));
```

```
body.cupri-fine           0,0      340x240
  div.panel               28,28    312x56    << CONTENT OVERFLOWS by 76px
    div.row               40,40    300x34
    div.row               40,78    300x34
  div.after               28,84    312x17
  div.collapsed           28,101   312x0     << EMPTY BOX, has children
```

(`div.after` sitting at y=84 while the rows run to y=150 *is* the overlap, visible as two numbers
rather than as a mysterious picture.)

An image shows you *that* something is wrong; this shows you *what*. A blank rectangle has many
possible causes — `312x0` has one. It is greppable, diffable between runs, cheap to assert on in a
test, and far cheaper in context than reading a PNG. Coordinates are **absolute**, so they are the
numbers to hand to `DispatchClick`.

### 4. `ImageDiff` — did I break anything else?

```csharp
var d = ImageDiff.Compare(before, after);     // render before your change, render after
Console.WriteLine(d);                          // "15,121/81,600 px changed (18.53%) within 14,14 312x129"
if (!d.IsIdentical) Save(ImageDiff.Visualise(before, after));   // changed pixels in magenta
```

This is what makes a broad edit — a variable rename in the cascade, padding on a shared class —
safe rather than nerve-wracking. Eyes are poor at spotting that one row moved three pixels;
subtraction is perfect at it. Default tolerance allows for antialiasing; pass `0` for exactness.

### 5. Driving it

```csharp
var box = HitTesting.AbsoluteBox(node);       // node from doc.Root, or read DumpTree's coordinates
doc.DispatchClick(box.X + box.W / 2f, box.Y + box.H / 2f);
// also: DispatchKey(text, EditKey, KeyMods), DispatchPointerMove/Up
```

Render, click, render again — state changes are checkable too, not just static layout.

### Gotchas

- `doc.Refresh()` before the first render, and throw away one frame before capturing — layout and
  images are warm only after a render.
- `doc.LoadFonts(dir)` if an image will be compared across machines, or you are comparing whichever
  sans-serif each box happens to have.
- Rendering at 2x means laying out at the logical size and scaling the canvas — **not**
  `RenderToImage(w*2, h*2)`, which lays out a wider viewport and gives a different screenshot.
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
