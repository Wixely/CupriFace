using System.Diagnostics;

namespace CupriFace.DevTools;

/// <summary>
/// Opens a file at a line in the developer's editor — VS Code by default, through the
/// <c>vscode://file/…</c> link it registers with the OS, so nothing has to be on the PATH.
///
/// <para>Another editor: set <c>CUPRIFACE_EDITOR_URI</c> to a template with <c>{path}</c> and
/// <c>{line}</c> in it — <c>cursor://file/{path}:{line}</c>, <c>vscode-insiders://file/{path}:{line}</c>,
/// or <c>jetbrains://rider/navigate/reference?path={path}&amp;line={line}</c>.</para>
/// </summary>
internal static class EditorLauncher
{
    public const string TemplateVariable = "CUPRIFACE_EDITOR_URI";
    public const string DefaultTemplate = "vscode://file/{path}:{line}";

    /// <summary>The link that opens <paramref name="path"/> at <paramref name="line"/>.</summary>
    public static string UriFor(string path, int line, string? template = null)
    {
        template ??= Environment.GetEnvironmentVariable(TemplateVariable) is { Length: > 0 } t ? t : DefaultTemplate;
        var p = Path.GetFullPath(path).Replace('\\', '/');
        return template.Replace("{path}", p, StringComparison.Ordinal)
                       .Replace("{line}", Math.Max(1, line).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    public static void Open(string path, int line)
    {
        var uri = UriFor(path, line);
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Console.WriteLine($"[CupriFace] Could not open {uri} ({ex.Message}). Set {TemplateVariable} for your editor.");
        }
    }
}
