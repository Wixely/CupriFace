# Component packaging

**Status: phases 1–4 built (2026-11); the `dotnet new` templates and the `html.customData`
generator are not.** This is the design for distributing `cupri`-style components as libraries
someone can add to a project — and for guaranteeing that whatever they add, they can override every
part of it. Where the build departed from the original plan, the section says what was built and
why.

Two requirements shape everything below, and they were stated as constraints rather than
preferences:

1. **Build-time only.** No runtime loading, no dropping a zip into a running app.
2. **Everything overridable.** A consumer must be able to change the styles *and* the code of any
   control they receive, without forking the library.

A third followed from the engine's targets: **trim- and NativeAOT-clean.** Nothing is discovered by
scanning assemblies or reading attributes; a library is an object that returns instances, so the
trimmer sees everything it registers.

---

## The unit: a NuGet class library

A component library is an ordinary class library that:

- references `CupriFace`,
- keeps its markup and styles in `Assets/*.html|.css` (embedded into the library's own assembly and
  typed by the resources generator — `Assets.Counter.Css.ReadText()`),
- implements one `ICupriComponent` per tag,
- implements `ICupriComponentLibrary` once, and exposes an extension method:
  `registry.AddAcmeControls()`.

```csharp
public sealed class AcmeControls : ICupriComponentLibrary
{
    public string Name => "Acme.Controls";      // named in conflict messages and doctor findings
    public string Prefix => "acme-";            // every tag carries it — enforced
    public IEnumerable<ICupriComponent> Components => [new RatingComponent(), new CardComponent()];
    public string Css => Assets.Acme.Css.ReadText();   // shared tokens; optional
}

public static class AcmeControlsRegistration
{
    public static ComponentRegistry AddAcmeControls(this ComponentRegistry r) => r.Add(new AcmeControls());
}

// the app
public override ComponentRegistry Components => base.Components.AddAcmeControls();
```

**Proven by a real second library**: `samples/ComponentLibrary` is the `Leaf.Controls` package
(`leaf-counter` with behaviour, `leaf-card` with a markup template, `leaf-status`), each with its
CSS — and two of them their markup — in `Assets/`. `samples/ComponentLibraryApp` consumes it two ways from the same
source: a ProjectReference (`dotnet run`) and the packed `.nupkg` restored from a local folder
(`-p:LeafFromPackage=true`, see the README beside it). The test suite references the library too, so
the contract below is pinned against a real package with real embedded assets, not test doubles.

Distribution needs no public feed: this repo already attaches `.nupkg` files to its releases and
tells people to add the folder as a local source, so "here is a zip" and "here is a package" are the
same artifact.

**What a library author outside this repo still needs.** The resources pipeline
(`CupriFace.Resources.targets` plus the `CupriFace.Resources.Gen` generator) is imported by path in
this repository and is not shipped inside any package yet. An outside author can embed assets with
a plain `<EmbeddedResource LogicalName="Assets/…">` and read them with
`CupriSource.Embedded(typeof(X).Assembly, "Assets/…")` — the generator only adds the typed
accessors. Shipping the targets in the CupriFace package (opt-in, so an app's own `Assets/` folder is
not reinterpreted) is the next step.

## Out of scope

**Runtime-loadable packages.** A manifest + template + CSS that an app loads at run time, with no
build step, is a genuinely different feature — its value is no-compile distribution and the safety
of executing no third-party code. It is not needed for the requirement here, so it is deliberately
excluded rather than left ambiguous.

---

## The override contract

"You can change it" is a **guarantee with a test behind it** (`ComponentPackagingTests`).

### 1. Styles — a cascade in layers

`ComponentRegistry.AggregatedCss` is emitted in layers, and the document parses it BEFORE the app's
stylesheet; later rules win ties:

| Order | Layer | Comes from |
|---|---|---|
| 1 | built-in | `ComponentRegistry.Default()` |
| 2 | library | `Add(library)` — the library's shared `Css`, then its components' `DefaultCss`, library by library |
| 3 | app | `Add(component)`, `Replace(component)`, `Register(component)` |
| 4 | app stylesheet | `CupriApp.Css` / `CupriDocument.Load(html, css)` |
| 5 | template | a `<style>` in the markup |

So at **equal specificity**: an app's `.leaf-status.down { … }` beats the library's; a library's
`.cupri-button { … }` beats the built-in; a replacement's CSS beats what it replaced (it moves to the
app layer rather than keeping the built-in's slot); and none of it depends on the order the calls
were made in. Specificity still beats order, as in any cascade — a library rule written as
`.acme-card .cupri-button` outranks an app's bare `.cupri-button`, and the app answers it with the
same selector. Tokens work the same way: a library's `:root { --leaf-accent: … }` is overridden by
the app's own `:root` rule.

**Component class names are public API.** The moment consumers style `.cupri-button` or
`.cupri-tt-bubble`, renaming one is a breaking change. That price is paid deliberately; it is
recorded in the release notes and TOOLBOX §8.

**CSS collisions between libraries** are prevented by convention, not scoping: a library's classes
carry its prefix (`.leaf-card`). The prefix is enforced for TAGS; it cannot be for classes, because a
library legitimately restyles built-ins it uses (`.leaf-counter .cupri-button`).

### 2. Code — unsealed, `Expand` overridable

Every concrete built-in component is now unsealed. The overridable surface is the
`ICupriComponent` contract itself — `Tag`, `DefaultCss`, `Expand` — and nothing else; helpers stay
private or `protected static` on `ComponentBase`.

```csharp
public sealed class SquareButton : ButtonComponent
{
    public override string DefaultCss => base.DefaultCss + ".cupri-button.square { border-radius:0 }";
    public override void Expand(IElement el) { base.Expand(el); el.ClassList.Add("square"); }
}
registry.Replace(new SquareButton());                 // or override Tag and Add it beside the original
```

A component that cannot be subclassed (a sealed one in somebody's library) can still be wrapped:
`registry.Replace(new Logged(registry.Find("acme-card")!))`.

### 3. Identity — explicit add and replace

Built: the registry has a verb for each intent, and the ambiguous one now leaves a trace.

| Call | Tag free | Tag taken |
|---|---|---|
| `Add(component)` | registers (app layer) | **throws** `ComponentConflictException`, naming the owner |
| `Add(library)` | registers all (library layer) | **throws**, and registers none of them |
| `Replace(component)` | **throws** — a misspelled override would add a dead control | replaces (app layer) |
| `Register(component)` | registers (app layer) | replaces, records a `ComponentConflict`; doctor **CF0022** |

A library only ever ADDS. Replacing a control is the app's decision, because the app is the one that
knows which version of `acme-card` it wants; no package can quietly take over a control the app
relies on. A library's tags must all carry its prefix and two libraries may not share one, so
library-against-library tag collisions cannot happen at all. `cupri-` is reserved for CupriFace's
own packages (Lottie, SVG), which register through the same API.

`Register` was kept, not obsoleted, because every existing app uses it and its behaviour for a new
tag is exactly `Add`'s. The plan said "make a silent double-Register a warning"; a warning needs
somewhere to be seen, and the doctor is where this repo puts them.

### 4. Eject — not built

A `dotnet new` template that emits a component's source into the consumer's project. Still the
right escape hatch; `samples/ComponentLibrary` is the shape a library template would scaffold.

---

## Behaviour: `ICupriBehaviour`

```csharp
public interface ICupriBehaviour { void Attach(CupriDocument document); }
```

A component (or a whole library) implements it alongside `ICupriComponent`. The document calls
`Attach` **once per document** — in `UseComponents`, or at the next rebuild for a component added to
the registry afterwards — and the behaviour registers ordinary document handlers keyed on
attributes its own expansion emits:

```csharp
public void Attach(CupriDocument doc) =>
    doc.OnAction("data-leaf-step", e => Step(e.Element, e.Value, e.Model));
```

The plan called this `Wire`; it is `Attach` because that is what it does to a document. Three
decisions in it:

- **Keyed on attributes, not attached to elements.** The DOM is rebuilt from the template on every
  model change and keystroke, so an element a handler was hung on is gone a moment later. An
  attribute-keyed handler finds its markup again in whatever DOM is current — the reason the
  built-in `data-*` hooks always survived a rebuild — so nothing needs re-attaching. Tested across
  `Refresh()` and a re-`Bind`.
- **Per-document state lives in the `Attach` closure.** The component instance is shared by every
  document using the registry; per-element state lives in the model or in re-emitted attributes.
- **No detach.** A component replaced after a document attached it keeps its handlers on that
  document. Compose the registry first, hand it over second — which `CupriApp.Components` does.

The available seams are the ones apps already use: `OnAction`, `OnPointer`, `OnManipulate`,
`OnWheel`, `OnSubmit`, `OnContext`, `OnRebuilt`. Attribute names carry the library prefix so two
libraries cannot answer each other's clicks.

`cupri-video`'s engine wiring off `data-video-role` was left as it is: moving a built-in onto the
seam would change nothing a consumer can observe, and the seam is proven by the library instead.

## Binding order — kept, measured cost avoided

`Rebuild` binds *then* expands, so a component cannot emit `{{Path}}`. This turned out to cost
nothing in practice: the binder already marks every pure-binding attribute with
`data-bind-<attr>="Path"`, so `<leaf-counter value="{{Guests}}">` arrives at `Expand` with
`value="2"` AND `data-bind-value="Guests"` — one attribute in the markup, as the built-in rating
already does. Two-pass binding was therefore not adopted. One catch, documented in the sample: the
engine also uses `data-bind-value` to identify a TEXT field, so a control that is not one moves the
path to an attribute of its own.

## Diagnostics

- **CF0022** (warning): a tag registered twice through `Register`, the second silently winning.
- **CF0020** covers library prefixes: an unregistered `<leaf-cuonter>` is an error with "did you
  mean `<leaf-counter>`?". Any other hyphenated tag nothing handles is noted at **info** — the case
  of a page checked with a registry that lacks its library.

## The `cupri` tool and libraries

`cupri preview` / `cupri check` build their registry with `ComponentRegistry.Default()` and cannot
add a library: loading one from a path would mean loading an assembly and finding its
`ICupriComponentLibrary` by reflection, which is exactly what this design rules out. What the tool
does instead is say so — a library tag shows up as a CF0020 note naming the reason, rather than as an
unexplained empty box. To preview a page that uses a library, check it where the registry exists:
`CupriDoctor.Check(html, css, CupriDoctor.DesktopAndPhone, registry, model)` and `DeviceSheet.Render`
in a test or a tiny console app (`samples/ComponentLibraryApp` is one).

## IDE support — not built

- **`html.customData`, generated from the registry**, for tag completion in VS Code. Would need
  attribute metadata the components do not carry yet.
- **`dotnet new` templates**: one to scaffold a library, one to eject a component's source.

---

## Phases

| # | Work | Status | Proven by |
|---|------|--------|-----------|
| 1 | Style-override contract: layered CSS + class names as API | **built** | app beats library beats built-in at equal specificity, independent of call order |
| 2 | `ICupriBehaviour` — components carry their own handlers | **built** | the library counter's own click handler fires, across refresh and rebind; attach-once-per-document |
| 3 | Unseal; `Add` / `Replace`; CF0022 for a silent double-register | **built** | subclass-and-replace of a built-in and of a library control; wrapper via `Find`; conflict rules |
| 4 | Library shape + docs | **built** (template not) | `samples/ComponentLibrary` consumed by ProjectReference and by its `.nupkg` |
| 5 | `html.customData` generator | not built | — |

---

## Open decisions (one-way doors) — as taken

- **Unsealing is irreversible.** Taken: every built-in is subclassable, with the surface narrowed to
  the three `ICupriComponent` members. Their expansion shape is now a compatibility obligation.
- **Component class names are public API.** Taken, and stated in the release notes.
- **Two-pass binding.** Not taken — the `data-bind-*` marker makes it unnecessary.
