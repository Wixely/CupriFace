using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace CupriFace.Binding;

/// <summary>
/// One-way (model → view) data binding over the DOM template, applied before style
/// resolution. Supports <c>{{ path }}</c> interpolation in text and attributes, and
/// <c>data-repeat="Collection"</c> to instantiate an element per item.
///
/// Reflection-based for now; DESIGN.md §6 calls for a Roslyn source generator to make
/// this AOT-clean — the surface here (path resolution + interpolation) is what the
/// generator would emit specialised code for.
/// </summary>
public static partial class BindingEngine
{
    [GeneratedRegex(@"\{\{\s*([^}]+?)\s*\}\}")]
    private static partial Regex Interp();

    [GeneratedRegex(@"^\s*\{\{\s*([^}]+?)\s*\}\}\s*$")]
    private static partial Regex PureBinding();

    public static void Apply(IDocument document, object model)
    {
        if (document.Body is { } body) Process(body, model);
    }

    private static void Process(IElement element, object? context)
    {
        // Repeat directive expands this element once per collection item.
        var repeatPath = element.GetAttribute("data-repeat");
        if (repeatPath is not null)
        {
            element.RemoveAttribute("data-repeat");
            var parent = element.ParentElement;
            if (parent is not null && Resolve(context, repeatPath) is IEnumerable items and not string)
            {
                foreach (var item in items)
                {
                    var clone = (IElement)element.Clone(deep: true);
                    ProcessSubtree(clone, item);
                    parent.InsertBefore(clone, element);
                }
            }
            element.Remove();
            return;
        }

        BindAttributes(element, context);

        // Snapshot children: interpolation/expansion mutates the live collection.
        foreach (var child in element.ChildNodes.ToArray())
        {
            switch (child)
            {
                case IElement childEl: Process(childEl, context); break;
                case IText text: BindText(text, context); break;
            }
        }
    }

    private static void ProcessSubtree(IElement element, object? context)
    {
        BindAttributes(element, context);
        foreach (var child in element.ChildNodes.ToArray())
        {
            switch (child)
            {
                case IElement childEl: Process(childEl, context); break;
                case IText text: BindText(text, context); break;
            }
        }
    }

    private static void BindAttributes(IElement element, object? context)
    {
        foreach (var attr in element.Attributes.ToArray())
        {
            if (!attr.Value.Contains("{{")) continue;

            // A pure `attr="{{Path}}"` records a two-way link so interaction can write back.
            var pure = PureBinding().Match(attr.Value);
            element.SetAttribute(attr.Name, Interpolate(attr.Value, context));
            if (pure.Success)
                element.SetAttribute($"data-bind-{attr.Name}", pure.Groups[1].Value.Trim());
        }
    }

    private static void BindText(IText text, object? context)
    {
        if (!text.Data.Contains("{{")) return;
        text.Data = Interpolate(text.Data, context);
    }

    private static string Interpolate(string template, object? context) =>
        Interp().Replace(template, m => FormatValue(Resolve(context, m.Groups[1].Value)));

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Resolve a dotted property path against a context object.</summary>
    public static object? Resolve(object? context, string path)
    {
        var current = context;
        foreach (var segRaw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null) return null;
            var seg = segRaw.Trim();
            if (seg is "this" or ".") continue;
            current = Step(current, seg);
        }
        return current;
    }

    private static object? Step(object obj, string name)
    {
        // AOT/trim-clean fast path: source-generated accessor (no reflection).
        if (obj is IBindableAccessor accessor)
            return accessor.GetBindable(name);
        return ReflectionGet(obj, name);
    }

    // Fallback for models that don't opt into [CupriBindable]. Not AOT-safe, so the
    // trim warning is suppressed here with intent: prefer IBindableAccessor for AOT.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Reflection fallback only; [CupriBindable] models use the generated accessor.")]
    private static object? ReflectionGet(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(obj);
    }

    /// <summary>Write <paramref name="value"/> back to a dotted path (two-way binding).</summary>
    public static bool TrySet(object? context, string path, object? value)
    {
        var segs = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = context;
        for (var i = 0; i < segs.Length - 1; i++)
        {
            if (current is null) return false;
            current = Step(current, segs[i].Trim());
        }
        if (current is null) return false;
        var name = segs[^1].Trim();
        // AOT/trim-clean fast path: source-generated setter (no reflection). Without this, the
        // reflection setter below is trimmed away in a published/AOT build and every two-way
        // control (switch, slider, tabs, select, text fields…) silently stops writing back.
        return current is IBindableAccessor accessor ? accessor.SetBindable(name, value)
                                                     : ReflectionSet(current, name, value);
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Two-way write-back at interaction time; not a hot path. Generated setters are future work.")]
    private static bool ReflectionSet(object obj, string name, object? value)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || !prop.CanWrite) return false;
        var t = prop.PropertyType;
        try
        {
            object? converted = value is null ? null
                : t == typeof(int) ? Convert.ToInt32(value)
                : t == typeof(bool) ? Convert.ToBoolean(value)
                : t == typeof(double) ? Convert.ToDouble(value)
                : t == typeof(float) ? Convert.ToSingle(value)
                : t == typeof(string) ? value.ToString()
                : Convert.ChangeType(value, t);
            prop.SetValue(obj, converted);
            return true;
        }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException)
        {
            return false; // invalid partial entry (e.g. "" or "-" into an int) — leave the model unchanged
        }
    }
}
