# Agent instructions

See **[CLAUDE.md](CLAUDE.md)** — the instructions live there in full, and this file exists so that
agents which look for `AGENTS.md` find them too.

The short version, because it is the thing most often missed and it changes the result the most:

**This engine renders HTML to pixels with no browser, and it fails silently by design.** An
unsupported CSS property is ignored. An unknown element lays out and stays blank. A binding that
names nothing renders as empty text. A container too small for its contents does not clip — the
contents paint over whatever comes next. Nothing throws. You cannot find any of it by reading the
markup, and the screenshot can mislead you too: overflowing content looks like a z-order bug rather
than a height that is too small.

So, headlessly — no window, no GPU, no browser:

```csharp
var report = CupriDoctor.Check(html, css, model: model);  // pass the model, or half the checks skip
if (!report.IsClean) Console.WriteLine(report);

using var img = doc.RenderToImage(w, h);                  // then open the PNG and LOOK at it
Console.WriteLine(doc.DumpTree(maxDepth: 3));             // what the image cannot tell you
var diff = ImageDiff.Compare(before, after);              // did my change touch anything else?
```

`CupriDoctor` names the problem, the PNG shows you the result, `DumpTree` gives the geometry behind
it, and `ImageDiff` tells you what else moved. Full details and gotchas in
[CLAUDE.md](CLAUDE.md).
