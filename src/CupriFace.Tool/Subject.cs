using System.Reflection;
using System.Runtime.Loader;
using CupriFace.Components;
using CupriFace.Svg;

namespace CupriFace.Tool;

/// <summary>
/// What is being previewed: markup files on disk, or a built app. Both come down to the same five
/// things — markup, stylesheet, model, component library, and the configuration an app applies —
/// plus where the markup lives on disk, so a finding can name the file and line.
/// </summary>
internal sealed class Subject
{
    public required string Name { get; init; }
    public required string Html { get; init; }
    public required string Css { get; init; }
    public string? HtmlPath { get; init; }
    public string? CssPath { get; init; }
    public object? Model { get; init; }
    public required ComponentRegistry Components { get; init; }
    /// <summary>What the doctor applies to the documents it builds for itself.</summary>
    public required Action<CupriDocument> Configure { get; init; }
    /// <summary>The document to render — the app's own, when there is an app.</summary>
    public required Func<CupriDocument> Create { get; init; }
    /// <summary>Where relative image and font paths in the markup resolve from.</summary>
    public required string WorkDir { get; init; }
    public required string Description { get; init; }
    /// <summary>The size an app asks its window to open at; null for a page.</summary>
    public (int Width, int Height)? WindowSize { get; init; }

    public static Subject FromFiles(string html, string? css, string? model, string? fonts)
    {
        var htmlText = File.ReadAllText(html);
        var cssText = css is null ? "" : File.ReadAllText(css);
        var json = model is null ? null : JsonModel.Parse(File.ReadAllText(model));
        var components = ComponentRegistry.Default();
        void Configure(CupriDocument d)
        {
            d.UseSvg();
            if (fonts is not null) d.LoadFonts(fonts);
        }
        // Relative paths in the markup resolve against the page's folder — or, for a page in an app's
        // Assets/ folder, against the project, because an app names its embedded assets
        // "Assets/logo.png" from there and that is what its markup says.
        var pageDir = Path.GetDirectoryName(html)!;
        return new Subject
        {
            Name = Path.GetFileNameWithoutExtension(html),
            Html = htmlText, Css = cssText, HtmlPath = html, CssPath = css, Model = json,
            Components = components, Configure = Configure,
            Create = () =>
            {
                var d = CupriDocument.Load(htmlText, cssText).UseComponents(components);
                Configure(d);
                if (json is not null) d.Bind(json);
                return d;
            },
            WorkDir = Path.GetFileName(pageDir).Equals("Assets", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(pageDir)! : pageDir,
            Description = Path.GetFileName(html)
                + (css is null ? " (no stylesheet)" : $" + {Path.GetFileName(css)}")
                + (model is null ? "" : $" bound to {Path.GetFileName(model)}"),
        };
    }

    /// <summary>
    /// A built app: load its assembly, construct its <see cref="CupriApp"/>, and preview it with
    /// its own model, components and <c>Configure</c> — the real document, not a mock-up.
    ///
    /// <para><b>The engine is the tool's.</b> The app's assembly is loaded beside this tool's
    /// CupriFace, which the app's references bind to; an app built against a very different
    /// version may not load. Build the app first: its markup is read from the assembly, as it runs.
    /// The files behind it are found through the project directory a Debug build stamps in
    /// (<c>CupriFace.AssetsRoot</c>), so findings still name them.</para>
    /// </summary>
    public static Subject FromApp(string dll, string? typeName, string? fonts)
    {
        var assembly = Load(dll);
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.OfType<Type>().ToArray(); }
        var apps = types.Where(t => typeof(CupriApp).IsAssignableFrom(t) && !t.IsAbstract && Constructor(t) is not null).ToList();
        if (typeName is not null) apps = apps.Where(t => t.Name == typeName || t.FullName == typeName).ToList();
        if (apps.Count == 0)
            throw new Cli.UsageException(typeName is null
                ? $"{Path.GetFileName(dll)} has no CupriApp with a constructor that needs no arguments."
                : $"{Path.GetFileName(dll)} has no CupriApp called {typeName}.");
        if (apps.Count > 1)
            throw new Cli.UsageException($"{Path.GetFileName(dll)} has several apps — pick one with --type: {string.Join(", ", apps.Select(t => t.Name))}");

        var type = apps[0];
        var ctor = Constructor(type)!;
        var app = (CupriApp)ctor.Invoke(ctor.GetParameters().Select(p => p.DefaultValue).ToArray());
        var html = app.Html;
        var css = app.Css;
        var model = app.Model;
        var components = app.Components;
        void Configure(CupriDocument d)
        {
            // What CreateDocument does that matters to a check: images and fonts from the app's own
            // assembly, then the app's Configure. The SVG package too, which an app may wire in its
            // host rather than in Configure.
            d.UseImages(type.Assembly);
            foreach (var font in app.Fonts) d.LoadFont(font);
            app.Configure(d);
            d.UseSvg();
            if (fonts is not null) d.LoadFonts(fonts);
        }

        var root = AssetsRoot(type.Assembly, dll);
        var htmlPath = root is null ? null : FindByContent(root, "*.html", html);
        var cssPath = root is null || css.Length == 0 ? null : FindByContent(root, "*.css", css);
        return new Subject
        {
            Name = type.Name,
            Html = html, Css = css, HtmlPath = htmlPath, CssPath = cssPath, Model = model,
            Components = components, Configure = Configure,
            Create = () =>
            {
                var d = app.CreateDocument();
                d.UseSvg();
                if (fonts is not null) d.LoadFonts(fonts);
                return d;
            },
            WorkDir = root ?? Path.GetDirectoryName(dll)!,
            WindowSize = (app.Width, app.Height),
            Description = $"{type.Name} from {Path.GetFileName(dll)}"
                + (htmlPath is null ? "" : $" ({Path.GetFileName(htmlPath)}{(cssPath is null ? "" : " + " + Path.GetFileName(cssPath))})")
                + (model is null ? "" : $" bound to its {model.GetType().Name}"),
        };
    }

    private static ConstructorInfo? Constructor(Type t) =>
        t.GetConstructors().FirstOrDefault(c => c.GetParameters().All(p => p.IsOptional));

    /// <summary>Load the app beside this tool. Anything already loaded under the same name —
    /// CupriFace above all, which the app's types derive from — is the copy used; the app's other
    /// dependencies come from its own output folder.</summary>
    private static Assembly Load(string dll)
    {
        var name = AssemblyName.GetAssemblyName(dll).Name;
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
        if (loaded is not null) return loaded;
        var dir = Path.GetDirectoryName(dll)!;
        AssemblyLoadContext.Default.Resolving += (ctx, an) =>
        {
            var candidate = Path.Combine(dir, an.Name + ".dll");
            return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
        };
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
    }

    /// <summary>The project directory an app's Assets/ came from: stamped into a Debug build by
    /// CupriFace.Resources.targets, else found by walking up from the build output.</summary>
    private static string? AssetsRoot(Assembly asm, string dll)
    {
        foreach (var attr in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (attr.Key == "CupriFace.AssetsRoot" && attr.Value is { Length: > 0 } v && Directory.Exists(v)) return v;
        var name = asm.GetName().Name;
        for (var d = Path.GetDirectoryName(dll); d is not null; d = Path.GetDirectoryName(d))
        {
            if (Directory.Exists(Path.Combine(d, "Assets"))) return d;
            if (name is not null && Directory.Exists(Path.Combine(d, name, "Assets"))) return Path.Combine(d, name);
        }
        return null;
    }

    /// <summary>The file under the project whose text is the app's markup (or stylesheet). By content,
    /// not by name, because an app may build its markup from several assets or choose one at run
    /// time; when nothing matches exactly, findings fall back to naming the app's property.</summary>
    private static string? FindByContent(string root, string pattern, string text)
    {
        var want = text.Replace("\r\n", "\n");
        foreach (var f in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            try { if (File.ReadAllText(f).Replace("\r\n", "\n") == want) return f; }
            catch (IOException) { }
        }
        return null;
    }
}
