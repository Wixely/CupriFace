using CupriFace.Components;

namespace Leaf.Controls;

/// <summary>
/// The library itself: its name, its tag prefix, its shared CSS and its components. An app adds it
/// with <c>registry.AddLeafControls()</c> (or <c>registry.Add(new LeafControls())</c>).
///
/// <para>Everything is constructed here, explicitly. Nothing scans the assembly for components, so
/// trimming and NativeAOT keep exactly what this list names and nothing else is needed.</para>
/// </summary>
public sealed class LeafControls : ICupriComponentLibrary
{
    public string Name => "Leaf.Controls";

    public string Prefix => "leaf-";

    public IEnumerable<ICupriComponent> Components =>
    [
        new CounterComponent(),
        new CardComponent(),
        new StatusComponent(),
    ];

    public string Css => LeafAssets.Text(Assets.Leaf.Css);
}

/// <summary>The one line an app writes.</summary>
public static class LeafControlsRegistration
{
    /// <summary>Add <c>&lt;leaf-counter&gt;</c>, <c>&lt;leaf-card&gt;</c> and <c>&lt;leaf-status&gt;</c>.
    /// Throws <see cref="ComponentConflictException"/> if any of their tags is already registered.</summary>
    public static ComponentRegistry AddLeafControls(this ComponentRegistry registry) =>
        registry.Add(new LeafControls());
}

/// <summary>Reading the library's own embedded assets, once each. The markup and CSS are authored
/// as real files under Assets/ (so an editor treats them as HTML and CSS) and embedded into THIS
/// assembly by the resources pipeline, so they travel inside the package with the code.</summary>
internal static class LeafAssets
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string Text(CupriFace.Resources.CupriSource source)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(source.Origin, out var text))
                Cache[source.Origin] = text = source.ReadText();
            return text;
        }
    }

    /// <summary>Fill <c>{name}</c> slots in a markup template, HTML-escaping each value — an
    /// attribute value can be anything a model held, and it lands inside markup.</summary>
    public static string Fill(string template, params (string Name, string Value)[] slots)
    {
        foreach (var (name, value) in slots) template = template.Replace("{" + name + "}", Escape(value));
        return template;
    }

    /// <summary>The same, for a slot whose value is already markup (a card's children).</summary>
    public static string FillRaw(string template, string name, string markup) =>
        template.Replace("{" + name + "}", markup);

    public static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
}
