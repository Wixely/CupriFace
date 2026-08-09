using AngleSharp.Dom;
using CupriFace.Components.Controls;

namespace CupriFace.Components;

/// <summary>
/// Registry mapping custom-element tags to <see cref="ICupriComponent"/>s and expanding
/// them in a DOM (DESIGN.md §10). Expansion runs after data binding, so components see
/// concrete attribute values, and repeats until nested components are fully expanded.
/// </summary>
public sealed class ComponentRegistry
{
    private readonly Dictionary<string, ICupriComponent> _components = new(StringComparer.OrdinalIgnoreCase);

    public ComponentRegistry Register(ICupriComponent component)
    {
        _components[component.Tag] = component;
        return this;
    }

    /// <summary>Concatenated default CSS of all registered components (low priority).</summary>
    public string AggregatedCss => string.Join("\n", _components.Values.Select(c => c.DefaultCss));

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
            foreach (var component in _components.Values)
            {
                if (!byTag.TryGetValue(component.Tag, out var els)) continue;
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

    // Collect registered, not-yet-expanded custom elements in one pre-order walk.
    private void Collect(IElement el, ref Dictionary<string, List<IElement>>? byTag)
    {
        if (_components.ContainsKey(el.LocalName) && !el.HasAttribute("data-cupri-expanded"))
        {
            byTag ??= new Dictionary<string, List<IElement>>(StringComparer.OrdinalIgnoreCase);
            if (!byTag.TryGetValue(el.LocalName, out var list)) byTag[el.LocalName] = list = new List<IElement>();
            list.Add(el);
        }
        foreach (var child in el.Children) Collect(child, ref byTag);
    }

    // An earlier expansion in this pass may have rewritten an ancestor's InnerHtml, detaching a collected
    // element — expanding a detached node would be wasted work (and could resurrect stale content).
    private static bool IsConnected(IElement el, IDocument document)
    {
        for (INode? n = el; n is not null; n = n.Parent)
            if (ReferenceEquals(n, document)) return true;
        return false;
    }

    /// <summary>The first-party control library (DESIGN.md §10.5).</summary>
    public static ComponentRegistry Default() => new ComponentRegistry()
        // Inputs
        .Register(new SliderComponent())
        .Register(new SwitchComponent())
        .Register(new ProgressComponent())
        .Register(new ButtonComponent())
        .Register(new IconButtonComponent())
        .Register(new CheckboxComponent())
        .Register(new RadioComponent())
        .Register(new TextFieldComponent())
        .Register(new NumberFieldComponent())
        .Register(new TextAreaComponent())
        .Register(new SelectComponent())
        .Register(new ComboboxComponent())
        .Register(new DatePickerComponent())
        .Register(new TimePickerComponent())
        .Register(new ColorComponent())
        .Register(new SearchFieldComponent())
        .Register(new PasswordFieldComponent())
        .Register(new RatingComponent())
        .Register(new SegmentedComponent())
        .Register(new SegmentComponent())
        .Register(new PaginationComponent())
        // Content
        .Register(new ImageComponent())
        .Register(new IconComponent())
        .Register(new BadgeComponent())
        .Register(new ChipComponent())
        .Register(new AvatarComponent())
        .Register(new CardComponent())
        .Register(new DividerComponent())
        .Register(new StatComponent())
        .Register(new MarkdownComponent())
        // Charts
        .Register(new BarChartComponent())
        .Register(new BarComponent())
        .Register(new SeriesComponent())
        .Register(new LineChartComponent())
        .Register(new LineSeriesComponent())
        .Register(new PointComponent())
        .Register(new SparklineComponent())
        .Register(new RollingChartComponent())
        .Register(new HeatmapComponent())
        .Register(new HeatCellComponent())
        // Navigation & disclosure
        .Register(new TabsComponent())
        .Register(new AccordionComponent())
        .Register(new AccordionItemComponent())
        .Register(new TreeComponent())
        .Register(new TreeItemComponent())
        .Register(new ReorderComponent())
        .Register(new ReorderItemComponent())
        .Register(new BoardComponent())
        .Register(new VirtualListComponent())
        .Register(new SplitComponent())
        .Register(new SplitPanelComponent())
        // Data
        .Register(new TableComponent())
        .Register(new TableRowComponent())
        .Register(new TableCellComponent())
        // Feedback
        .Register(new AlertComponent())
        .Register(new SpinnerComponent())
        .Register(new SkeletonComponent())
        // Overlays
        .Register(new DialogComponent())
        .Register(new ToastComponent())
        .Register(new ToasterComponent())
        .Register(new MenuComponent())
        .Register(new MenuItemComponent())
        .Register(new ContextMenuComponent())
        .Register(new CommandPaletteComponent())
        .Register(new TooltipComponent())
        .Register(new PopoverComponent())
        .Register(new DrawerComponent())
        .Register(new ShelfComponent());
}
