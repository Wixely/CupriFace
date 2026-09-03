using System.Text.Json;

// A GLB reader small enough to read in one sitting, and deliberately not a library. It exists to
// answer one question — does a real exported model survive the whole path from bytes to pixels
// under NativeAOT-LLVM — so it handles what teapot.glb actually contains and refuses the rest
// loudly rather than half-supporting it.
//
// JsonDocument, not JsonSerializer<T>: the DOM reader uses no reflection, so it is trim-clean by
// construction under TrimMode=full. A shipping loader would want source-generated contexts for
// speed; both are AOT-safe, which is the property that matters here and the one Stride cannot meet.
internal sealed class Gltf
{
    public required float[] Vertices;      // interleaved px,py,pz,nx,ny,nz,u,v, already world-space
    public required uint[] Indices;
    public required float[] BaseColor;     // rgba
    public required float[] Min;           // world-space bounds, for framing the camera
    public required float[] Max;
    public byte[]? BaseColorImage;         // the ENCODED bytes; decoding is the host's job, not ours
    public bool HasUv;

    private const uint GLB_MAGIC = 0x46546C67;   // "glTF"

    public static Gltf Load(byte[] glb)
    {
        if (glb.Length < 20) throw new InvalidDataException("not a glb: too short");
        var magic = BitConverter.ToUInt32(glb, 0);
        if (magic != GLB_MAGIC) throw new InvalidDataException($"not a glb: magic 0x{magic:X8}");
        var version = BitConverter.ToUInt32(glb, 4);
        if (version != 2) throw new InvalidDataException($"glb version {version}, expected 2");

        // Chunks: JSON first, then BIN. Walking them rather than assuming offsets, because the
        // spec allows padding between chunks and a file that pads is still valid.
        int off = 12, jsonStart = 0, jsonLen = 0, binStart = 0, binLen = 0;
        while (off + 8 <= glb.Length)
        {
            var clen = (int)BitConverter.ToUInt32(glb, off);
            var ctype = BitConverter.ToUInt32(glb, off + 4);
            var start = off + 8;
            if (ctype == 0x4E4F534A) { jsonStart = start; jsonLen = clen; }        // "JSON"
            else if (ctype == 0x004E4942) { binStart = start; binLen = clen; }     // "BIN\0"
            off = start + clen;
        }
        if (jsonLen == 0) throw new InvalidDataException("glb has no JSON chunk");

        using var doc = JsonDocument.Parse(glb.AsMemory(jsonStart, jsonLen));
        var root = doc.RootElement;

        // Anything in extensionsRequired would change how the bytes must be read — Draco geometry
        // and KTX2 textures both do. Refusing by name beats rendering nonsense.
        if (root.TryGetProperty("extensionsRequired", out var req))
            throw new InvalidDataException($"required extensions not supported: {req}");

        var accessors = root.GetProperty("accessors");
        var views = root.GetProperty("bufferViews");
        var meshes = root.GetProperty("meshes");
        var nodes = root.TryGetProperty("nodes", out var n) ? n : default;

        // --- the node transform for the first mesh, accumulated down the scene graph -------------
        // glTF stores matrices COLUMN-major, which is also what GL wants, so no transpose anywhere.
        var world = Identity();
        var meshIndex = -1;
        if (nodes.ValueKind == JsonValueKind.Array)
        {
            var scene = root.TryGetProperty("scenes", out var sc) && sc.GetArrayLength() > 0
                ? sc[root.TryGetProperty("scene", out var si) ? si.GetInt32() : 0]
                : default;
            if (scene.ValueKind == JsonValueKind.Object && scene.TryGetProperty("nodes", out var roots))
                foreach (var r in roots.EnumerateArray())
                    if (Walk(nodes, r.GetInt32(), Identity(), ref world, ref meshIndex)) break;
        }
        if (meshIndex < 0) meshIndex = 0;

        var prim = meshes[meshIndex].GetProperty("primitives")[0];
        var mode = prim.TryGetProperty("mode", out var m) ? m.GetInt32() : 4;
        if (mode != 4) throw new InvalidDataException($"primitive mode {mode}; only TRIANGLES (4) handled");

        var attrs = prim.GetProperty("attributes");
        var posAcc = attrs.GetProperty("POSITION").GetInt32();
        var nrmAcc = attrs.TryGetProperty("NORMAL", out var na) ? na.GetInt32() : -1;

        var uvAcc = attrs.TryGetProperty("TEXCOORD_0", out var ua) ? ua.GetInt32() : -1;

        var pos = ReadVec3(glb, binStart, accessors[posAcc], views);
        var nrm = nrmAcc >= 0 ? ReadVec3(glb, binStart, accessors[nrmAcc], views) : null;
        var uv = uvAcc >= 0 ? ReadVec2(glb, binStart, accessors[uvAcc], views) : null;
        var count = pos.Length / 3;

        // Bake the node transform in on load. A per-frame model matrix would be the real design;
        // this keeps the render loop to one draw call and the proof to one thing at a time.
        var normalMat = NormalMatrix(world);
        var verts = new float[count * 8];
        float[] min = { float.MaxValue, float.MaxValue, float.MaxValue };
        float[] max = { float.MinValue, float.MinValue, float.MinValue };
        for (var i = 0; i < count; i++)
        {
            var (x, y, z) = TransformPoint(world, pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
            verts[i * 8 + 0] = x; verts[i * 8 + 1] = y; verts[i * 8 + 2] = z;
            if (x < min[0]) min[0] = x; if (x > max[0]) max[0] = x;
            if (y < min[1]) min[1] = y; if (y > max[1]) max[1] = y;
            if (z < min[2]) min[2] = z; if (z > max[2]) max[2] = z;

            if (nrm is not null)
            {
                var (nx, ny, nz) = TransformDir(normalMat, nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);
                var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-6f) { nx /= len; ny /= len; nz /= len; }
                verts[i * 8 + 3] = nx; verts[i * 8 + 4] = ny; verts[i * 8 + 5] = nz;
            }
            else verts[i * 8 + 4] = 1f;      // no normals in the file: face them up rather than zero

            if (uv is not null) { verts[i * 8 + 6] = uv[i * 2]; verts[i * 8 + 7] = uv[i * 2 + 1]; }
        }

        var indices = prim.TryGetProperty("indices", out var ia)
            ? ReadIndices(glb, binStart, accessors[ia.GetInt32()], views)
            : Sequential(count);

        float[] colour = { 1f, 1f, 1f, 1f };
        if (prim.TryGetProperty("material", out var mi) && root.TryGetProperty("materials", out var mats))
        {
            var mat = mats[mi.GetInt32()];
            if (mat.TryGetProperty("pbrMetallicRoughness", out var pbr)
                && pbr.TryGetProperty("baseColorFactor", out var bc))
            {
                var k = 0;
                foreach (var c in bc.EnumerateArray()) if (k < 4) colour[k++] = c.GetSingle();
            }
        }

        // The base-colour texture, as ENCODED bytes. Deliberately not decoded here: a renderer that
        // owns a JPEG decoder has taken on a codec dependency it never needed, and every host this
        // could run on already has one.
        byte[]? image = null;
        if (prim.TryGetProperty("material", out var mi2) && root.TryGetProperty("materials", out var mats2))
        {
            var mat = mats2[mi2.GetInt32()];
            if (mat.TryGetProperty("pbrMetallicRoughness", out var pbr2)
                && pbr2.TryGetProperty("baseColorTexture", out var bct)
                && root.TryGetProperty("textures", out var texs)
                && root.TryGetProperty("images", out var imgs))
            {
                var tex = texs[bct.GetProperty("index").GetInt32()];
                var img = imgs[tex.GetProperty("source").GetInt32()];
                if (img.TryGetProperty("bufferView", out var ibv))
                {
                    var view = views[ibv.GetInt32()];
                    var o = binStart + (view.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0);
                    var len = view.GetProperty("byteLength").GetInt32();
                    image = new byte[len];
                    Array.Copy(glb, o, image, 0, len);
                }
                // A uri image would be a second fetch; this file embeds, so that path is unbuilt
                // rather than half-built.
                else if (img.TryGetProperty("uri", out _))
                    throw new InvalidDataException("external image uri not supported by this probe");
            }
        }

        return new Gltf
        {
            Vertices = verts, Indices = indices, BaseColor = colour,
            Min = min, Max = max, BaseColorImage = image, HasUv = uv is not null,
        };
    }

    // ---- scene graph ---------------------------------------------------------------------------

    private static bool Walk(JsonElement nodes, int idx, float[] parent, ref float[] world, ref int mesh)
    {
        var node = nodes[idx];
        var local = Local(node);
        var acc = Multiply(parent, local);
        if (node.TryGetProperty("mesh", out var m)) { world = acc; mesh = m.GetInt32(); return true; }
        if (node.TryGetProperty("children", out var kids))
            foreach (var k in kids.EnumerateArray())
                if (Walk(nodes, k.GetInt32(), acc, ref world, ref mesh)) return true;
        return false;
    }

    /// <summary>A node's own transform: either a matrix, or TRS. The spec allows both and an
    /// exporter picks either — this file uses matrix, but assuming that is how a loader breaks on
    /// the second model it is given.</summary>
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
    /// getting right: this file interleaves POSITION and NORMAL in ONE buffer view at stride 32, so
    /// a reader that assumed tight packing would return normals as positions and draw a mess.</summary>
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

    /// <summary>VEC2 floats, same stride discipline as VEC3. This file packs UV at offset 24 inside
    /// the same stride-32 buffer view as position and normal.</summary>
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

    private static (float, float, float) TransformDir(float[] m, float x, float y, float z) =>
        (m[0] * x + m[4] * y + m[8] * z,
         m[1] * x + m[5] * y + m[9] * z,
         m[2] * x + m[6] * y + m[10] * z);

    /// <summary>Normals transform by the inverse-transpose, not the matrix. With uniform scale and
    /// rotation only — which is all this file has — the rotation part suffices, and the shader
    /// renormalises anyway. Named so the shortcut is visible rather than silently assumed.</summary>
    private static float[] NormalMatrix(float[] m) => m;
}
