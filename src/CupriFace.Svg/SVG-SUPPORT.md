# What CupriFace.Svg draws, and what it does not

This package reads inline `<svg>` and hands the engine a list of paths to paint. It is **not** an
SVG renderer in the full sense and is not trying to become one by accident — so this file records
where the line currently sits, and what it would cost to move it.

The first slice was chosen from a survey of 187 designed compositions (#203), where inline SVG was
used for four things: logos and wordmarks, icons in a UI mock, a progress ring, and connecting lines
on a diagram. Everything below "supported" is what those four need.

## Supported

| | |
|---|---|
| Structure | `<svg>`, `<g>`, `viewBox`, `width`/`height`, nesting |
| Shapes | `<path>`, `<rect>` (incl. `rx`/`ry`), `<circle>`, `<ellipse>`, `<line>`, `<polygon>`, `<polyline>` |
| Paint | `fill`, `stroke`, `stroke-width`, `fill-rule`, `opacity`, `fill-opacity` |
| Stroking | `stroke-linecap`, `stroke-linejoin`, `stroke-dasharray`, `stroke-dashoffset` |
| Transforms | `translate`, `scale`, `rotate` (incl. about a point), `skewX`, `skewY`, `matrix`, composed down the tree |
| Hiding | `display:none`, `visibility:hidden` |
| Where they come from | presentation attributes *and* an inline `style` — export tools emit both |

Path data is parsed by Skia, so the whole `d` grammar works, arcs and smooth curves included.
`preserveAspectRatio` behaves as the default `xMidYMid meet`: one uniform scale, centred.

## Not supported, and what each would take

Listed roughly by (how often it appears) ÷ (how hard it is).

### Worth looking at first

- **Gradients** (`<linearGradient>`, `<radialGradient>` + `fill="url(#id)"`). The most common thing
  in this list by a distance — a brand logo very often has one. Needs a `<defs>` index and a paint
  that carries a shader rather than a colour. The engine already has `GradientRect` and a `Gradient`
  type for CSS gradients, so the concepts exist; what is missing is per-*path* shader paint and the
  `objectBoundingBox`/`userSpaceOnUse` coordinate modes. **Currently `fill="url(#…)"` draws nothing
  at all**, which is deliberate: black would be worse.
- **`<use>` and `<defs>`.** Icon sprites are built from these, and an export from a design tool often
  defines a symbol once and instantiates it. Needs an id index and a guard against recursion.
- **`<clipPath>`.** Reasonably common, and the engine already has a clip stack — but it clips to
  rectangles with corner radii today, not to an arbitrary path.

### Real work, less often needed

- **`<text>`.** Wants the whole text stack — shaping, fonts, `textPath`, `dx`/`dy` lists. In the
  surveyed corpus, type in a logo was almost always already converted to paths, which is what design
  tools do on export. Low value for the cost.
- **Filters** (`<filter>`, `feGaussianBlur`, …). A rabbit hole with a large surface; Skia can do the
  primitives, but the filter graph is a language of its own.
- **`<mask>`, `<pattern>`, `<marker>`.** Arrowheads on a diagram come from `<marker>`, which is the
  one of these with a clear use case in the corpus.
- **`<image>`.** Raster inside vector. The engine has an image pipeline already, so mostly plumbing.

### Deliberately out of scope

- **SMIL** (`<animate>`, `<animateTransform>`). CSS animations already drive everything else in the
  engine; a second animation system with its own timing model is not something to own. An app can
  animate the SVG's *attributes* through its model and get the same result.
- **Scripting.** There is no JavaScript engine anywhere in CupriFace, and that is the point.
- **External references** (`xlink:href` to another file, `<use href="other.svg#id">`). A rendering
  pass that fetches is a different kind of component.

## How to tell what happened

Nothing here fails silently. An `<svg>` this package cannot turn into geometry is left unclaimed, so
`CupriDoctor` reports it as `CF0030` exactly as it did before the package existed — the element lays
out, stays empty, and says so. A shape whose `fill` is a paint server it cannot resolve is simply not
filled rather than filled with a guess.

When checking a document that uses this package, pass `configure:` so the checker sees it:

```csharp
CupriDoctor.Check(html, css, configure: doc => doc.UseSvg());
```

## Why this package exists rather than Svg.Skia

[Svg.Skia](https://github.com/wieslawsoltes/Svg.Skia) is a far more complete SVG renderer over the
same SkiaSharp, and it is MIT. Its dependency tree is not: `Svg.Custom`, the adapted fork of
[vvvv/SVG](https://github.com/vvvv/SVG) it is built on, is **MS-PL** — permissive and OSI-approved,
but file-level copyleft on source distribution and GPL-incompatible, which lands on consumers of this
library rather than on us. It would also couple the engine's SkiaSharp version to someone else's
release cadence: Svg.Skia 5.1.1 wants SkiaSharp 3.119.2 against our 3.116.1, and 5.2 has already
moved to SkiaSharp 4.

If the scope above ever needs to grow past what is reasonable to maintain, that trade is worth
revisiting — it is a licence and versioning decision, not a technical one.
