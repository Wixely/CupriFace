using AngleSharp.Dom;

namespace CupriFace.DevTools;

/// <summary>
/// Which line of the markup an element of the LIVE document was written on.
///
/// <para>The live DOM has been through binding and component expansion, so it carries no source
/// positions; this searches the template text instead, the way <see cref="Diagnostics.CupriDoctor"/>
/// finds a line for a finding. An id is exact. A tag carrying the element's first class comes next.
/// Last is the Nth occurrence of the tag, counted among the template's elements with that tag —
/// right for written markup, and why a <c>data-repeat</c> row finds the one line all its copies came
/// from. An element the template does not contain (the inside of a component) returns 0, and the
/// caller asks its parent.</para>
/// </summary>
internal static class SourceLocator
{
    public static int LineOf(string html, IElement el)
    {
        var lines = html.Replace("\r\n", "\n").Split('\n');
        var tag = el.LocalName;

        if (el.Id is { Length: > 0 } id)
        {
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains($"id=\"{id}\"", StringComparison.Ordinal) || lines[i].Contains($"id='{id}'", StringComparison.Ordinal))
                    return i + 1;
        }

        // The first class the template could have written. A bound class ("tab {{State}}") resolves
        // to a value the template does not contain, so every class is tried, in order.
        if (el.GetAttribute("class") is { Length: > 0 } cls)
            foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                for (var i = 0; i < lines.Length; i++)
                    if (OpensTag(lines[i], tag) && ContainsClass(lines[i], c))
                        return i + 1;

        var any = 0;
        for (var i = 0; i < lines.Length; i++)
            if (OpensTag(lines[i], tag)) { any = i + 1; break; }
        // Only a tag the template itself writes; an engine-generated wrapper is not in the file.
        return any;
    }

    private static bool OpensTag(string line, string tag)
    {
        var at = 0;
        while ((at = line.IndexOf("<" + tag, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = at + 1 + tag.Length;
            if (end >= line.Length || !(char.IsLetterOrDigit(line[end]) || line[end] == '-')) return true;
            at = end;
        }
        return false;
    }

    private static bool ContainsClass(string line, string cls)
    {
        var at = 0;
        while ((at = line.IndexOf(cls, at, StringComparison.Ordinal)) >= 0)
        {
            var before = at == 0 ? ' ' : line[at - 1];
            var end = at + cls.Length;
            var after = end >= line.Length ? ' ' : line[end];
            if (!IsClassChar(before) && !IsClassChar(after)) return true;
            at = end;
        }
        return false;
    }

    private static bool IsClassChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';
}
