using System.Globalization;
using CupriFace.Components;
using CupriFace.Diagnostics;
using CupriFace.Svg;
using SkiaSharp;

namespace CupriFace.Tool;

/// <summary>
/// <c>cupri</c> — look at a CupriFace UI the way the ENGINE draws it, from the command line.
///
/// <para>A browser is the wrong preview for CupriFace markup, and an agent reaches for one because
/// it is the easy way to see HTML. This is the easy way that is also right: one command renders the
/// document through the engine at a desktop and a phone size, writes one image to read, and prints
/// what <see cref="CupriDoctor"/> finds — in the compiler's format, so an editor lists it.</para>
/// </summary>
public static class Cli
{
    public const string Usage = """
cupri — preview and check a CupriFace UI as the engine draws it (not as a browser would).

  cupri preview <page.html> [page.css] [options]   render to a PNG, and run the doctor
  cupri preview --app <MyApp.dll> [--type MyApp]    the same, for a BUILT app: its own markup,
                                                    model, components and Configure
  cupri check   <page.html> [page.css] [options]   the doctor only (for CI: exit 1 on errors)
  cupri check   --app <MyApp.dll> [--type MyApp]
  cupri skill   [--dir <project>] [--print]        install the CupriFace UI skill for agents

The stylesheet defaults to the .css beside the .html with the same name. With --app, build the app
first: the preview is of what it runs.

Options:
  --model <file.json>    data to bind: {{Name}}, data-repeat="Items", two-way controls
  --sizes <list>         desktop, tablet, phone, small-phone, app (the app's own size) or WxH;
                         default desktop,phone
  -o, --out <file.png>   where the image goes (default: <page>.preview.png)
  --outlines             outline every element's box in the image (finds overlaps)
  --tree [depth]         print the laid-out tree with coordinates, at the first size
  --sweep                also check every bool flag in the model, on and off, and Tab focus
  --fonts <dir>          register the fonts in a folder (for output that matches across machines)
  --fail-on <level>      error (default), warning, info or never — what makes the exit code 1

Read the PNG. Then fix what the doctor printed. A browser will show you something else.

Component libraries: a PAGE preview knows the built-in cupri-* controls only — a library's tag
(<acme-card>) is a CF0020 note and draws as a plain box. Preview the built app instead
(--app MyApp.dll): it uses the app's own registry, libraries included.
""";

    /// <summary>Run a command. Returns the process exit code: 0 clean, 1 findings at or above
    /// --fail-on, 2 usage error.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.Write(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            return args[0] switch
            {
                "preview" => Preview(Options.Parse(args[1..]), output, render: true),
                "check" => Preview(Options.Parse(args[1..]), output, render: false),
                "skill" => Skill(args[1..], output),
                _ => throw new UsageException($"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException ex)
        {
            error.WriteLine($"cupri: {ex.Message}");
            error.WriteLine("Run 'cupri --help' for usage.");
            return 2;
        }
    }

    private static int Preview(Options o, TextWriter output, bool render)
    {
        var subject = o.App is { } dll
            ? Subject.FromApp(dll, o.Type, o.Fonts)
            : Subject.FromFiles(o.Html!, o.Css, o.Model, o.Fonts);
        var sizes = o.Sizes;
        if (o.AppSizeRequested)
        {
            // "app" in --sizes: the size the app asks its window to open at.
            var own = subject.WindowSize ?? throw new UsageException("--sizes app needs --app: a page has no size of its own.");
            sizes = sizes.Select(s => s == AppSize ? own : s).ToList();
        }
        var html = subject.Html;
        var css = subject.Css;
        var model = subject.Model;

        output.WriteLine($"[cupri] {subject.Description} at {string.Join(", ", sizes.Select(s => $"{s.Width}x{s.Height}"))}");

        var previousDir = Environment.CurrentDirectory;
        Environment.CurrentDirectory = subject.WorkDir;
        try
        {
            if (render)
            {
                using var doc = subject.Create();
                doc.Refresh();
                using (doc.RenderToImage(sizes[0].Width, sizes[0].Height)) { }   // warm layout + images
                using var sheet = DeviceSheet.Render(doc, sizes, outlines: o.Outlines);
                using var data = sheet.Encode(SKEncodedImageFormat.Png, 95);
                var outPath = o.Out ?? Path.Combine(previousDir, subject.Name + ".preview.png");
                using (var f = File.Create(outPath)) data.SaveTo(f);
                output.WriteLine($"[cupri] image: {outPath}  ({sheet.Width}x{sheet.Height}) — open it and look");

                if (o.TreeDepth is { } depth)
                {
                    using (doc.RenderToImage(sizes[0].Width, sizes[0].Height)) { }
                    output.WriteLine($"[cupri] tree at {sizes[0].Width}x{sizes[0].Height}:");
                    output.WriteLine(doc.DumpTree(depth));
                }
            }

            var report = o.Sweep
                ? CupriDoctor.Sweep(html, css, subject.Components, model, subject.Configure, sizes)
                : CupriDoctor.Check(html, css, sizes, subject.Components, model, subject.Configure);
            foreach (var f in report.Findings) output.WriteLine(Format(f, subject));
            var states = o.Sweep ? $", {report.Checks} states checked" : "";
            output.WriteLine(report.IsClean
                ? $"[cupri] doctor: no problems found{states}"
                : $"[cupri] doctor: {Summary(report)}{states}");
            if (model is null && html.Contains("{{", StringComparison.Ordinal))
                output.WriteLine(o.App is null
                    ? "[cupri] note: the markup has {{bindings}} and no --model, so binding checks were skipped and they render empty."
                    : "[cupri] note: the markup has {{bindings}} and the app's Model is null, so binding checks were skipped.");

            return o.FailOn is { } level && report.Findings.Any(f => f.Severity >= level) ? 1 : 0;
        }
        finally { Environment.CurrentDirectory = previousDir; }
    }

    /// <summary>The compiler's diagnostic format — <c>path(line): warning CF0060: …</c> — which VS
    /// Code's <c>$msCompile</c> matcher and most CI annotators read.</summary>
    internal static string Format(Finding f, Subject s)
    {
        // CSS findings carry a stylesheet line when there is a stylesheet; a source with no file on
        // disk (an app's markup written in C#) is named after the property it came from.
        var inCss = f.Code.StartsWith("CF005", StringComparison.Ordinal) && s.Css.Length > 0;
        var file = inCss ? s.CssPath ?? $"{s.Name}.Css" : s.HtmlPath ?? $"{s.Name}.Html";
        var sev = f.Severity switch { Severity.Error => "error", Severity.Warning => "warning", _ => "info" };
        var where = (f.Viewports.Length > 0 ? $" [at {f.Viewports} only]" : "") + (f.State.Length > 0 ? $" [{f.State}]" : "");
        var fix = f.Fix.Length > 0 ? "  -> " + f.Fix : "";
        return $"{file}({Math.Max(1, f.Line)}): {sev} {f.Code}: {f.Message}{where}{fix}";
    }

    private static string Summary(DoctorReport r)
    {
        var parts = new List<string>();
        void Add(Severity s, string one) { var n = r.Count(s); if (n > 0) parts.Add($"{n} {one}{(n == 1 ? "" : "s")}"); }
        Add(Severity.Error, "error"); Add(Severity.Warning, "warning"); Add(Severity.Info, "note");
        return string.Join(", ", parts);
    }

    // ---- skill -----------------------------------------------------------------------------

    private const string SkillName = "cupriface-ui";

    /// <summary>The skill's text, embedded from the repository's own
    /// <c>.claude/skills/cupriface-ui/SKILL.md</c> — one source for both.</summary>
    public static string SkillText()
    {
        using var s = typeof(Cli).Assembly.GetManifestResourceStream("SKILL.md")
            ?? throw new InvalidOperationException("The skill is not embedded in this build.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static int Skill(string[] args, TextWriter output)
    {
        string? dir = null;
        var print = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dir": dir = Next(args, ref i, "--dir"); break;
                case "--print": print = true; break;
                default: throw new UsageException($"Unknown option '{args[i]}' for skill.");
            }
        }
        if (print) { output.Write(SkillText()); return 0; }

        var root = Path.GetFullPath(dir ?? Environment.CurrentDirectory);
        var target = Path.Combine(root, ".claude", "skills", SkillName, "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var existed = File.Exists(target);
        File.WriteAllText(target, SkillText());
        output.WriteLine($"[cupri] {(existed ? "updated" : "installed")} the {SkillName} skill: {target}");
        output.WriteLine("[cupri] agents in this project now load it when they build or change CupriFace UI.");
        return 0;
    }

    // ---- options ---------------------------------------------------------------------------

    internal sealed class UsageException(string message) : Exception(message);

    internal sealed class Options
    {
        public string? Html { get; init; }
        public string? App { get; init; }
        public string? Type { get; init; }
        public bool AppSizeRequested { get; init; }
        public string? Css { get; init; }
        public string? Model { get; init; }
        public required IReadOnlyList<(int Width, int Height)> Sizes { get; init; }
        public string? Out { get; init; }
        public bool Outlines { get; init; }
        public int? TreeDepth { get; init; }
        public bool Sweep { get; init; }
        public string? Fonts { get; init; }
        public Severity? FailOn { get; init; } = Severity.Error;

        public static Options Parse(string[] args)
        {
            string? html = null, css = null, model = null, outPath = null, fonts = null, app = null, type = null;
            IReadOnlyList<(int, int)> sizes = CupriDoctor.DesktopAndPhone;
            bool outlines = false, sweep = false;
            int? tree = null;
            Severity? failOn = Severity.Error;
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--model": model = Next(args, ref i, a); break;
                    case "--app": app = Next(args, ref i, a); break;
                    case "--type": type = Next(args, ref i, a); break;
                    case "--sizes": sizes = ParseSizes(Next(args, ref i, a)); break;
                    case "-o" or "--out": outPath = Next(args, ref i, a); break;
                    case "--outlines": outlines = true; break;
                    case "--sweep": sweep = true; break;
                    case "--fonts": fonts = Next(args, ref i, a); break;
                    case "--tree":
                        tree = 3;
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var depth)) { tree = depth; i++; }
                        break;
                    case "--fail-on":
                        failOn = Next(args, ref i, a).ToLowerInvariant() switch
                        {
                            "error" => Severity.Error,
                            "warning" => Severity.Warning,
                            "info" => Severity.Info,
                            "never" => null,
                            var v => throw new UsageException($"--fail-on takes error, warning, info or never, not '{v}'."),
                        };
                        break;
                    default:
                        if (a.StartsWith('-')) throw new UsageException($"Unknown option '{a}'.");
                        if (html is null) html = a;
                        else if (css is null) css = a;
                        else throw new UsageException($"Unexpected argument '{a}'.");
                        break;
                }
            }
            if (app is not null)
            {
                if (html is not null) throw new UsageException("Give a page or --app, not both.");
                if (model is not null) throw new UsageException("--app previews with the app's own model; --model is for pages.");
                if (!File.Exists(app)) throw new UsageException($"No such file: {app} (build the app first)");
            }
            else
            {
                if (type is not null) throw new UsageException("--type picks an app, and needs --app.");
                if (html is null) throw new UsageException("Give the page: cupri preview page.html [page.css], or --app MyApp.dll.");
                if (!File.Exists(html)) throw new UsageException($"No such file: {html}");
            }
            if (css is not null && !File.Exists(css)) throw new UsageException($"No such file: {css}");
            if (model is not null && !File.Exists(model)) throw new UsageException($"No such file: {model}");
            if (fonts is not null && !Directory.Exists(fonts)) throw new UsageException($"No such folder: {fonts}");
            // The stylesheet beside the page, by name — App.html → App.css — unless one was given.
            if (html is not null)
                css ??= Path.ChangeExtension(html, ".css") is var sibling && File.Exists(sibling) ? sibling : null;
            // Absolute from here on: the preview runs with the page's folder as the working directory.
            static string? Full(string? p) => p is null ? null : Path.GetFullPath(p);
            return new Options
            {
                Html = Full(html), App = Full(app), Type = type, Css = Full(css), Model = Full(model), Sizes = sizes,
                AppSizeRequested = sizes.Contains(AppSize), Outlines = outlines,
                Sweep = sweep, TreeDepth = tree, Fonts = Full(fonts), FailOn = failOn,
                // A page's image goes beside it; an app's, where the command was run (Preview).
                Out = Full(outPath ?? (html is null ? null : Path.ChangeExtension(html, ".preview.png"))),
            };
        }

        private static IReadOnlyList<(int, int)> ParseSizes(string list)
        {
            var sizes = new List<(int, int)>();
            foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "desktop": sizes.Add((1024, 768)); break;
                    case "tablet": sizes.Add((768, 1024)); break;
                    case "phone": sizes.Add((412, 915)); break;
                    case "small-phone": sizes.Add((360, 640)); break;
                    case "app": sizes.Add(AppSize); break;
                    default:
                        var parts = raw.Split('x', 'X');
                        if (parts.Length != 2
                            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
                            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                            || w is < 1 or > 8192 || h is < 1 or > 8192)
                            throw new UsageException($"'{raw}' is not a size: use desktop, tablet, phone, small-phone or WxH.");
                        sizes.Add((w, h));
                        break;
                }
            }
            if (sizes.Count == 0) throw new UsageException("--sizes needs at least one size.");
            return sizes;
        }
    }

    /// <summary>The placeholder "app" stands for in --sizes, replaced by the app's own size.</summary>
    internal static readonly (int Width, int Height) AppSize = (-1, -1);

    private static string Next(string[] args, ref int i, string option) =>
        ++i < args.Length ? args[i] : throw new UsageException($"{option} needs a value.");
}
