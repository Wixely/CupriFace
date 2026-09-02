using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The two desktop windows must map the SAME cursors.
///
/// A cursor is the last hop of an affordance, and the only one nothing upstream can test: the engine
/// resolving <c>Grab</c> over a drag handle passed happily while both windows folded it in with
/// <c>Pointer</c> and handed the OS a hyperlink's pointing hand. Auditing the tables against the enum
/// then found the same shape twice more — the GL window had never mapped <c>Wait</c> or
/// <c>Progress</c>, which the SDL window has mapped since it was written, so a busy app showed an
/// hourglass on one desktop path and an arrow on the other.
///
/// Source analysis, because a cursor mapping needs a real window to exercise. That is the same reason
/// the web hosts are compared this way, and the same underlying risk: two implementations of one
/// contract drift silently when nothing compares them.
/// </summary>
public class CursorTableTests(ITestOutputHelper output)
{
    /// <summary>The one <see cref="CupriFace.Style.CursorType"/> neither GLFW nor SDL has a standard
    /// cursor for. It falls through to the default arrow on purpose; listing it here is what keeps
    /// that a decision rather than an omission nobody noticed.</summary>
    private static readonly string[] Unmappable = ["Help"];

    /// <summary>Not affordances — the absence of one, or the platform's own default.</summary>
    private static readonly string[] NotCursors = ["Auto", "Default", "None"];

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriFace.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static List<string> CursorTypes()
    {
        var src = File.ReadAllText(Path.Combine(Root(), "src", "CupriFace", "Style", "Values.cs"));
        var body = Regex.Match(src, @"public enum CursorType\s*\{(.*?)\}", RegexOptions.Singleline).Groups[1].Value;
        return [.. body.Replace("\n", " ").Split(',').Select(v => v.Trim()).Where(v => v.Length > 0)];
    }

    /// <summary>The body of a window's SetCursor switch, up to its fall-through arm.</summary>
    private static string SwitchBody(string file)
    {
        var src = File.ReadAllText(Path.Combine(Root(), "src", "CupriFace.Shell", file));
        var from = src.IndexOf("public void SetCursor", StringComparison.Ordinal);
        Assert.True(from >= 0, $"{file} has no SetCursor");
        var body = src[from..];
        return body[..body.IndexOf("_ =>", StringComparison.Ordinal)];
    }

    [Theory]
    [InlineData("SkiaWindow.cs")]
    [InlineData("SdlSoftwareWindow.cs")]
    public void Every_cursor_the_platform_can_show_is_mapped(string file)
    {
        var body = SwitchBody(file);
        var expected = CursorTypes().Except(NotCursors).Except(Unmappable).ToList();
        var missing = expected.Where(v => !body.Contains($"CursorType.{v}", StringComparison.Ordinal)).ToList();

        output.WriteLine($"{file}: {expected.Count - missing.Count}/{expected.Count} mapped");
        Assert.True(missing.Count == 0,
            $"{file} falls through to the default arrow for: {string.Join(", ", missing)}. " +
            "Either map it, or add it to Unmappable with the reason — silence is how Grab shipped as a link.");
    }

    /// <summary>…and they must agree with each other. One window gaining a cursor the other lacks is
    /// the drift itself, not merely a gap.</summary>
    [Fact]
    public void The_two_windows_map_the_same_set()
    {
        var types = CursorTypes().Except(NotCursors).ToList();
        var gl = types.Where(v => SwitchBody("SkiaWindow.cs").Contains($"CursorType.{v}", StringComparison.Ordinal)).ToList();
        var sdl = types.Where(v => SwitchBody("SdlSoftwareWindow.cs").Contains($"CursorType.{v}", StringComparison.Ordinal)).ToList();

        output.WriteLine($"GL {gl.Count}, SDL {sdl.Count}");
        Assert.Equal(sdl.Except(gl).OrderBy(x => x), gl.Except(sdl).OrderBy(x => x));   // both empty
        Assert.Equal(gl.OrderBy(x => x), sdl.OrderBy(x => x));
    }

    /// <summary>A drag handle must not arrive as a hyperlink. The pointing hand belongs to Pointer
    /// alone — this is the bug the audit started from.</summary>
    [Theory]
    [InlineData("SkiaWindow.cs", "StandardCursor.Hand")]
    [InlineData("SdlSoftwareWindow.cs", "SystemCursor.SystemCursorHand")]
    public void The_pointing_hand_belongs_to_pointer_alone(string file, string hand)
    {
        var line = SwitchBody(file).Split('\n').FirstOrDefault(l => l.Contains(hand) && l.Contains("=>"));
        Assert.NotNull(line);
        output.WriteLine($"{file}: {line!.Trim()}");
        Assert.DoesNotContain("Grab", line);
        Assert.Contains("Pointer", line);
    }
}
