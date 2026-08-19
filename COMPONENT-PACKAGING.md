# Component packaging (planned)

**Status: planned, not built.** This is the design for distributing `cupri-*` components as
libraries someone can add to a project — and for guaranteeing that whatever they add, they can
override every part of it.

Two requirements shape everything below, and they were stated as constraints rather than
preferences:

1. **Build-time only.** No runtime loading, no dropping a zip into a running app.
2. **Everything overridable.** A consumer must be able to change the styles *and* the code of any
   control they receive, without forking the library.

---

## What already exists

Most of the machinery is here; the gaps are specific and small in number.

- **Components are a public extension point.** `ICupriComponent` (`Tag`, `DefaultCss`,
  `Expand(IElement)`) is documented in TOOLBOX §8, and its own doc comment states that "first-party
  controls and third-party components implement this same contract". `CupriApp.Components` is
  `virtual`, and `ComponentRegistry.Default().Register(new RatingComponent())` is the documented
  way in.
- **Compound components are normal.** `cupri-table`, `cupri-board`, `cupri-video` and
  `cupri-pagination` already expand one tag into a multi-element subtree with roles and `aria-*`
  baked in. A packaged control is not a new shape of thing.
- **HTML/CSS already live beside the C#.** `CupriFace.Resources.targets` plus the assets generator
  turn `Assets/*.html|.css` into typed `Assets.Foo.Html` / `.Css` sources. This is exactly the
  "keep it all in one folder" story, and it is already reusable MSBuild.
- **The cascade already favours the consumer** (see below).

## Out of scope

**Runtime-loadable packages.** A manifest + template + CSS that an app loads at run time, with no
build step, is a genuinely different feature — its value is no-compile distribution and the safety
of executing no third-party code. It is not needed for the requirement here, so it is deliberately
excluded rather than left ambiguous. If it is ever wanted, it should be justified on those grounds,
not adopted as a packaging shortcut.

---

## The unit: a NuGet class library

A component library is an ordinary class library that:

- references `CupriFace`,
- keeps its markup and styles in `Assets/*.html|.css` (typed via the existing generator),
- implements one `ICupriComponent` per tag,
- exposes a single entry point: `registry.AddAcmeControls()`.

```
Acme.Controls/
  Acme.Controls.csproj        → references CupriFace, pulls in the resources targets
  Assets/Rating.css           → the component's own styles
  RatingComponent.cs          → ICupriComponent: Tag, DefaultCss, Expand
  AcmeControlsRegistration.cs → registry.AddAcmeControls()
```

Distribution needs no public feed to start with: this repo already attaches `.nupkg` files to its
releases and tells people to add the folder as a local source, so "here is a zip" and "here is a
package" are the same artifact. Versioning, updates and transitive dependencies come free, and
VS Code handles it natively (`dotnet add package`, IntelliSense, go-to-definition through the
`.snupkg` symbols already published).

---

## The override contract

The point of this section is that "you can change it" must be a **guarantee with a test behind
it**, not a hope that nothing collides.

### 1. Styles — already works, needs pinning

`CupriDocument` parses component CSS *before* the app's, and later rules win ties:

```csharp
rules.AddRange(CssParser.Parse(_components.AggregatedCss));  // components first
rules.AddRange(CssParser.Parse(_css));                       // the app's CSS second
rules[i].Order = i;                                          // later stylesheets win ties
```

So `.cupri-btn { border-radius: 0 }` in an app stylesheet beats the packaged rule at equal
specificity — no `!important`, no build flag. Two things turn this from a happy accident into a
contract:

- **a test** that an app rule overrides a component rule at equal specificity, so the ordering
  cannot be "tidied" away later;
- **a documented statement that component class names are public API.** The moment consumers style
  `.cupri-btn` or `.cupri-tt-bubble`, renaming one is a breaking change. That is a price worth
  paying deliberately.

### 2. Code — blocked today

Every concrete component is `sealed`:

```csharp
public sealed class ButtonComponent : ComponentBase
```

so the cheap override — subclass, call `base.Expand(el)`, adjust the result — is impossible. The
only route is writing a replacement from scratch, which is all-or-nothing.

**Plan: unseal, and make `Expand` virtual — nothing else.** Overridable members are API you are
committed to, so the surface stays deliberately narrow: override the whole expansion or none of it.
Helpers stay private. This covers "I like this control, but…" without binding the engine to its own
internals.

### 3. Identity — explicit replacement

`ComponentRegistry.Register` writes into a dictionary keyed by tag, so a second registration of
`cupri-slider` silently wins. Today "override" and "collision" are indistinguishable.

**Plan:** add `registry.Replace(tag, component)` for the deliberate case, and make a silent
double-`Register` a warning. Same behaviour, stated intent.

### 4. Eject — the floor

A `dotnet new` template that emits a component's source into the consumer's project, so they own it
outright. This is the escape hatch when subclassing is not enough, and it is the *same* template
work as scaffolding a new library — one piece of work, two uses.

---

## Prerequisites

These are not packaging features, but packaging is not worth shipping without them.

### Behaviour: `ICupriBehaviour`

A component can only produce markup; it cannot bring a handler. Today interactive components work
one of two ways: the app wires it (`doc.OnClick(".signin-submit", …)`, which is what the sample's
sign-in form does), or the *engine* recognises a marker the component emits (`cupri-video`'s
controls do this via `data-video-role`). Neither works for a third party.

**Plan:** components may implement `ICupriBehaviour` with `void Wire(CupriDocument doc)`, called
once per document, so a packaged control owns its own click/submit logic. Without this, a package
can only ship markup plus instructions.

### Binding order

`Rebuild` binds *then* expands, deliberately — "so components see concrete attribute values":

```csharp
BindingEngine.Apply(dom, _model, …);   // bind
_components?.Expand(dom);               // then expand
```

A component therefore **cannot emit `{{Path}}`** — the binder has already passed. A reusable field
must be handed the path as a plain string (`user-path="Email"`) and emit `data-bind-value="Email"`
itself, receiving the current value through a second attribute. It works; it costs two attributes
where one should do.

**Option:** bind → expand → bind again, so components emit `{{path}}` naturally. This runs on every
keystroke, so the cost and the idempotence of a second pass must be **measured before it is
adopted**, not assumed.

### Collisions

- **CSS**: `AggregatedCss` concatenates every component's styles globally. Two libraries will
  eventually fight over a class name. Needs a prefix convention (`acme-*`) or real scoping —
  decided before an ecosystem exists, not after.
- **Tags**: see *Identity* above.

---

## IDE support (VS Code)

- **`html.customData`, generated from the registry.** VS Code reads a JSON file describing custom
  tags and attributes and then offers autocomplete and hover docs for `<cupri-slider min max
  value>` inside `.html` files. Generating it from the registry means *any* library — first- or
  third-party — gets IntelliSense for free. Small generator, disproportionate return.
- **`dotnet new` templates**: one to scaffold a library, one to eject a component's source.

---

## Phases

Each lands independently and is proven before the next, per the house rule that a claim without a
test is not a claim.

| # | Work | Proven by |
|---|------|-----------|
| 1 | Style-override contract: test + document class names as API | a test where an app rule beats a component rule at equal specificity |
| 2 | `ICupriBehaviour` — components carry their own handlers | a headless test: a packaged control's own click handler fires |
| 3 | Unseal + `virtual Expand`; `Replace(tag, …)`; warn on silent double-register | tests for subclass-and-tweak and for deliberate replacement |
| 4 | Library shape + `dotnet new` template + docs | a real second library (extract something from the samples) consumed by the Showcase |
| 5 | `html.customData` generator | the generated file loaded in VS Code, tags completing |

Phase 4 is the one that matters: **a packaging story is not proven by a document, it is proven by a
second library actually being consumed.**

---

## Open decisions (one-way doors)

- **Unsealing is irreversible.** Once `ButtonComponent` is subclassable, its expansion shape is a
  compatibility obligation. Narrow surface (`Expand` only) is the mitigation, not a cure.
- **Component class names become public API** the moment overriding by CSS is documented.
- **Two-pass binding** may cost more per keystroke than the ergonomics are worth. Measure first;
  the `*-path` attribute convention is a perfectly serviceable fallback.
