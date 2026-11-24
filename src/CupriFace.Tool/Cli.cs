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
  cupri check   <page.html> [page.css] [options]   the doctor only (for CI: exit 1 on errors)
  cupri skill   [--dir <project>] [--print]        install the CupriFace UI skill for agents

The stylesheet defaults to the .css beside the .html with the same name.

Options:
  --model <file.json>    data to bind: {{Name}}, data-repeat="Items", two-way controls
  --sizes <list>         desktop, tablet, phone, small-phone, or WxH; default desktop,phone
  -o, --out <file.png>   where the image goes (default: <page>.preview.png)
  --outlines             outline every element's box in the image (finds overlaps)
  --tree [depth]         print the laid-out tree with coordinates, at the first size
  --sweep                also check every bool flag in the model, on and off, and Tab focus
  --fonts <dir>          register the fonts in a folder (for output that matches across machines)
  --fail-on <level>      error (default), warning, info or never — what makes the exit code 1

Read the PNG. Then fix what the doctor printed. A browser will show you something else.

Component libraries: cupri knows the built-in cupri-* controls only. A tag from a library
(<acme-card>) is reported as a CF0020 note and draws as a plain box, because loading a library
here would mean finding its components by reflection. Check such a page where the app's registry
exists: CupriDoctor.Check(html, css, CupriDoctor.DesktopAndPhone, registry, model).
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
        var html = File.ReadAllText(o.Html);
        var css = o.Css is null ? "" : File.ReadAllText(o.Css);
        var model = o.Model is null ? null : JsonModel.Parse(File.ReadAllText(o.Model));
        var components = ComponentRegistry.Default();

        void Configure(CupriDocument d)
        {
            d.UseSvg();
            if (o.Fonts is { } fonts) d.LoadFonts(fonts);
        }

        output.WriteLine($"[cupri] {Path.GetFileName(o.Html)}"
            + (o.Css is null ? " (no stylesheet)" : $" + {Path.GetFileName(o.Css)}")
            + (o.Model is null ? "" : $" bound to {Path.GetFileName(o.Model)}")
            + $" at {string.Join(", ", o.Sizes.Select(s => $"{s.Width}x{s.Height}"))}");

        // Relative paths in the markup (images, fonts) resolve against the page's folder — or, for a
        // page in an app's Assets/ folder, against the project, because an app names its embedded
        // assets "Assets/logo.png" from there and that is what its markup says.
        var previousDir = Environment.CurrentDirectory;
        var pageDir = Path.GetDirectoryName(Path.GetFullPath(o.Html))!;
        Environment.CurrentDirectory = Path.GetFileName(pageDir).Equals("Assets", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(pageDir)! : pageDir;
        try
        {
            if (render)
            {
                using var doc = CupriDocument.Load(html, css).UseComponents(components);
                Configure(doc);
                if (model is not null) doc.Bind(model);
                doc.Refresh();
                using (doc.RenderToImage(o.Sizes[0].Width, o.Sizes[0].Height)) { }   // warm layout + images
                using var sheet = DeviceSheet.Render(doc, o.Sizes, outlines: o.Outlines);
                using var data = sheet.Encode(SKEncodedImageFormat.Png, 95);
                using (var f = File.Create(o.Out)) data.SaveTo(f);
                output.WriteLine($"[cupri] image: {o.Out}  ({sheet.Width}x{sheet.Height}) — open it and look");

                if (o.TreeDepth is { } depth)
                {
                    using (doc.RenderToImage(o.Sizes[0].Width, o.Sizes[0].Height)) { }
                    output.WriteLine($"[cupri] tree at {o.Sizes[0].Width}x{o.Sizes[0].Height}:");
                    output.WriteLine(doc.DumpTree(depth));
                }
            }

            var report = o.Sweep
                ? CupriDoctor.Sweep(html, css, components, model, Configure, o.Sizes)
                : CupriDoctor.Check(html, css, o.Sizes, components, model, Configure);
            foreach (var f in report.Findings) output.WriteLine(Format(f, o));
            var states = o.Sweep ? $", {report.Checks} states checked" : "";
            output.WriteLine(report.IsClean
                ? $"[cupri] doctor: no problems found{states}"
                : $"[cupri] doctor: {Summary(report)}{states}");
            if (model is null && html.Contains("{{", StringComparison.Ordinal))
                output.WriteLine("[cupri] note: the markup has {{bindings}} and no --model, so binding checks were skipped and they render empty.");

            return o.FailOn is { } level && report.Findings.Any(f => f.Severity >= level) ? 1 : 0;
        }
        finally { Environment.CurrentDirectory = previousDir; }
    }

    /// <summary>The compiler's diagnostic format — <c>path(line): warning CF0060: …</c> — which VS
    /// Code's <c>$msCompile</c> matcher and most CI annotators read.</summary>
    internal static string Format(Finding f, Options o)
    {
        var inCss = f.Code.StartsWith("CF005", StringComparison.Ordinal) && o.Css is not null;
        var file = inCss ? o.Css! : o.Html;
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
        public required string Html { get; init; }
        public string? Css { get; init; }
        public string? Model { get; init; }
        public required IReadOnlyList<(int Width, int Height)> Sizes { get; init; }
        public required string Out { get; init; }
        public bool Outlines { get; init; }
        public int? TreeDepth { get; init; }
        public bool Sweep { get; init; }
        public string? Fonts { get; init; }
        public Severity? FailOn { get; init; } = Severity.Error;

        public static Options Parse(string[] args)
        {
            string? html = null, css = null, model = null, outPath = null, fonts = null;
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
            if (html is null) throw new UsageException("Give the page: cupri preview page.html [page.css].");
            if (!File.Exists(html)) throw new UsageException($"No such file: {html}");
            if (css is not null && !File.Exists(css)) throw new UsageException($"No such file: {css}");
            if (model is not null && !File.Exists(model)) throw new UsageException($"No such file: {model}");
            if (fonts is not null && !Directory.Exists(fonts)) throw new UsageException($"No such folder: {fonts}");
            // The stylesheet beside the page, by name — App.html → App.css — unless one was given.
            css ??= Path.ChangeExtension(html, ".css") is var sibling && File.Exists(sibling) ? sibling : null;
            // Absolute from here on: the preview runs with the page's folder as the working directory.
            static string? Full(string? p) => p is null ? null : Path.GetFullPath(p);
            return new Options
            {
                Html = Full(html)!, Css = Full(css), Model = Full(model), Sizes = sizes, Outlines = outlines,
                Sweep = sweep, TreeDepth = tree, Fonts = Full(fonts), FailOn = failOn,
                Out = Full(outPath ?? Path.ChangeExtension(html, ".preview.png"))!,
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

    private static string Next(string[] args, ref int i, string option) =>
        ++i < args.Length ? args[i] : throw new UsageException($"{option} needs a value.");
}
