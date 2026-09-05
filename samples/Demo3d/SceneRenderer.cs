using System.Text;

namespace CupriFace.Demo.ThreeD;

/// <summary>
/// Draws a whole glTF scene: every primitive, each with its own material, shaded with the
/// metallic-roughness BRDF the format actually specifies.
///
/// <para>Replaces the earlier single-mesh Lambert renderer. Both limitations were fine for a teapot
/// and would have misrepresented the engine: "only the first mesh" renders a fraction of any real
/// asset, and "Lambert" is not what a glTF material means, so a proof built on either would have been
/// answering an easier question than the one asked.</para>
///
/// <para>One shader source, two dialects. Desktop needs <c>#version 330 core</c>; web and Android
/// both take <c>#version 300 es</c> plus a precision qualifier, because WebGL2 IS GLES 3.0. That one
/// header is the entire portability tax on the shading side.</para>
/// </summary>
public sealed unsafe class SceneRenderer
{
    private readonly Gltf _scene;
    private readonly bool _es;
    private uint _prog;
    private int _mvpLoc, _camLoc, _baseColorLoc, _metalLoc, _roughLoc, _hasTexLoc;
    private readonly List<(uint Vao, int IndexCount, Gltf.Primitive Prim)> _draws = [];
    private readonly List<uint> _textures = [];
    // Tracked only so they can be deleted. Nothing reads this list otherwise.
    private readonly List<uint> _buffers = [];

    public SceneRenderer(Gltf scene, bool glslEs) { _scene = scene; _es = glslEs; }

    public Gltf Scene => _scene;
    public int DrawCalls => _draws.Count;
    public int TextureCount => _textures.Count;

    private string Header => _es
        ? "#version 300 es\nprecision highp float;\n"
        : "#version 330 core\n";

    private static void Source(uint shader, string src)
    {
        var bytes = Encoding.UTF8.GetBytes(src + "\0");
        fixed (byte* p = bytes)
        {
            byte** one = stackalloc byte*[1];
            one[0] = p;
            Gl.ShaderSource(shader, 1, one, null);
        }
    }

    private static uint Compile(uint type, string src, string label, Action<string> log)
    {
        var s = Gl.CreateShader(type);
        Source(s, src);
        Gl.CompileShader(s);
        int ok; Gl.GetShaderiv(s, Gl.COMPILE_STATUS, &ok);
        if (ok != 0) return s;
        var buf = stackalloc byte[2048];
        Gl.GetShaderInfoLog(s, 2048, null, buf);
        log($"FAIL {label} shader: {Gl.Str(buf)}");
        return 0;
    }

    private static int Uniform(uint prog, string name)
    {
        var b = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = b) return Gl.GetUniformLocation(prog, p);
    }

    private static void Attrib(uint prog, uint index, string name)
    {
        var b = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = b) Gl.BindAttribLocation(prog, index, p);
    }

    /// <summary><paramref name="decodeRgba"/> keeps the codec out of the renderer: the caller turns
    /// encoded bytes into RGBA however its platform already can — Skia on desktop and the web,
    /// BitmapFactory on Android.</summary>
    public bool Initialise(Func<byte[], (byte[] Pixels, int W, int H)?> decodeRgba, Action<string> log)
    {
        var vs = Compile(Gl.VERTEX_SHADER, Header + """
            in vec3 aPos;
            in vec3 aNormal;
            in vec2 aUv;
            uniform mat4 uMvp;
            out vec3 vNormal;
            out vec3 vWorld;
            out vec2 vUv;
            void main() {
                vNormal = aNormal;
                // Node transforms are baked into the vertex data at load, so object space IS world
                // space here. A renderer with per-frame model matrices would pass one and multiply.
                vWorld = aPos;
                vUv = aUv;
                gl_Position = uMvp * vec4(aPos, 1.0);
            }
            """, "vertex", log);

        // Cook-Torrance metallic-roughness: GGX distribution, Smith geometry, Schlick Fresnel — the
        // BRDF glTF's pbrMetallicRoughness actually names. One directional light plus a flat ambient
        // term standing in for IBL, which is the honest simplification: without an environment map
        // there is nothing for a metal to reflect, so pure metals go dark. Real IBL is the next
        // thing this would need, and its absence is visible rather than hidden.
        var fs = Compile(Gl.FRAGMENT_SHADER, Header + """
            in vec3 vNormal;
            in vec3 vWorld;
            in vec2 vUv;
            uniform vec4 uBaseColor;
            uniform float uMetallic;
            uniform float uRoughness;
            uniform vec3 uCamPos;
            uniform sampler2D uTex;
            uniform int uHasTex;
            out vec4 fragColor;

            const float PI = 3.14159265359;

            void main() {
                vec3 albedo = uBaseColor.rgb;
                if (uHasTex == 1) albedo *= texture(uTex, vUv).rgb;

                vec3 N = normalize(vNormal);
                vec3 V = normalize(uCamPos - vWorld);
                vec3 L = normalize(vec3(0.35, 0.75, 0.55));
                vec3 H = normalize(V + L);

                float NdotL = max(dot(N, L), 0.0);
                float NdotV = max(dot(N, V), 0.0001);
                float NdotH = max(dot(N, H), 0.0);
                float VdotH = max(dot(V, H), 0.0);

                float rough = clamp(uRoughness, 0.05, 1.0);
                float a = rough * rough;
                float a2 = a * a;
                float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
                float D = a2 / max(PI * denom * denom, 0.0001);

                float k = (rough + 1.0) * (rough + 1.0) / 8.0;
                float G = (NdotV / (NdotV * (1.0 - k) + k)) * (NdotL / (NdotL * (1.0 - k) + k));

                vec3 F0 = mix(vec3(0.04), albedo, uMetallic);
                vec3 F = F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);

                vec3 spec = (D * G * F) / max(4.0 * NdotV * NdotL, 0.0001);
                vec3 kD = (vec3(1.0) - F) * (1.0 - uMetallic);
                vec3 direct = (kD * albedo / PI + spec) * NdotL;

                vec3 ambient = albedo * 0.14 * (1.0 - uMetallic * 0.6);
                vec3 colour = direct * 2.6 + ambient;

                colour = colour / (colour + vec3(1.0));          // Reinhard
                colour = pow(colour, vec3(1.0 / 2.2));           // to sRGB
                fragColor = vec4(colour, uBaseColor.a);
            }
            """, "fragment", log);
        if (vs == 0 || fs == 0) return false;

        _prog = Gl.CreateProgram();
        Gl.AttachShader(_prog, vs); Gl.AttachShader(_prog, fs);
        Attrib(_prog, 0, "aPos"); Attrib(_prog, 1, "aNormal"); Attrib(_prog, 2, "aUv");
        Gl.LinkProgram(_prog);
        int linked; Gl.GetProgramiv(_prog, Gl.LINK_STATUS, &linked);
        if (linked == 0)
        {
            var buf = stackalloc byte[2048];
            Gl.GetProgramInfoLog(_prog, 2048, null, buf);
            log($"FAIL link: {Gl.Str(buf)}");
            return false;
        }
        Gl.UseProgram(_prog);

        _mvpLoc = Uniform(_prog, "uMvp");
        _camLoc = Uniform(_prog, "uCamPos");
        _baseColorLoc = Uniform(_prog, "uBaseColor");
        _metalLoc = Uniform(_prog, "uMetallic");
        _roughLoc = Uniform(_prog, "uRoughness");
        _hasTexLoc = Uniform(_prog, "uHasTex");
        Gl.Uniform1i(Uniform(_prog, "uTex"), 0);

        // Textures first: uploaded once each and shared by every primitive that names them, which is
        // why the loader deduplicates by glTF image index rather than per material.
        foreach (var encoded in _scene.Images)
        {
            uint tex = 0;
            if (decodeRgba(encoded) is { } img)
            {
                Gl.GenTextures(1, &tex);
                Gl.BindTexture(Gl.TEXTURE_2D, tex);
                fixed (byte* p = img.Pixels)
                    Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RGBA8, img.W, img.H, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, p);

                // THE SAMPLER THE ASSET ASKED FOR, not one invented here.
                //
                // This used to hardcode LINEAR_MIPMAP_LINEAR and call GenerateMipmap on everything.
                // The teapot's own sampler declares plain LINEAR and no mipmaps, and forcing them on
                // its 701x561 NPOT texture rendered as coloured speckle on a PowerVR phone while a
                // desktop NVIDIA driver showed nothing wrong. NPOT mipmaps are legal in GLES 3.0;
                // they are evidently not equally well travelled, and the asset never wanted them.
                //
                // A primitive carries the sampler, an image is shared, so the first primitive that
                // names this image supplies it. Two materials sampling one image differently would
                // need a sampler object per reference — noted rather than built, since this is a
                // sample and no real asset here does it.
                var smp = _scene.Primitives.Find(pr => pr.ImageIndex == _textures.Count);
                var (minF, magF, wrapS, wrapT) = smp is null
                    ? (0x2601, 0x2601, 0x2901, 0x2901)          // GL_LINEAR / GL_REPEAT
                    : (smp.MinFilter, smp.MagFilter, smp.WrapS, smp.WrapT);

                // WRAP is honoured exactly — this teapot's unwrap runs u 0..2 and v -1..1, so REPEAT
                // is not a preference, it is the difference between a tiled texture and a clamped
                // smear. MAG is honoured too.
                //
                // MIN is the one place the asset is overruled, and only upwards. A texture minified
                // without a mip chain aliases: this model's base colour is a photograph of paint,
                // tiled twice in each axis, and at viewport size it broke into shimmering streaks
                // that read as a broken UV mapping. Exporters emit minFilter=LINEAR by default
                // rather than as an artistic decision, and no asset means "please alias", so a
                // non-mipmapped minification filter is upgraded to its mipmapped equivalent.
                //
                // Honouring it literally was tried first and made the desktop visibly worse, which
                // is the evidence for this paragraph existing.
                var mipped = minF is 0x2700 or 0x2701 or 0x2702 or 0x2703;
                if (!mipped)
                {
                    minF = minF == 0x2600 ? 0x2700 : 0x2703;   // NEAREST->NEAREST_MIPMAP_NEAREST, else LINEAR_MIPMAP_LINEAR
                    mipped = true;
                }
                Gl.GenerateMipmap(Gl.TEXTURE_2D);

                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEX_MIN_FILTER, minF);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEX_MAG_FILTER, magF);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEX_WRAP_S, wrapS);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEX_WRAP_T, wrapT);

                // ANISOTROPY, which is what this model actually needs. Its unwrap is a lathe: the u
                // gradient around the ring is enormous compared with v, so isotropic mip selection
                // takes the worst axis and picks a level far blurrier than the surface deserves —
                // a photograph of paint collapsing to a flat average colour. Without mips it
                // aliases into streaks instead; anisotropy is the option that is neither.
                //
                // An extension, so it is asked for and not assumed: query the driver's ceiling and
                // take the lesser of that and 8. A driver without it leaves the value alone.
                float maxAniso = 0;
                Gl.GetFloatv(Gl.MAX_MAX_ANISOTROPY, &maxAniso);
                if (maxAniso > 1f)
                    Gl.TexParameterf(Gl.TEXTURE_2D, Gl.MAX_ANISOTROPY, MathF.Min(8f, maxAniso));
                log($"texture {_textures.Count} decoded {img.W}x{img.H} -> rgba8888 " +
                    $"(min=0x{minF:X4} mag=0x{magF:X4} wrap=0x{wrapS:X4} mips={mipped} aniso={MathF.Min(8f, MathF.Max(1f, maxAniso))})");
            }
            else log($"WARN image {_textures.Count} did not decode");
            _textures.Add(tex);
        }

        // One VAO per primitive. The VAO is what makes the per-draw state a single bind rather than
        // five calls, and it is also what stops one primitive's attribute layout leaking into the next.
        foreach (var prim in _scene.Primitives)
        {
            uint vao, vbo, ebo;
            Gl.GenVertexArrays(1, &vao); Gl.BindVertexArray(vao);
            Gl.GenBuffers(1, &vbo); Gl.BindBuffer(Gl.ARRAY_BUFFER, vbo); _buffers.Add(vbo);
            fixed (float* v = prim.Vertices)
                Gl.BufferData(Gl.ARRAY_BUFFER, prim.Vertices.Length * sizeof(float), v, Gl.STATIC_DRAW);
            var stride = 8 * sizeof(float);
            Gl.VertexAttribPointer(0, 3, Gl.FLOAT, 0, stride, (void*)0); Gl.EnableVertexAttribArray(0);
            Gl.VertexAttribPointer(1, 3, Gl.FLOAT, 0, stride, (void*)(3 * sizeof(float))); Gl.EnableVertexAttribArray(1);
            Gl.VertexAttribPointer(2, 2, Gl.FLOAT, 0, stride, (void*)(6 * sizeof(float))); Gl.EnableVertexAttribArray(2);
            Gl.GenBuffers(1, &ebo); Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, ebo); _buffers.Add(ebo);
            fixed (uint* i = prim.Indices)
                Gl.BufferData(Gl.ELEMENT_ARRAY_BUFFER, prim.Indices.Length * sizeof(uint), i, Gl.STATIC_DRAW);
            _draws.Add((vao, prim.Indices.Length, prim));
        }
        Gl.BindVertexArray(0);

        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LESS);
        return true;
    }

    public static float[] Perspective(float fovY, float aspect, float near, float far)
    {
        var f = 1f / MathF.Tan(fovY / 2f);
        var m = new float[16];
        m[0] = f / aspect; m[5] = f;
        m[10] = (far + near) / (near - far); m[11] = -1f;
        m[14] = 2f * far * near / (near - far);
        return m;
    }

    public static float[] LookAt(float ex, float ey, float ez, float cx, float cy, float cz)
    {
        float zx = ex - cx, zy = ey - cy, zz = ez - cz;
        var zl = MathF.Sqrt(zx * zx + zy * zy + zz * zz); zx /= zl; zy /= zl; zz /= zl;
        float xx = zz, xy = 0f, xz = -zx;
        var xl = MathF.Sqrt(xx * xx + xy * xy + xz * xz); xx /= xl; xy /= xl; xz /= xl;
        float yx = zy * xz - zz * xy, yy = zz * xx - zx * xz, yz = zx * xy - zy * xx;
        return new float[16]
        {
            xx, yx, zx, 0, xy, yy, zy, 0, xz, yz, zz, 0,
            -(xx * ex + xy * ey + xz * ez), -(yx * ex + yy * ey + yz * ez), -(zx * ex + zy * ey + zz * ez), 1,
        };
    }

    /// <summary>Camera position for a given orbit angle, framed from the scene's own bounds. Returned
    /// as well as used because the PBR shader needs the eye point for its view vector — a Lambert
    /// shader did not, which is the one structural difference between the two.</summary>
    public (float[] Mvp, float Ex, float Ey, float Ez) Camera(float angle, float aspect)
    {
        float cx = (_scene.Min[0] + _scene.Max[0]) / 2f;
        float cy = (_scene.Min[1] + _scene.Max[1]) / 2f;
        float cz = (_scene.Min[2] + _scene.Max[2]) / 2f;
        float dx = _scene.Max[0] - _scene.Min[0], dy = _scene.Max[1] - _scene.Min[1], dz = _scene.Max[2] - _scene.Min[2];
        var radius = MathF.Sqrt(dx * dx + dy * dy + dz * dz) / 2f;
        var fov = 45f * MathF.PI / 180f;
        var dist = radius / MathF.Sin(fov / 2f) * 1.15f;
        var ex = cx + MathF.Sin(angle) * dist;
        var ey = cy + dist * 0.35f;
        var ez = cz + MathF.Cos(angle) * dist;
        var proj = Perspective(fov, aspect, radius * 0.01f, dist + radius * 4f);
        return (Gltf.Multiply(proj, LookAt(ex, ey, ez, cx, cy, cz)), ex, ey, ez);
    }

    /// <summary>Draw the scene <paramref name="instances"/> times on a grid, for measuring rather
    /// than for looking at. Each instance gets its own MVP so they occupy different screen space —
    /// drawing the same geometry in the same place would let the depth test reject almost every
    /// fragment after the first, measuring vertex throughput while appearing to measure fill.</summary>
    public void DrawInstances(float angle, int w, int h, int instances, float bgR, float bgG, float bgB, float bgA)
    {
        Gl.UseProgram(_prog);
        ResetState();
        Gl.Viewport(0, 0, w, h);
        Gl.ClearColor(bgR, bgG, bgB, bgA);
        Gl.ClearBits(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);

        // FIXED grid and FIXED zoom, deliberately not scaled to the instance count. An earlier
        // version zoomed out as the count rose, so each instance shrank and fill fell while draw
        // calls climbed — the two moved together and the resulting numbers were non-monotonic
        // (100 instances "faster" than 50, 1000 "faster" than 500), which is the signature of a
        // measurement of nothing. Holding the size constant means the only thing varying is the
        // count, so this measures draw-call and vertex cost with bounded fill; instances past the
        // 36 that fit are clipped, and pay vertex and call cost without fill.
        const int side = 6;
        const float zoomFactor = 1f / 3.6f;
        float rx = _scene.Max[0] - _scene.Min[0], ry = _scene.Max[1] - _scene.Min[1];
        var (baseMvp, ex, ey, ez) = Camera(angle, (float)w / h);
        Gl.Uniform3f(_camLoc, ex, ey, ez);

        for (var i = 0; i < instances; i++)
        {
            // Spread on a grid, then pull the camera back by the grid's size so the whole lot stays
            // in frame — otherwise the instance count silently becomes a fill-rate experiment.
            var gx = (i % side) - (side - 1) / 2f;
            var gy = ((i / side) % side) - (side - 1) / 2f;
            var t = Gltf.Identity();
            t[12] = gx * rx * 1.2f; t[13] = gy * ry * 1.2f;
            var zoom = Gltf.Identity();
            zoom[0] = zoom[5] = zoom[10] = zoomFactor;
            var mvp = Gltf.Multiply(baseMvp, Gltf.Multiply(zoom, t));
            fixed (float* p = mvp) Gl.UniformMatrix4fv(_mvpLoc, 1, 0, p);

            foreach (var (vao, count, prim) in _draws)
            {
                Gl.Uniform4f(_baseColorLoc, prim.BaseColor[0], prim.BaseColor[1], prim.BaseColor[2], prim.BaseColor[3]);
                Gl.Uniform1f(_metalLoc, prim.Metallic);
                Gl.Uniform1f(_roughLoc, prim.Roughness);
                var tex = prim.ImageIndex >= 0 && prim.ImageIndex < _textures.Count ? _textures[prim.ImageIndex] : 0;
                if (tex != 0) { Gl.BindTexture(Gl.TEXTURE_2D, tex); Gl.Uniform1i(_hasTexLoc, 1); }
                else Gl.Uniform1i(_hasTexLoc, 0);
                Gl.BindVertexArray(vao);
                Gl.DrawElements(Gl.TRIANGLES, count, Gl.UNSIGNED_INT, (void*)0);
            }
        }
        Gl.BindVertexArray(0);
    }


    /// <summary>
    /// Put the pipeline into the state this renderer assumes, instead of inheriting whatever the
    /// last user of the context left set.
    ///
    /// <para>This became necessary the moment a surface started drawing on the HOST'S context
    /// (<c>IGpuSurfaceSource</c>) rather than a private one. Skia is a heavy user of exactly these
    /// switches — it clips with the scissor box and the stencil buffer, and it blends almost
    /// everything — so a producer that sets only the state it changes is really running with a
    /// pipeline configured by someone else's last draw call. That is invisible until a different
    /// driver leaves different state behind: on one desktop the teapot looked right, and on a
    /// PowerVR phone it came out speckled and half-transparent.</para>
    ///
    /// <para>The mirror image is already handled: SurfaceRegistry calls
    /// <c>GRContext.ResetContext()</c> AFTER a producer runs, so Skia recovers from us. This is the
    /// other direction — us recovering from Skia — and nothing but the producer can do it.</para>
    /// </summary>
    private static void ResetState()
    {
        // A BOUND SAMPLER OBJECT OVERRIDES EVERY TEXTURE PARAMETER. Skia binds them, so our
        // REPEAT/filter/LOD settings were being ignored at draw time in favour of Skia's — which
        // clamps. With this model's unwrap running u 0..2, more than half the surface then sampled
        // one edge texel, which is why it looked like a stretched planar projection rather than a
        // tiled texture. Unbinding unit 0 hands control back to the texture's own parameters.
        if (Gl.BindSampler is not null) Gl.BindSampler(0, 0);

        Gl.Disable(Gl.BLEND);           // Skia blends constantly; our model is opaque
        Gl.Disable(Gl.SCISSOR_TEST);    // …and clips with the scissor box, which would crop us
        Gl.Disable(Gl.STENCIL_TEST);    // …and with the stencil buffer, which would punch holes in us
        Gl.Disable(Gl.CULL_FACE);       // the teapot is authored single-sided either way
        Gl.Disable(Gl.DITHER);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LESS);
        Gl.DepthMask(1);                // a depth-masked context would let the far side draw last
        Gl.ColorMask(1, 1, 1, 1);       // and a masked channel is how a teapot loses its blue
    }

    public void Draw(float angle, int w, int h, float bgR, float bgG, float bgB, float bgA)
    {
        ResetState();
        Gl.Viewport(0, 0, w, h);
        Gl.ClearColor(bgR, bgG, bgB, bgA);
        Gl.ClearBits(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
        Draw(angle, w, h);
    }

    /// <summary>
    /// Draw with the state, viewport and clear ALREADY DONE by whoever owns the context — which is
    /// what <c>CupriFace.Gl</c> promises its content, and is why this overload exists.
    ///
    /// <para>Worth having as a separate entry point rather than a flag: under the package those four
    /// things are guaranteed, and repeating them here would be a second implementation of a contract
    /// that has exactly one correct version. The standalone probe, which owns its own window and its
    /// own context, calls the overload above and still needs them.</para>
    /// </summary>
    public void Draw(float angle, int w, int h)
    {
        Gl.UseProgram(_prog);
        var (mvp, ex, ey, ez) = Camera(angle, (float)w / h);
        fixed (float* p = mvp) Gl.UniformMatrix4fv(_mvpLoc, 1, 0, p);
        Gl.Uniform3f(_camLoc, ex, ey, ez);

        foreach (var (vao, count, prim) in _draws)
        {
            Gl.Uniform4f(_baseColorLoc, prim.BaseColor[0], prim.BaseColor[1], prim.BaseColor[2], prim.BaseColor[3]);
            Gl.Uniform1f(_metalLoc, prim.Metallic);
            Gl.Uniform1f(_roughLoc, prim.Roughness);

            var tex = prim.ImageIndex >= 0 && prim.ImageIndex < _textures.Count ? _textures[prim.ImageIndex] : 0;
            // Re-bound every draw rather than trusting earlier state: whoever owns the context may
            // have bound something else since, and an offscreen host binds its own colour attachment
            // to TEXTURE_2D — which reads back as a solid black model if this is skipped.
            if (tex != 0) { Gl.BindTexture(Gl.TEXTURE_2D, tex); Gl.Uniform1i(_hasTexLoc, 1); }
            else Gl.Uniform1i(_hasTexLoc, 0);

            Gl.BindVertexArray(vao);
            Gl.DrawElements(Gl.TRIANGLES, count, Gl.UNSIGNED_INT, (void*)0);
        }
        Gl.BindVertexArray(0);
    }

    /// <summary>
    /// Delete every GL object this renderer made. Must be called with the owning context CURRENT,
    /// which is exactly the guarantee <c>IGlContent.Shutdown</c> gives — and the reason that hook
    /// exists rather than a plain IDisposable.
    ///
    /// <para>The demo used to leak all of this, which is invisible in something that runs once and
    /// is a real leak in an app whose user opens and closes a panel. Idempotent, because a teardown
    /// that can only safely happen once is a teardown that will be called twice.</para>
    /// </summary>
    public void Dispose()
    {
        if (_prog != 0) { Gl.DeleteProgram(_prog); _prog = 0; }

        void DeleteAll(List<uint> names, delegate* unmanaged<int, uint*, void> del)
        {
            foreach (var name in names) { var n = name; del(1, &n); }
            names.Clear();
        }

        DeleteAll(_textures, Gl.DeleteTextures);
        DeleteAll(_buffers, Gl.DeleteBuffers);
        foreach (var (vao, _, _) in _draws) { var v = vao; Gl.DeleteVertexArrays(1, &v); }
        _draws.Clear();
    }
}
