using AngleSharp.Dom;
using CupriFace.Components.Controls;

namespace CupriFace.Components;

/// <summary>
/// Where a registration came from, which decides where its CSS sits in the cascade
/// (COMPONENT-PACKAGING.md, "The override contract").
///
/// <para>The order is the contract: <see cref="BuiltIn"/> &lt; <see cref="Library"/> &lt;
/// <see cref="App"/> &lt; the app's own stylesheet &lt; a <c>&lt;style&gt;</c> in the template. At
/// equal specificity the later layer wins, so a library can restyle a built-in control it uses, and
/// an app can restyle anything it received — with no <c>!important</c> and no build flag.</para>
/// </summary>
public enum ComponentLayer
{
    /// <summary>The engine's own catalogue, from <see cref="ComponentRegistry.Default"/>.</summary>
    BuiltIn = 0,
    /// <summary>A packaged library, added with <see cref="ComponentRegistry.Add(ICupriComponentLibrary)"/>.</summary>
    Library = 1,
    /// <summary>The app's own components and replacements: <see cref="ComponentRegistry.Add(ICupriComponent)"/>,
    /// <see cref="ComponentRegistry.Replace"/> and <see cref="ComponentRegistry.Register"/>.</summary>
    App = 2,
}

/// <summary>One tag's registration, as <see cref="ComponentRegistry.Registrations"/> reports it.</summary>
/// <param name="Tag">The custom element tag.</param>
/// <param name="Component">The component that expands it now.</param>
/// <param name="Layer">Where its CSS sits in the cascade.</param>
/// <param name="Library">The library that supplied it, when it came from one.</param>
public sealed record ComponentRegistration(string Tag, ICupriComponent Component, ComponentLayer Layer,
                                           ICupriComponentLibrary? Library);

/// <summary>A tag registered a second time through <see cref="ComponentRegistry.Register"/>, which
/// replaced the first silently. <see cref="CupriFace.Diagnostics.CupriDoctor"/> reports each as
/// CF0022, because a deliberate override and an accidental collision are indistinguishable from the
/// outside — <see cref="ComponentRegistry.Replace"/> is how to say which one it was.</summary>
public sealed record ComponentConflict(string Tag, ICupriComponent Previous, ICupriComponent Replacement);

/// <summary>Thrown when a registration would quietly take a tag that is already spoken for.</summary>
public sealed class ComponentConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// Registry mapping custom-element tags to <see cref="ICupriComponent"/>s and expanding
/// them in a DOM (DESIGN.md §10). Expansion runs after data binding, so components see
/// concrete attribute values, and repeats until nested components are fully expanded.
///
/// <para><b>Three ways in, each saying what it means</b> (COMPONENT-PACKAGING.md):</para>
/// <list type="bullet">
/// <item><see cref="Add(ICupriComponentLibrary)"/> — a packaged library. Every tag must carry the
/// library's prefix and none may already be registered, or the whole library is refused.</item>
/// <item><see cref="Add(ICupriComponent)"/> — one new tag of the app's own. Refused if taken.</item>
/// <item><see cref="Replace"/> — the deliberate override of a tag that exists: a subclass of a
/// built-in, a wrapper around a library control. Refused if the tag is NOT taken, because replacing
/// a misspelled tag would otherwise quietly add a second, unused one.</item>
/// </list>
/// <para><see cref="Register"/> is the original last-one-wins call and keeps that behaviour, but a
/// second registration of a tag is now recorded in <see cref="Conflicts"/> and reported by the
/// doctor rather than vanishing.</para>
///
/// <para>Everything here is explicit code — no assembly scanning, no attributes found by
/// reflection — so a registry composed this way survives trimming and NativeAOT unchanged.</para>
/// </summary>
public sealed class ComponentRegistry
{
    private readonly Dictionary<string, ComponentRegistration> _components = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ICupriComponentLibrary> _libraries = new();
    private readonly List<ComponentConflict> _conflicts = new();

    // Registration order per tag, kept separately from the dictionary because CSS order is part of
    // the contract and a Dictionary's enumeration order is an implementation detail — and because a
    // replacement must MOVE to its new layer rather than keep the slot of what it replaced.
    private readonly List<string> _order = new();

    /// <summary>Bumped by every change, so a document holding this registry can tell that CSS or
    /// behaviours were added after it was handed over and pick them up at its next rebuild.</summary>
    internal int Version { get; private set; }

    /// <summary>
    /// Register a component under its tag, replacing any earlier registration of that tag.
    ///
    /// <para>The original API, kept with its behaviour. A replacement through here is recorded in
    /// <see cref="Conflicts"/> (CupriDoctor reports it as CF0022): prefer <see cref="Add(ICupriComponent)"/>
    /// for a new tag and <see cref="Replace"/> for an override, which say which one you meant.</para>
    /// </summary>
    public ComponentRegistry Register(ICupriComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (_components.TryGetValue(component.Tag, out var previous) && !ReferenceEquals(previous.Component, component))
            _conflicts.Add(new ComponentConflict(component.Tag, previous.Component, component));
        Set(component, ComponentLayer.App, null);
        return this;
    }

    /// <summary>Add one new component of the app's own.</summary>
    /// <exception cref="ComponentConflictException">The tag is already registered — use
    /// <see cref="Replace"/> if overriding it is the intent.</exception>
    public ComponentRegistry Add(ICupriComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (_components.TryGetValue(component.Tag, out var existing))
            throw new ComponentConflictException(
                $"<{component.Tag}> is already registered by {Describe(existing)}. " +
                $"Use Replace(new {component.GetType().Name}()) if overriding it is the intent.");
        Set(component, ComponentLayer.App, null);
        return this;
    }

    /// <summary>
    /// Add a packaged component library: its components, its shared CSS, and any behaviour it carries.
    ///
    /// <para>All or nothing. Every tag is checked before any is registered, so a refused library
    /// leaves the registry exactly as it was rather than half-installed.</para>
    /// </summary>
    /// <exception cref="ComponentConflictException">A tag is already registered, a tag does not carry
    /// the library's <see cref="ICupriComponentLibrary.Prefix"/>, or another library already owns that
    /// prefix. A library only ever ADDS tags; replacing one is the app's decision, made with
    /// <see cref="Replace"/>, so no package can silently take over a control the app relies on.</exception>
    public ComponentRegistry Add(ICupriComponentLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var prefix = library.Prefix ?? "";
        if (prefix.Length < 2 || !prefix.EndsWith('-'))
            throw new ComponentConflictException(
                $"Library {library.Name} declares the prefix \"{prefix}\"; a prefix is a word ending in '-', such as \"acme-\".");
        foreach (var other in _libraries)
            if (string.Equals(other.Prefix, prefix, StringComparison.OrdinalIgnoreCase))
                throw new ComponentConflictException(
                    ReferenceEquals(other, library)
                        ? $"Library {library.Name} is already added to this registry."
                        : $"Libraries {other.Name} and {library.Name} both claim the prefix \"{prefix}\". " +
                          "Two libraries sharing a prefix will eventually collide on a tag or a class name.");

        var components = library.Components.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in components)
        {
            if (!c.Tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ComponentConflictException(
                    $"Library {library.Name} registers <{c.Tag}>, which does not carry its prefix \"{prefix}\". " +
                    "A library's tags all carry its prefix, so two libraries can never collide on one.");
            if (!seen.Add(c.Tag))
                throw new ComponentConflictException($"Library {library.Name} registers <{c.Tag}> twice.");
            if (_components.TryGetValue(c.Tag, out var existing))
                throw new ComponentConflictException(
                    $"Library {library.Name} registers <{c.Tag}>, which is already registered by {Describe(existing)}. " +
                    "A library only adds tags; if the app means to override one, it calls Replace() itself.");
        }

        _libraries.Add(library);
        foreach (var c in components) Set(c, ComponentLayer.Library, library);
        if (components.Count == 0) Version++; // its shared CSS alone still changes the stylesheet
        return this;
    }

    /// <summary>
    /// Deliberately replace the component registered for a tag — a subclass of a built-in that
    /// tweaks its expansion, or a wrapper around a library control. The replacement's CSS moves to
    /// the <see cref="ComponentLayer.App"/> layer, after every library, so a replacement styled at
    /// equal specificity wins over what it replaced.
    /// </summary>
    /// <exception cref="ComponentConflictException">Nothing is registered under the tag. Replacing a
    /// misspelled tag would otherwise add a new one nobody uses, and the override would silently do
    /// nothing — use <see cref="Add(ICupriComponent)"/> for a new tag.</exception>
    public ComponentRegistry Replace(ICupriComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (!_components.ContainsKey(component.Tag))
            throw new ComponentConflictException(
                $"Nothing is registered as <{component.Tag}>, so there is nothing to replace. " +
                "Check the spelling, or use Add() for a new tag.");
        Set(component, ComponentLayer.App, null);
        return this;
    }

    /// <summary>The component registered for <paramref name="tag"/>, or null. What a wrapper needs to
    /// decorate a control it cannot subclass: <c>registry.Replace(new Logged(registry.Find("acme-x")!))</c>.</summary>
    public ICupriComponent? Find(string tag) =>
        _components.TryGetValue(tag, out var r) ? r.Component : null;

    /// <summary>Every registration in cascade order — built-ins, then libraries, then the app's own.</summary>
    public IReadOnlyList<ComponentRegistration> Registrations =>
        OrderedRegistrations().ToList();

    /// <summary>The libraries added, in the order they were added.</summary>
    public IReadOnlyList<ICupriComponentLibrary> Libraries => _libraries;

    /// <summary>Tags registered more than once through <see cref="Register"/>, each a silent
    /// replacement. Reported by CupriDoctor as CF0022.</summary>
    public IReadOnlyList<ComponentConflict> Conflicts => _conflicts;

    /// <summary>Every tag this registry can expand. The single source of truth for "is that a real
    /// component?" — a checker or a tool asks here rather than keeping its own list, which would go
    /// stale the moment a control is added.</summary>
    public IReadOnlyCollection<string> Tags => _components.Keys;

    /// <summary>The behaviours the registered components and libraries carry, which a document
    /// attaches once each (<see cref="ICupriBehaviour"/>).</summary>
    internal IEnumerable<ICupriBehaviour> Behaviours
    {
        get
        {
            foreach (var lib in _libraries)
                if (lib is ICupriBehaviour b) yield return b;
            foreach (var r in OrderedRegistrations())
                if (r.Component is ICupriBehaviour b) yield return b;
        }
    }

    /// <summary>
    /// Every registered component's default CSS, preceded by one rule putting the whole catalogue on
    /// <c>box-sizing: border-box</c>, and in LAYER order: built-ins, then each library (its shared
    /// <see cref="ICupriComponentLibrary.Css"/> first, then its components), then the app's own.
    /// A document parses this BEFORE the app's stylesheet, so at equal specificity the app always
    /// wins, a library always beats the built-ins it restyles, and a replacement beats what it replaced.
    /// That ordering is the override contract and is pinned by tests (ComponentPackagingTests); do not
    /// "tidy" it.
    ///
    /// <para><b>The catalogue only.</b> A control chose its own padding and often its own border, so
    /// a caller who writes <c>width: 120px</c> on one means the box they can see — under content-box
    /// that silently excludes the control's padding and it comes out wider than asked for, which is
    /// indistinguishable from a layout bug. Author elements keep the CSS default, because that is
    /// what someone who knows CSS expects of their own markup and flipping it under them would be a
    /// surprise in the other direction.</para>
    ///
    /// <para>Generated from the registry and emitted FIRST, so it cannot drift as controls are added
    /// and any control can override it. A hand-kept list of forty tags is wrong within a month.</para>
    ///
    /// <para>The controls' own declared sizes were re-derived to mean the same thing under it — see
    /// the migration note in RELEASE-NOTES. Flipping this without that work changes what each
    /// control's own CSS says about itself, which is how it was first tried and reverted.</para>
    /// </summary>
    public string AggregatedCss
    {
        get
        {
            if (_components.Count == 0 && _libraries.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            if (_components.Count > 0) sb.Append(string.Join(", ", _order)).Append(" { box-sizing: border-box; }\n");
            var emittedLibraries = new HashSet<ICupriComponentLibrary>();
            foreach (var r in OrderedRegistrations())
            {
                // A library's shared CSS (tokens, shared classes) goes immediately before its first
                // component, so its components can build on it and the next library comes after both.
                if (r.Layer == ComponentLayer.Library && r.Library is { } lib && emittedLibraries.Add(lib))
                    sb.Append(lib.Css).Append('\n');
                sb.Append(r.Component.DefaultCss).Append('\n');
            }
            foreach (var lib in _libraries)               // a library of shared CSS and no components
                if (emittedLibraries.Add(lib)) sb.Append(lib.Css).Append('\n');
            return sb.ToString();
        }
    }

    private IEnumerable<ComponentRegistration> OrderedRegistrations()
    {
        for (var layer = ComponentLayer.BuiltIn; layer <= ComponentLayer.App; layer++)
            foreach (var tag in _order)
                if (_components[tag] is var r && r.Layer == layer) yield return r;
    }

    private void Set(ICupriComponent component, ComponentLayer layer, ICupriComponentLibrary? library)
    {
        // Re-append on replacement: the new registration's place in the CSS is its own layer's end,
        // not the slot of the component it displaced.
        var i = _order.FindIndex(t => string.Equals(t, component.Tag, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _order.RemoveAt(i);
        _order.Add(component.Tag);
        _components[component.Tag] = new ComponentRegistration(component.Tag, component, layer, library);
        Version++;
    }

    private static string Describe(ComponentRegistration r) =>
        r.Layer switch
        {
            ComponentLayer.BuiltIn => $"the built-in {r.Component.GetType().Name}",
            ComponentLayer.Library => $"{r.Component.GetType().Name} from library {r.Library?.Name}",
            _ => r.Component.GetType().Name,
        };

    public void Expand(IDocument document)
    {
        // Expansion runs on EVERY rebuild (each keystroke), so avoid a full-document QuerySelectorAll per
        // registered component (~60 tag queries, most for components not on the page): ONE walk collects
        // the registered custom elements present, grouped by tag, and only those expand. A pass that
        // expanded anything re-walks, so components emitted by other components still expand (next pass).
        const int maxPasses = 8; // supports components that emit other components
        for (var pass = 0; pass < maxPasses; pass++)
        {
            Dictionary<string, List<IElement>>? byTag = null;
            if (document.DocumentElement is { } docRoot) Collect(docRoot, ref byTag);
            if (byTag is null) break;

            var any = false;
            foreach (var tag in _order)
            {
                if (!byTag.TryGetValue(tag, out var els)) continue;
                var component = _components[tag].Component;
                foreach (var el in els)
                {
                    if (el.HasAttribute("data-cupri-expanded")) continue;
                    if (!IsConnected(el, document)) continue; // orphaned by an earlier expansion this pass
                    component.Expand(el);
                    el.SetAttribute("data-cupri-expanded", "");
                    any = true;
                }
            }
            if (!any) break;
        }
    }

    // Collect registered, not-yet-expanded custom elements in one pre-order walk. Subtrees hidden by an
    // INLINE display:none (e.g. the showcase's switched-off sections, `display:{{Sec}}` resolved at bind
    // time) are skipped entirely: they can't render, the style pass prunes them anyway, and expanding
    // them was most of the per-keystroke expansion cost. The rebuild that reveals one expands it then.
    private void Collect(IElement el, ref Dictionary<string, List<IElement>>? byTag)
    {
        if (HasInlineDisplayNone(el)) return;
        if (_components.ContainsKey(el.LocalName) && !el.HasAttribute("data-cupri-expanded"))
        {
            byTag ??= new Dictionary<string, List<IElement>>(StringComparer.OrdinalIgnoreCase);
            if (!byTag.TryGetValue(el.LocalName, out var list)) byTag[el.LocalName] = list = new List<IElement>();
            list.Add(el);
        }
        foreach (var child in el.Children) Collect(child, ref byTag);
    }

    // True when the element's inline style contains `display: none`. String-level (the DOM is restyled
    // from scratch later anyway); strict about token boundaries so e.g. a `--display-mode` custom
    // property can never false-positive (which would wrongly hide a visible component).
    private static bool HasInlineDisplayNone(IElement el)
    {
        var s = el.GetAttribute("style");
        if (s is null) return false;
        for (var i = s.IndexOf("display", StringComparison.OrdinalIgnoreCase); i >= 0;
             i = s.IndexOf("display", i + 7, StringComparison.OrdinalIgnoreCase))
        {
            if (i > 0 && s[i - 1] is not (';' or ' ' or '\t')) continue;         // mid-identifier: not the property
            var j = i + 7;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            if (j >= s.Length || s[j] != ':') continue;
            j++;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            return j + 4 <= s.Length && s.AsSpan(j, 4).Equals("none", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    // An earlier expansion in this pass may have rewritten an ancestor's InnerHtml, detaching a collected
    // element — expanding a detached node would be wasted work (and could resurrect stale content).
    private static bool IsConnected(IElement el, IDocument document)
    {
        for (INode? n = el; n is not null; n = n.Parent)
            if (ReferenceEquals(n, document)) return true;
        return false;
    }

    // The built-ins enter through here rather than Add, so they are marked as the BuiltIn layer and
    // their CSS is emitted first whatever is added later.
    private ComponentRegistry BuiltIn(ICupriComponent component)
    {
        Set(component, ComponentLayer.BuiltIn, null);
        return this;
    }

    /// <summary>The first-party control library (DESIGN.md §10.5).</summary>
    public static ComponentRegistry Default() => new ComponentRegistry()
        // Inputs
        .BuiltIn(new SliderComponent())
        .BuiltIn(new SwitchComponent())
        .BuiltIn(new ProgressComponent())
        .BuiltIn(new ButtonComponent())
        .BuiltIn(new IconButtonComponent())
        .BuiltIn(new CheckboxComponent())
        .BuiltIn(new RadioComponent())
        .BuiltIn(new TextFieldComponent())
        .BuiltIn(new NumberFieldComponent())
        .BuiltIn(new TextAreaComponent())
        .BuiltIn(new SelectComponent())
        .BuiltIn(new ComboboxComponent())
        .BuiltIn(new DatePickerComponent())
        .BuiltIn(new TimePickerComponent())
        .BuiltIn(new ColorComponent())
        .BuiltIn(new SearchFieldComponent())
        .BuiltIn(new PasswordFieldComponent())
        .BuiltIn(new RatingComponent())
        .BuiltIn(new SegmentedComponent())
        .BuiltIn(new SegmentComponent())
        .BuiltIn(new PaginationComponent())
        .BuiltIn(new RangeComponent())
        .BuiltIn(new TagInputComponent())
        .BuiltIn(new FormComponent())
        // Content
        .BuiltIn(new ImageComponent())
        .BuiltIn(new VideoComponent())
        .BuiltIn(new VideoTracksComponent())
        .BuiltIn(new VideoChaptersComponent())
        .BuiltIn(new IconComponent())
        .BuiltIn(new BadgeComponent())
        .BuiltIn(new ChipComponent())
        .BuiltIn(new AvatarComponent())
        .BuiltIn(new CardComponent())
        .BuiltIn(new DividerComponent())
        .BuiltIn(new StatComponent())
        .BuiltIn(new MarkdownComponent())
        // Charts
        .BuiltIn(new BarChartComponent())
        .BuiltIn(new BarComponent())
        .BuiltIn(new SeriesComponent())
        .BuiltIn(new LineChartComponent())
        .BuiltIn(new LineSeriesComponent())
        .BuiltIn(new PointComponent())
        .BuiltIn(new SparklineComponent())
        .BuiltIn(new RollingChartComponent())
        .BuiltIn(new HeatmapComponent())
        .BuiltIn(new HeatCellComponent())
        // Navigation & disclosure
        .BuiltIn(new CarouselComponent())
        .BuiltIn(new SlideComponent())
        .BuiltIn(new BreadcrumbComponent())
        .BuiltIn(new CrumbComponent())
        .BuiltIn(new ToolbarComponent())
        .BuiltIn(new ToolbarGroupComponent())
        .BuiltIn(new ToolbarSeparatorComponent())
        .BuiltIn(new TabsComponent())
        .BuiltIn(new AccordionComponent())
        .BuiltIn(new AccordionItemComponent())
        .BuiltIn(new TreeComponent())
        .BuiltIn(new TreeItemComponent())
        .BuiltIn(new ReorderComponent())
        .BuiltIn(new ReorderItemComponent())
        .BuiltIn(new BoardComponent())
        .BuiltIn(new VirtualListComponent())
        .BuiltIn(new SplitComponent())
        .BuiltIn(new SplitPanelComponent())
        // Data
        .BuiltIn(new TableComponent())
        .BuiltIn(new TableRowComponent())
        .BuiltIn(new TableCellComponent())
        // Feedback
        .BuiltIn(new AlertComponent())
        .BuiltIn(new SpinnerComponent())
        .BuiltIn(new SkeletonComponent())
        // Overlays
        .BuiltIn(new DialogComponent())
        .BuiltIn(new ToastComponent())
        .BuiltIn(new ToasterComponent())
        .BuiltIn(new MenuComponent())
        .BuiltIn(new MenuItemComponent())
        .BuiltIn(new ContextMenuComponent())
        .BuiltIn(new CommandPaletteComponent())
        .BuiltIn(new TooltipComponent())
        .BuiltIn(new PopoverComponent())
        .BuiltIn(new DrawerComponent())
        .BuiltIn(new ShelfComponent());
}
