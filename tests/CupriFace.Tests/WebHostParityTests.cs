using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The two web hosts must offer the SAME surface to the page, whatever runtime is underneath.
///
/// They are separate implementations of one contract — CupriFace.Web.Mono over Mono's JS interop,
/// CupriFace.Web.NativeAot over the C ABI — and they have drifted before: the LLVM host went
/// without IME caret positioning for as long as it existed, because nothing compared them (#77).
/// A browser gate catches a gap only where a test happens to exercise it; this compares the
/// surfaces directly, so a call added to one host and forgotten in the other fails here.
///
/// Source analysis rather than reflection, deliberately: the NativeAOT host's exports are attribute
/// entry points that only exist after an Emscripten link, so there is no assembly to reflect over
/// on a test runner. The shapes being compared are declarations, which the source states plainly.
///
/// This is the cheap half of #79. The expensive half — sharing the implementation so the two
/// cannot differ at all — is what this test makes safe to attempt.
/// </summary>
public class WebHostParityTests(ITestOutputHelper output)
{
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriFace.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    /// <summary>Both hosts' names for one call, reduced to a comparable key. The two use different
    /// conventions — <c>A11y</c>/<c>js_a11y</c>, <c>TextInputJs</c>/<c>js_text_input</c> — so the
    /// key drops the convention and keeps the meaning.</summary>
    private static string Key(string name)
    {
        name = Regex.Replace(name, "^js_", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, "Js$", "");            // TextInputJs -> TextInput
        name = Regex.Replace(name, "^Js(?=[A-Z])", "");   // JsPresent   -> Present
        return name.Replace("_", "").ToLowerInvariant();
    }

    private static HashSet<string> MonoExports(string s) =>
        [.. Regex.Matches(s, @"\[JSExport\]\s*(?:internal|public)\s+static\s+[^\s(]+(?:\s*\?)?\s+(\w+)\s*\(")
                 .Select(m => Key(m.Groups[1].Value))];

    private static HashSet<string> AotExports(string s) =>
        [.. Regex.Matches(s, @"UnmanagedCallersOnly\(EntryPoint\s*=\s*""(\w+)""")
                 .Select(m => Key(m.Groups[1].Value))];

    private static HashSet<string> MonoImports(string s) =>
        [.. Regex.Matches(s, @"\[JSImport\(""(\w+)""").Select(m => Key(m.Groups[1].Value))];

    private static HashSet<string> AotImports(string s) =>
        [.. Regex.Matches(s, @"EntryPoint\s*=\s*""(js_\w+)""").Select(m => Key(m.Groups[1].Value))];

    private (string Mono, string Aot) Sources()
    {
        var mono = Read("src", "CupriFace.Web.Mono", "Interop.cs")
                 + Read("src", "CupriFace.Web.Mono", "BrowserVideo.cs");
        var aot = Read("src", "CupriFace.Web.NativeAot", "Interop.cs")
                + Read("src", "CupriFace.Web.NativeAot", "BrowserVideo.cs");
        return (mono, aot);
    }

    /// <summary>Names the host lacking each call, because "they differ" is not actionable and
    /// "the LLVM host cannot position an IME" is.</summary>
    private void AssertSame(HashSet<string> mono, HashSet<string> aot, string what)
    {
        var missingFromAot = mono.Except(aot).OrderBy(x => x).ToList();
        var missingFromMono = aot.Except(mono).OrderBy(x => x).ToList();
        output.WriteLine($"{what}: Mono {mono.Count}, NativeAot {aot.Count}, shared {mono.Intersect(aot).Count()}");

        Assert.True(missingFromAot.Count == 0,
            $"{what} present in CupriFace.Web.Mono but MISSING from CupriFace.Web.NativeAot: " +
            $"{string.Join(", ", missingFromAot)}. A page written against one host would break on the other.");
        Assert.True(missingFromMono.Count == 0,
            $"{what} present in CupriFace.Web.NativeAot but MISSING from CupriFace.Web.Mono: " +
            $"{string.Join(", ", missingFromMono)}.");
    }

    [Fact]
    public void Both_hosts_export_the_same_calls_to_the_page()
    {
        var (mono, aot) = Sources();
        var m = MonoExports(mono);
        var a = AotExports(aot);
        // Each host needs a couple of names the other has no use for: the NativeAOT side marshals
        // strings through a shared buffer (TextBuffer/PasteText) because the C ABI has no string.
        a.Remove(Key("TextBuffer"));
        a.Remove(Key("PasteText"));
        AssertSame(m, a, "Exports (page -> engine)");
    }

    [Fact]
    public void Both_hosts_import_the_same_calls_from_the_page()
    {
        var (mono, aot) = Sources();
        AssertSame(MonoImports(mono), AotImports(aot), "Imports (engine -> page)");
    }

    [Fact]
    public void Both_hosts_offer_the_same_public_entry_point()
    {
        // The promise that switching runtimes is a PackageReference change and no app code.
        foreach (var pkg in new[] { "CupriFace.Web.Mono", "CupriFace.Web.NativeAot" })
        {
            var src = Read("src", pkg, "WebHost.cs");
            Assert.Contains("namespace CupriFace.Web;", src);
            Assert.Contains("public static class WebHost", src);
            Assert.Contains("public static void Run(CupriApp app, Action<CupriDocument>? configure = null)", src);
        }
    }

    /// <summary>Every named key must carry its modifiers to the engine, in BOTH hosts.
    ///
    /// Tab was dispatched with a literal 0 where the line directly below it forwards <c>mods</c>, so
    /// <c>OnShortcut(KeyMods.Ctrl, "Tab", …)</c> was a registration that fired on desktop and Android
    /// and never in a browser — a host divergence of exactly the kind this file exists to catch, and
    /// one both hosts got wrong identically, which is why comparing them to each other could not
    /// find it (#96). Shift is the deliberate exception: it rides in the KEY as ShiftTab.</summary>
    [Fact]
    public void Neither_host_drops_the_modifiers_when_dispatching_tab()
    {
        foreach (var (pkg, call) in new[]
                 {
                     ("CupriFace.Web.Mono", "I.EditKeyPress"),
                     ("CupriFace.Web.NativeAot", "M._EditKeyPress"),
                 })
        {
            var js = Read("src", pkg, "wwwroot", "main.js");
            var line = js.Split('\n').FirstOrDefault(l => l.Contains("EK.ShiftTab : EK.Tab"));
            Assert.NotNull(line);
            output.WriteLine($"{pkg}: {line!.Trim()}");

            Assert.Contains(call, line);
            Assert.DoesNotContain("EK.Tab, 0)", line.Replace(" ", ""));
            Assert.Contains("mods", line);
        }
    }
}
