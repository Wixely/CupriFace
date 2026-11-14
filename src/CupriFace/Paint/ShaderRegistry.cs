using System.Text.RegularExpressions;
using CupriFace.Style;
using SkiaSharp;

namespace CupriFace.Paint;

/// <summary>A shader that did not compile, with what the SkSL compiler said about it.</summary>
public sealed class ShaderCompileException(string name, string error)
    : Exception($"Shader '{name}' did not compile: {error}")
{
    public string ShaderName { get; } = name;
    public string CompileError { get; } = error;
}

/// <summary>
/// One compiled SkSL shader, and what the engine learned by reading its declarations: which of
/// the built-in uniforms it asks for, how wide each uniform is, and whether it wants the
/// element's own pixels.
///
/// <para>Immutable once made, which is what lets a display list hold one across threads.</para>
/// </summary>
public sealed partial class CompiledShader
{
    /// <summary>The child shader a <c>filter: shader()</c> reads the element's pixels from.</summary>
    public const string ContentChannel = "iChannel0";

    public string Name { get; }
    /// <summary>The source as compiled — after the Shadertoy wrapper, when one was added.</summary>
    public string Source { get; }
    internal SKRuntimeEffect Effect { get; }

    /// <summary>Declares <c>iTime</c> (or <c>iFrame</c>): its output changes every frame, so a
    /// document showing it keeps its host drawing.</summary>
    public bool UsesTime { get; }
    /// <summary>Declares <c>iMouse</c>: a pointer move has to repaint it.</summary>
    public bool UsesPointer { get; }
    /// <summary>Declares <c>uniform shader iChannel0</c>.</summary>
    public bool ReadsContent { get; }
    /// <summary>Written as Shadertoy's <c>mainImage</c>, so its y axis runs UP from the bottom.</summary>
    public bool Shadertoy { get; }

    // Uniform name → how many scalars it holds, and whether they are ints.
    private readonly Dictionary<string, (int Size, bool IsInt)> _uniforms;

    private CompiledShader(string name, string source, SKRuntimeEffect effect, bool shadertoy,
                           Dictionary<string, (int, bool)> uniforms)
    {
        Name = name; Source = source; Effect = effect; Shadertoy = shadertoy; _uniforms = uniforms;
        UsesTime = uniforms.ContainsKey("iTime") || uniforms.ContainsKey("iFrame");
        UsesPointer = uniforms.ContainsKey("iMouse");
        ReadsContent = effect.Children.Contains(ContentChannel);
    }

    /// <summary>The uniforms the source declares, by name.</summary>
    public IReadOnlyCollection<string> Uniforms => _uniforms.Keys;

    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"\buniform\s+(?:(?:lowp|mediump|highp)\s+)?(\w+)\s+(\w+)\s*(?:\[\s*(\d+)\s*\])?\s*;")]
    private static partial Regex UniformDecl();

    [GeneratedRegex(@"\bmain\s*\(")]
    private static partial Regex MainFn();

    [GeneratedRegex(@"\bmainImage\s*\(")]
    private static partial Regex MainImageFn();

    // What Shadertoy declares for every shader, so a pasted one finds them. Only the ones the
    // source does not declare itself are added.
    private static readonly (string Name, string Decl)[] ShadertoyUniforms =
    [
        ("iResolution", "uniform float3 iResolution;"),
        ("iTime", "uniform float iTime;"),
        ("iTimeDelta", "uniform float iTimeDelta;"),
        ("iFrame", "uniform float iFrame;"),
        ("iMouse", "uniform float4 iMouse;"),
    ];

    internal static CompiledShader? Compile(string name, string sksl, out string? error)
    {
        var bare = Comments().Replace(sksl, " ");
        var shadertoy = MainImageFn().IsMatch(bare) && !MainFn().IsMatch(bare);
        var source = sksl;
        if (shadertoy)
        {
            // A Shadertoy shader is a function, not a program: the site supplies the uniforms and
            // the entry point. Supply the same, and turn its coordinates over — fragCoord there
            // starts at the BOTTOM left, here (as in everything else in this engine) at the top.
            // Only the uniforms the body mentions are declared, so a shader that never reads
            // iTime does not look animated and keep a host drawing frames nobody can tell apart.
            var head = new System.Text.StringBuilder();
            foreach (var (uniform, decl) in ShadertoyUniforms)
                if (uniform == "iResolution"
                    || (Regex.IsMatch(bare, $@"\b{uniform}\b") && !Regex.IsMatch(bare, $@"\buniform\s+\w+\s+{uniform}\b")))
                    head.Append(decl).Append('\n');
            source = head + sksl
                + "\nhalf4 main(float2 cupri_p) {\n"
                + "    float4 cupri_c = float4(0.0);\n"
                + "    mainImage(cupri_c, float2(cupri_p.x, iResolution.y - cupri_p.y));\n"
                // Shadertoy ignores alpha, and most shaders leave junk in it.
                + "    return half4(half3(clamp(cupri_c.rgb, 0.0, 1.0)), 1.0);\n"
                + "}\n";
            bare = Comments().Replace(source, " ");
        }

        SKRuntimeEffect? effect;
        try { effect = SKRuntimeEffect.CreateShader(source, out error); }
        catch (Exception ex) { effect = null; error = ex.Message; }
        if (effect is null)
        {
            error = string.IsNullOrWhiteSpace(error) ? "the SkSL compiler gave no reason" : error.Trim();
            return null;
        }
        error = null;

        var uniforms = new Dictionary<string, (int, bool)>(StringComparer.Ordinal);
        foreach (Match m in UniformDecl().Matches(bare))
        {
            if (ScalarCount(m.Groups[1].Value) is not { } t) continue; // a child (shader/colorFilter/blender)
            var count = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 1;
            uniforms[m.Groups[2].Value] = (t.Size * count, t.IsInt);
        }
        return new CompiledShader(name, source, effect, shadertoy, uniforms);
    }

    private static (int Size, bool IsInt)? ScalarCount(string type) => type switch
    {
        "float" or "half" => (1, false),
        "float2" or "half2" or "vec2" => (2, false),
        "float3" or "half3" or "vec3" => (3, false),
        "float4" or "half4" or "vec4" => (4, false),
        "float2x2" or "half2x2" or "mat2" => (4, false),
        "float3x3" or "half3x3" or "mat3" => (9, false),
        "float4x4" or "half4x4" or "mat4" => (16, false),
        "int" => (1, true),
        "int2" or "ivec2" => (2, true),
        "int3" or "ivec3" => (3, true),
        "int4" or "ivec4" => (4, true),
        _ => null,
    };

    /// <summary>
    /// The shader for one use, with its coordinates starting at (<paramref name="x"/>,
    /// <paramref name="y"/>) — the corner of the box it paints.
    /// </summary>
    /// <param name="content">What the element drew, in absolute coordinates, for a shader that
    /// reads <see cref="ContentChannel"/>. Null where there is nothing behind it (a background).</param>
    internal SKShader? ToShader(ShaderCall call, SKShader? content, float x, float y)
    {
        try
        {
            using var uniforms = new SKRuntimeEffectUniforms(Effect);
            // Lowest priority first: what the engine knows, then what the app set from code, then
            // what this particular use says in CSS. A stylesheet may therefore override iTime to
            // freeze a shader, which is the cheap way to take a still of one.
            var py = Shadertoy ? call.H - call.PointerY : call.PointerY;
            var pressY = Shadertoy && call.PressY != 0f ? MathF.CopySign(call.H - MathF.Abs(call.PressY), call.PressY) : call.PressY;
            Set(uniforms, "iResolution", [call.W, call.H, 1f]);
            Set(uniforms, "iTime", [call.Time]);
            Set(uniforms, "iTimeDelta", [1f / 60f]);
            Set(uniforms, "iFrame", [MathF.Floor(call.Time * 60f)]);
            Set(uniforms, "iMouse", [call.PointerX, py, call.PressX, pressY]);
            if (call.Values is { } values)
                foreach (var (uniform, v) in values) Set(uniforms, uniform, v);
            Span<float> given = stackalloc float[4];
            foreach (var arg in call.Use.Args)
            {
                for (var i = 0; i < arg.Count; i++) given[i] = arg[i];
                Set(uniforms, arg.Name, given[..arg.Count]);
            }

            using var children = new SKRuntimeEffectChildren(Effect);
            SKShader? blank = null;
            if (ReadsContent)
                children[ContentChannel] = content ?? (blank = SKShader.CreateColor(SKColors.Transparent));
            var shader = MakeShader(uniforms, children, SKMatrix.CreateTranslation(x, y));
            blank?.Dispose();
            return shader;
        }
        catch (Exception)
        {
            // A uniform whose declared width was misread (an exotic type, a macro) is refused by
            // Skia with an exception. A frame with one effect missing is the better failure.
            return null;
        }
    }

    // SkiaSharp's public ToShader copies the uniforms into an SKData and never disposes it (true
    // of 3.116 through 4.156), so every shader drawn left one wrapper for the finalizer, every
    // frame. On NativeAOT-LLVM in a browser there is no finalizer thread: it runs inside whatever
    // allocation started the collection, and when that is SkiaSharp's handle table the finalizer
    // wants a lock the table already holds — LockRecursionException, and the page is dead. So the
    // copy is made here, where it can be disposed, and handed to the overload that takes it.
    private unsafe SKShader? MakeShader(SKRuntimeEffectUniforms uniforms, SKRuntimeEffectChildren children, SKMatrix local)
    {
        if (!s_ownsUniformData) return Effect.ToShader(uniforms, children, local);
        var data = uniforms.ToData();
        try { return ToShaderOwningData(Effect, data, children.ToArray(), &local); }
        catch (MissingMethodException)
        {
            // A SkiaSharp that renamed its private overload: leak as it always did, rather than
            // paint nothing.
            s_ownsUniformData = false;
            return Effect.ToShader(uniforms, children, local);
        }
        // SKData.Empty is shared, and ignores this.
        finally { data.Dispose(); }
    }

    private static bool s_ownsUniformData = true;

    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = "ToShader")]
    private static extern unsafe SKShader ToShaderOwningData(SKRuntimeEffect effect, SKData uniforms, SKObject[] children, SKMatrix* localMatrix);

    // Fit whatever was given to what was declared: a float3 handed a colour takes its rgb, a
    // float4 handed two numbers takes zeros for the rest. A uniform the source never declared is
    // not an error — one stylesheet may drive several shaders that each read a different subset.
    private void Set(SKRuntimeEffectUniforms uniforms, string name, ReadOnlySpan<float> given)
    {
        if (!_uniforms.TryGetValue(name, out var decl)) return;
        if (decl.IsInt)
        {
            var ints = new int[decl.Size];
            for (var i = 0; i < ints.Length && i < given.Length; i++) ints[i] = (int)given[i];
            if (ints.Length == 1) uniforms[name] = ints[0];
            else uniforms[name] = ints;
            return;
        }
        var floats = new float[decl.Size];
        given[..Math.Min(given.Length, floats.Length)].CopyTo(floats);
        // A scalar is not a one-element array as far as the binding is concerned.
        if (floats.Length == 1) uniforms[name] = floats[0];
        else uniforms[name] = floats;
    }
}

/// <summary>
/// One use of a shader in one frame: everything the rasteriser needs to bind its uniforms, as a
/// value. Two frames in which a shader would draw the same thing compare equal, so a shader that
/// reads no clock and no pointer costs nothing after its first frame.
/// </summary>
/// <param name="Values">The uniforms the app set from code — a snapshot, replaced whole on every
/// change, so equality is one reference comparison.</param>
/// <param name="W">Width of the box the shader paints, in px: <c>iResolution.x</c>.</param>
/// <param name="Time">Seconds, or 0 for a shader that does not declare <c>iTime</c>.</param>
/// <param name="PointerX">Pointer position relative to the box, or 0 for a shader that does not
/// declare <c>iMouse</c>.</param>
/// <param name="PressX">Where the pointer last went down, relative to the box; negated once it
/// has been released, which is Shadertoy's convention for "not pressed".</param>
public sealed record ShaderCall(CompiledShader Shader, ShaderUse Use,
    IReadOnlyDictionary<string, float[]>? Values, float W, float H,
    float Time, float PointerX, float PointerY, float PressX, float PressY);

/// <summary>
/// SkSL shaders by name — what <c>shader(name)</c> in a stylesheet refers to.
///
/// <para>Two ways in: <see cref="Register"/> from code, or an <c>@shader name { … }</c> block in
/// the stylesheet. Either way the shader is compiled once, here, and a failure is kept (see
/// <see cref="ErrorFor"/>) rather than discovered as an element that mysteriously paints nothing.</para>
/// </summary>
public sealed partial class ShaderRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CompiledShader> _shaders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, float[]>> _values = new(StringComparer.Ordinal);

    /// <summary>The document's "something changed, repaint" hook.</summary>
    internal Action? Changed;

    /// <summary>Compile <paramref name="sksl"/> and make it available as <c>shader(name)</c>.
    /// The source is either a complete SkSL program (<c>half4 main(float2 fragCoord)</c>) or a
    /// Shadertoy-style <c>void mainImage(out vec4 fragColor, in vec2 fragCoord)</c>.</summary>
    /// <exception cref="ShaderCompileException">The source did not compile.</exception>
    public void Register(string name, string sksl)
    {
        if (!TryRegister(name, sksl, out var error)) throw new ShaderCompileException(name, error!);
    }

    /// <summary><see cref="Register"/> without the exception: false, and the compiler's message,
    /// when the source does not compile. The name then refers to nothing, and
    /// <see cref="ErrorFor"/> remembers why.</summary>
    public bool TryRegister(string name, string sksl, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var compiled = CompiledShader.Compile(name, sksl ?? "", out error);
        lock (_lock)
        {
            if (compiled is null) { _shaders.Remove(name); _errors[name] = error!; }
            else { _shaders[name] = compiled; _errors.Remove(name); }
        }
        Changed?.Invoke();
        return compiled is not null;
    }

    public bool Unregister(string name)
    {
        bool removed;
        lock (_lock) { removed = _shaders.Remove(name); _errors.Remove(name); }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public CompiledShader? Get(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (_lock) return _shaders.TryGetValue(name, out var s) ? s : null;
    }

    /// <summary>Why <paramref name="name"/> failed to compile, or null if it compiled or was never
    /// registered at all.</summary>
    public string? ErrorFor(string name)
    {
        lock (_lock) return _errors.TryGetValue(name, out var e) ? e : null;
    }

    /// <summary>The names whose source did not compile.</summary>
    public IReadOnlyList<string> FailedNames { get { lock (_lock) return [.. _errors.Keys]; } }

    /// <summary>The names that compiled.</summary>
    public IReadOnlyList<string> Names { get { lock (_lock) return [.. _shaders.Keys]; } }

    /// <summary>
    /// Give one of a shader's uniforms a value from code — for every element using that shader.
    /// A value written where the shader is used (<c>shader(name, uniform 0.5)</c>) wins over this
    /// one. Like every other member of the document, call it on the UI thread, or through
    /// <c>doc.Post</c>.
    /// </summary>
    public void SetUniform(string shader, string uniform, params float[] values)
    {
        lock (_lock)
        {
            // Copy-on-write: a display list already handed to a render thread keeps the snapshot
            // it was built with.
            var next = _values.TryGetValue(shader, out var cur)
                ? new Dictionary<string, float[]>(cur, StringComparer.Ordinal)
                : new Dictionary<string, float[]>(StringComparer.Ordinal);
            next[uniform] = [.. values];
            _values[shader] = next;
        }
        Changed?.Invoke();
    }

    internal IReadOnlyDictionary<string, float[]>? ValuesFor(string shader)
    {
        lock (_lock) return _values.TryGetValue(shader, out var v) ? v : null;
    }

    [GeneratedRegex(@"@shader\s+([A-Za-z_][\w-]*)\s*\{")]
    private static partial Regex ShaderHeader();

    /// <summary>Every <c>@shader name { …SkSL… }</c> block in a stylesheet, as (name, source).
    /// The body is taken verbatim up to the matching brace; braces inside comments are stepped
    /// over, so a commented-out block does not end the shader early.</summary>
    public static List<(string Name, string Source)> ParseBlocks(string? css)
    {
        var found = new List<(string, string)>();
        if (string.IsNullOrEmpty(css) || !css.Contains("@shader", StringComparison.Ordinal)) return found;
        foreach (Match m in ShaderHeader().Matches(css))
        {
            var start = m.Index + m.Length;
            var depth = 1;
            var i = start;
            for (; i < css.Length && depth > 0; i++)
            {
                var c = css[i];
                if (c == '/' && i + 1 < css.Length && css[i + 1] == '/')
                { var nl = css.IndexOf('\n', i); i = nl < 0 ? css.Length : nl; }
                else if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
                { var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? css.Length : end + 1; }
                else if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            if (depth == 0) found.Add((m.Groups[1].Value, css[start..(i - 1)]));
        }
        return found;
    }

    /// <summary>Compile a stylesheet's <c>@shader</c> blocks. Nothing throws: a stylesheet is
    /// not code, and the failure is kept for <see cref="ErrorFor"/> and CupriDoctor (CF0054).</summary>
    internal void RegisterFromCss(string? css)
    {
        foreach (var (name, source) in ParseBlocks(css)) TryRegister(name, source, out _);
    }
}
