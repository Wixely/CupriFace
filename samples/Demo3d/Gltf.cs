using System.Text.Json;

namespace CupriFace.Demo.ThreeD;

/// <summary>
/// A GLB reader small enough to read in one sitting, and deliberately not a library. It exists to
/// answer one question — does a real exported model survive the whole path from bytes to pixels —
/// so it handles what real files contain and refuses the rest loudly rather than half-supporting it.
///
/// <para>JsonDocument, not JsonSerializer&lt;T&gt;: the DOM reader uses no reflection, so it is
/// trim-clean by construction under TrimMode=full. A shipping loader would want source-generated
/// contexts for speed; both are AOT-safe, which is the property that matters here and the one Stride
/// cannot currently meet.</para>
///
/// <para>It walks the WHOLE scene and returns every primitive with its own material. The first
/// version returned only the first mesh of the first node that had one, which was fine for a teapot
/// and would have silently rendered a fraction of anything real — the kind of limitation that makes
/// a proof worthless the moment someone tries their own asset.</para>
/// </summary>
public sealed class Gltf
{
    /// <summary>One drawable: its own buffer, its own material. Kept separate rather than merged
    /// because materials differ per primitive and merging would need a material atlas, which is a
    /// real renderer's problem and not this file's.</summary>
    public sealed class Primitive
    {
        public required float[] Vertices;      // interleaved px,py,pz, nx,ny,nz, u,v — world space
        public required uint[] Indices;
        public required float[] BaseColor;     // rgba, MULTIPLIES the texture per the spec
        public required float Metallic;
        public required float Roughness;
        public required int ImageIndex;        // index into Images, or -1

        /// <summary>The glTF sampler for that image, as GL enums, defaulted per the spec when the
        /// asset omits one. Carried rather than assumed because the renderer previously hardcoded
        /// LINEAR_MIPMAP_LINEAR and generated mipmaps for every texture — which the teapot's own
        /// sampler never asked for (it declares plain LINEAR), and which a PowerVR phone rendered as
        /// coloured speckle where a desktop NVIDIA driver had looked fine. Mipmaps on a 701x561 NPOT
        /// texture are legal in GLES 3.0 and evidently not equally well travelled.</summary>
        public int MinFilter = 0x2601;         // GL_LINEAR
        public int MagFilter = 0x2601;         // GL_LINEAR
        public int WrapS = 0x2901;             // GL_REPEAT
        public int WrapT = 0x2901;             // GL_REPEAT
        public required bool HasUv;
        public required string Name;
    }

    public required List<Primitive> Primitives;
    /// <summary>Encoded image bytes, deduplicated by glTF image index. Encoded, not decoded: a
    /// renderer that owns a codec has taken a dependency it never needed, and every host already
    /// has one.</summary>
    public required List<byte[]> Images;
    public required float[] Min;
    public required float[] Max;

    public int TriangleCount { get { var n = 0; foreach (var p in Primitives) n += p.Indices.Length / 3; return n; } }
    public int VertexCount { get { var n = 0; foreach (var p in Primitives) n += p.Vertices.Length / 8; return n; } }

    private const uint GLB_MAGIC = 0x46546C67;   // "glTF"

    public static Gltf Load(byte[] glb)
    {
        if (glb.Length < 20) throw new InvalidDataException("not a glb: too short");
        var magic = BitConverter.ToUInt32(glb, 0);
        if (magic != GLB_MAGIC) throw new InvalidDataException($"not a glb: magic 0x{magic:X8}");
        var version = BitConverter.ToUInt32(glb, 4);
        if (version != 2) throw new InvalidDataException($"glb version {version}, expected 2");

        // Chunks: JSON first, then BIN. Walked rather than assumed, because the spec allows padding
        // between chunks and a file that pads is still valid.
        int off = 12, jsonStart = 0, jsonLen = 0, binStart = 0;
        while (off + 8 <= glb.Length)
        {
            var clen = (int)BitConverter.ToUInt32(glb, off);
            var ctype = BitConverter.ToUInt32(glb, off + 4);
            var start = off + 8;
            if (ctype == 0x4E4F534A) { jsonStart = start; jsonLen = clen; }        // "JSON"
            else if (ctype == 0x004E4942) binStart = start;                        // "BIN\0"
            off = start + clen;
        }
        if (jsonLen == 0) throw new InvalidDataException("glb has no JSON chunk");

        using var doc = JsonDocument.Parse(glb.AsMemory(jsonStart, jsonLen));
        var root = doc.RootElement;

        // Anything in extensionsRequired changes how the bytes must be read — Draco geometry and
        // KTX2 textures both do. Refusing by name beats rendering nonsense.
        if (root.TryGetProperty("extensionsRequired", out var req))
            throw new InvalidDataException($"required extensions not supported: {req}");

        var accessors = root.GetProperty("accessors");
        var views = root.GetProperty("bufferViews");
        var meshes = root.TryGetProperty("meshes", out var me) ? me : default;
        var nodes = root.TryGetProperty("nodes", out var n) ? n : default;
        if (meshes.ValueKind != JsonValueKind.Array) throw new InvalidDataException("glb has no meshes");

        var prims = new List<Primitive>();
        var images = new List<byte[]>();
        var imageIndexMap = new Dictionary<int, int>();     // glTF image index -> our Images index
        float[] min = { float.MaxValue, float.MaxValue, float.MaxValue };
        float[] max = { float.MinValue, float.MinValue, float.MinValue };

        void Emit(JsonElement mesh, float[] world)
        {
            var meshName = mesh.TryGetProperty("name", out var mn) ? mn.GetString() ?? "" : "";
            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                var mode = prim.TryGetProperty("mode", out var m) ? m.GetInt32() : 4;
                if (mode != 4) continue;                    // only TRIANGLES; skip, do not pretend

                var attrs = prim.GetProperty("attributes");
                if (!attrs.TryGetProperty("POSITION", out var posA)) continue;
                var pos = ReadVec3(glb, binStart, accessors[posA.GetInt32()], views);
                var nrm = attrs.TryGetProperty("NORMAL", out var na)
                    ? ReadVec3(glb, binStart, accessors[na.GetInt32()], views) : null;
                var uv = attrs.TryGetProperty("TEXCOORD_0", out var ua)
                    ? ReadVec2(glb, binStart, accessors[ua.GetInt32()], views) : null;

                var count = pos.Length / 3;
                var verts = new float[count * 8];
                for (var i = 0; i < count; i++)
                {
                    var (x, y, z) = TransformPoint(world, pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
                    verts[i * 8 + 0] = x; verts[i * 8 + 1] = y; verts[i * 8 + 2] = z;
                    if (x < min[0]) min[0] = x; if (x > max[0]) max[0] = x;
                    if (y < min[1]) min[1] = y; if (y > max[1]) max[1] = y;
                    if (z < min[2]) min[2] = z; if (z > max[2]) max[2] = z;

                    if (nrm is not null)
                    {
                        var (nx, ny, nz) = TransformDir(world, nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);
                        var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (len > 1e-6f) { nx /= len; ny /= len; nz /= len; }
                        verts[i * 8 + 3] = nx; verts[i * 8 + 4] = ny; verts[i * 8 + 5] = nz;
                    }
                    else verts[i * 8 + 4] = 1f;
                    if (uv is not null) { verts[i * 8 + 6] = uv[i * 2]; verts[i * 8 + 7] = uv[i * 2 + 1]; }
                }

                var indices = prim.TryGetProperty("indices", out var ia)
                    ? ReadIndices(glb, binStart, accessors[ia.GetInt32()], views)
                    : Sequential(count);

                float[] colour = { 1f, 1f, 1f, 1f };
                float metallic = 1f, roughness = 1f;      // the spec's defaults, not zero
                var imgIdx = -1;
                // GL_LINEAR / GL_REPEAT: the spec leaves filtering to the implementation when a
                // sampler omits it, and plain LINEAR is the choice that needs no mipmap chain.
                int minFilter = 0x2601, magFilter = 0x2601, wrapS = 0x2901, wrapT = 0x2901;
                if (prim.TryGetProperty("material", out var mi) && root.TryGetProperty("materials", out var mats))
                {
                    var mat = mats[mi.GetInt32()];
                    if (mat.TryGetProperty("pbrMetallicRoughness", out var pbr))
                    {
                        if (pbr.TryGetProperty("baseColorFactor", out var bc))
                        {
                            var k = 0;
                            foreach (var c in bc.EnumerateArray()) if (k < 4) colour[k++] = c.GetSingle();
                        }
                        if (pbr.TryGetProperty("metallicFactor", out var mf)) metallic = mf.GetSingle();
                        if (pbr.TryGetProperty("roughnessFactor", out var rf)) roughness = rf.GetSingle();
                        if (pbr.TryGetProperty("baseColorTexture", out var bct)
                            && root.TryGetProperty("textures", out var texs)
                            && root.TryGetProperty("images", out var imgs))
                        {
                            var texNode = texs[bct.GetProperty("index").GetInt32()];
                            var srcIdx = texNode.GetProperty("source").GetInt32();

                            // The sampler the ASSET asked for. Absent members keep the spec's
                            // defaults (repeat, and "let the implementation choose" filtering,
                            // which we read as plain LINEAR - the conservative choice, and the one
                            // that needs no mipmap chain).
                            if (texNode.TryGetProperty("sampler", out var sIdx)
                                && root.TryGetProperty("samplers", out var samplers))
                            {
                                var smp = samplers[sIdx.GetInt32()];
                                if (smp.TryGetProperty("minFilter", out var smn)) minFilter = smn.GetInt32();
                                if (smp.TryGetProperty("magFilter", out var smg)) magFilter = smg.GetInt32();
                                if (smp.TryGetProperty("wrapS", out var ws)) wrapS = ws.GetInt32();
                                if (smp.TryGetProperty("wrapT", out var wt)) wrapT = wt.GetInt32();
                            }
                            if (!imageIndexMap.TryGetValue(srcIdx, out imgIdx))
                            {
                                var img = imgs[srcIdx];
                                if (img.TryGetProperty("bufferView", out var ibv))
                                {
                                    var view = views[ibv.GetInt32()];
                                    var o = binStart + (view.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0);
                                    var len = view.GetProperty("byteLength").GetInt32();
                                    var bytes = new byte[len];
                                    Array.Copy(glb, o, bytes, 0, len);
                                    imgIdx = images.Count;
                                    images.Add(bytes);
                                    imageIndexMap[srcIdx] = imgIdx;
                                }
                                else imgIdx = -1;          // a uri image would be a second fetch; unbuilt
                            }
                        }
                    }
                }

                prims.Add(new Primitive
                {
                    Vertices = verts, Indices = indices, BaseColor = colour,
                    Metallic = metallic, Roughness = roughness,
                    ImageIndex = imgIdx, HasUv = uv is not null,
                    MinFilter = minFilter, MagFilter = magFilter, WrapS = wrapS, WrapT = wrapT,
                    Name = meshName,
                });
            }
        }

        void Walk(int idx, float[] parent)
        {
            var node = nodes[idx];
            var acc = Multiply(parent, Local(node));
            if (node.TryGetProperty("mesh", out var m)) Emit(meshes[m.GetInt32()], acc);
            if (node.TryGetProperty("children", out var kids))
                foreach (var k in kids.EnumerateArray()) Walk(k.GetInt32(), acc);
        }

        if (nodes.ValueKind == JsonValueKind.Array)
        {
            var sceneIdx = root.TryGetProperty("scene", out var si) ? si.GetInt32() : 0;
            if (root.TryGetProperty("scenes", out var sc) && sc.GetArrayLength() > sceneIdx
                && sc[sceneIdx].TryGetProperty("nodes", out var roots))
                foreach (var r in roots.EnumerateArray()) Walk(r.GetInt32(), Identity());
            else
                for (var i = 0; i < nodes.GetArrayLength(); i++) Walk(i, Identity());
        }
        else
            foreach (var mesh in meshes.EnumerateArray()) Emit(mesh, Identity());

        if (prims.Count == 0) throw new InvalidDataException("no TRIANGLES primitives found");
        return new Gltf { Primitives = prims, Images = images, Min = min, Max = max };
    }

    // ---- node transforms ---------------------------------------------------------------------

    /// <summary>A node's own transform: either a matrix, or TRS. The spec allows both and an exporter
    /// picks either — assuming one is how a loader breaks on the second model it is given.</summary>
    private static float[] Local(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var m))
        {
            var r = new float[16]; var i = 0;
            foreach (var v in m.EnumerateArray()) r[i++] = v.GetSingle();
            return r;
        }
        var t = Identity();
        if (node.TryGetProperty("translation", out var tr))
        {
            var i = 0; foreach (var v in tr.EnumerateArray()) t[12 + i++] = v.GetSingle();
        }
        if (node.TryGetProperty("rotation", out var rq))
        {
            Span<float> q = stackalloc float[4]; var i = 0;
            foreach (var v in rq.EnumerateArray()) q[i++] = v.GetSingle();
            float x = q[0], y = q[1], z = q[2], w = q[3];
            var rot = Identity();
            rot[0] = 1 - 2 * (y * y + z * z); rot[1] = 2 * (x * y + z * w); rot[2] = 2 * (x * z - y * w);
            rot[4] = 2 * (x * y - z * w); rot[5] = 1 - 2 * (x * x + z * z); rot[6] = 2 * (y * z + x * w);
            rot[8] = 2 * (x * z + y * w); rot[9] = 2 * (y * z - x * w); rot[10] = 1 - 2 * (x * x + y * y);
            t = Multiply(t, rot);
        }
        if (node.TryGetProperty("scale", out var sc))
        {
            var s = Identity(); var i = 0;
            foreach (var v in sc.EnumerateArray()) { s[i * 5] = v.GetSingle(); i++; }
            t = Multiply(t, s);
        }
        return t;
    }

    // ---- accessors ------------------------------------------------------------------------------

    /// <summary>Read a VEC3 float accessor, honouring byteStride. The stride is the part worth
    /// getting right: files interleave POSITION and NORMAL in ONE buffer view, so a reader that
    /// assumed tight packing would return normals as positions and draw a mess.</summary>
    private static float[] ReadVec3(byte[] glb, int bin, JsonElement acc, JsonElement views)
    {
        if (acc.GetProperty("type").GetString() != "VEC3") throw new InvalidDataException("expected VEC3");
        if (acc.GetProperty("componentType").GetInt32() != 5126) throw new InvalidDataException("expected float VEC3");
        var count = acc.GetProperty("count").GetInt32();
        var view = views[acc.GetProperty("bufferView").GetInt32()];
        var viewOff = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0;
        var accOff = acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
        var stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 12;
        var start = bin + viewOff + accOff;
        var r = new float[count * 3];
        for (var i = 0; i < count; i++)
        {
            var o = start + i * stride;
            r[i * 3 + 0] = BitConverter.ToSingle(glb, o);
            r[i * 3 + 1] = BitConverter.ToSingle(glb, o + 4);
            r[i * 3 + 2] = BitConverter.ToSingle(glb, o + 8);
        }
        return r;
    }

    private static float[] ReadVec2(byte[] glb, int bin, JsonElement acc, JsonElement views)
    {
        if (acc.GetProperty("type").GetString() != "VEC2") throw new InvalidDataException("expected VEC2");
        if (acc.GetProperty("componentType").GetInt32() != 5126) throw new InvalidDataException("expected float VEC2");
        var count = acc.GetProperty("count").GetInt32();
        var view = views[acc.GetProperty("bufferView").GetInt32()];
        var viewOff = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0;
        var accOff = acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
        var stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 8;
        var start = bin + viewOff + accOff;
        var r = new float[count * 2];
        for (var i = 0; i < count; i++)
        {
            var o = start + i * stride;
            r[i * 2 + 0] = BitConverter.ToSingle(glb, o);
            r[i * 2 + 1] = BitConverter.ToSingle(glb, o + 4);
        }
        return r;
    }

    private static uint[] ReadIndices(byte[] glb, int bin, JsonElement acc, JsonElement views)
    {
        var count = acc.GetProperty("count").GetInt32();
        var type = acc.GetProperty("componentType").GetInt32();
        var view = views[acc.GetProperty("bufferView").GetInt32()];
        var viewOff = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0;
        var accOff = acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
        var start = bin + viewOff + accOff;
        var r = new uint[count];
        for (var i = 0; i < count; i++)
            r[i] = type switch
            {
                5121 => glb[start + i],                             // UNSIGNED_BYTE
                5123 => BitConverter.ToUInt16(glb, start + i * 2),  // UNSIGNED_SHORT
                5125 => BitConverter.ToUInt32(glb, start + i * 4),  // UNSIGNED_INT
                _ => throw new InvalidDataException($"index componentType {type}"),
            };
        return r;
    }

    private static uint[] Sequential(int n)
    {
        var r = new uint[n];
        for (var i = 0; i < n; i++) r[i] = (uint)i;
        return r;
    }

    // ---- 4x4, column-major (glTF's convention and GL's, so nothing is transposed) ----------------

    public static float[] Identity() => new float[16] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };

    public static float[] Multiply(float[] a, float[] b)
    {
        var r = new float[16];
        for (var c = 0; c < 4; c++)
            for (var row = 0; row < 4; row++)
            {
                float s = 0;
                for (var k = 0; k < 4; k++) s += a[k * 4 + row] * b[c * 4 + k];
                r[c * 4 + row] = s;
            }
        return r;
    }

    private static (float, float, float) TransformPoint(float[] m, float x, float y, float z) =>
        (m[0] * x + m[4] * y + m[8] * z + m[12],
         m[1] * x + m[5] * y + m[9] * z + m[13],
         m[2] * x + m[6] * y + m[10] * z + m[14]);

    /// <summary>Normals transform by the inverse-transpose, not the matrix. With rotation and uniform
    /// scale — which is what these files carry — the matrix itself suffices and the shader
    /// renormalises. Named so the shortcut is visible rather than silently assumed; a non-uniformly
    /// scaled node would need the real thing.</summary>
    private static (float, float, float) TransformDir(float[] m, float x, float y, float z) =>
        (m[0] * x + m[4] * y + m[8] * z,
         m[1] * x + m[5] * y + m[9] * z,
         m[2] * x + m[6] * y + m[10] * z);
}
