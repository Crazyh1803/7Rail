using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;

namespace SevenRail.Convert;

/// Writes one EverRail static mesh as a .7rm file (see Format below) plus a PNG per material slot.
/// Coordinates leave in Unity space: metres, Y up, the car's long axis on +Z, at EverRail's full size
/// (the mod applies the track sheet's world_scale).
static class MeshExport
{
    // .7rm: "7RM2", int vertexCount, int submeshCount,
    //   vertexCount x (float3 position, float3 normal, float2 uv),
    //   submeshCount x (string slot, string texture png or "", float4 tint rgba, bool translucent, int indexCount, int[] indices)
    public static void Run(DefaultFileProvider provider, EverrailAssetsRow row, string outDir, Action<string> log)
    {
        var mesh = provider.LoadPackage(row.Path + ".uasset").GetExports().OfType<UStaticMesh>().FirstOrDefault()
                   ?? throw new InvalidDataException("no StaticMesh in package");
        Export(mesh, row.Id, Path.Combine(outDir, row.Out), (int)double.Parse(row.MaxTris), row.ForwardAxis, log);
    }

    /// A mesh in EverRail's own frame (only the axis swap to Unity), for layouts that place it themselves.
    public static void RunNative(UStaticMesh mesh, string meshPath, int maxTris, Action<string> log) =>
        Export(mesh, mesh.Name, meshPath, maxTris, "+X", log);

    static void Export(UStaticMesh mesh, string id, string meshPath, int maxTris, string forwardAxis, Action<string> log)
    {
        var lods = mesh.RenderData?.LODs ?? throw new InvalidDataException("no render data");
        var lod = lods.FirstOrDefault(l => l.IndexBuffer!.Length / 3 <= maxTris) ?? lods.Last();
        var lodIndex = Array.IndexOf(lods, lod);

        var src = Extract(lod, forwardAxis);
        var srcTris = src.Sections.Sum(s => s.Indices.Count / 3);
        var simplified = srcTris > maxTris ? Simplify.Cluster(src, maxTris) : src;

        // One shared texture folder for the whole cache; each mesh stores the path relative to its own folder.
        var cacheRoot = Path.GetDirectoryName(meshPath)!;
        while (Path.GetFileName(cacheRoot) is "meshes" or "native") cacheRoot = Path.GetDirectoryName(cacheRoot)!;
        var texDir = Path.Combine(cacheRoot, "textures");
        var texRel = Path.GetRelativePath(Path.GetDirectoryName(meshPath)!, texDir).Replace('\\', '/');
        Directory.CreateDirectory(texDir);
        var materials = mesh.StaticMaterials ?? [];
        for (int i = 0; i < simplified.Sections.Count; i++)
        {
            var sec = simplified.Sections[i];
            var slot = sec.MaterialIndex < materials.Length ? materials[sec.MaterialIndex] : null;
            sec.Slot = slot?.MaterialSlotName.Text ?? $"slot{sec.MaterialIndex}";
            if (slot?.MaterialInterface?.TryLoad<UMaterialInterface>(out var mat) == true)
                (sec.Texture, sec.Tint, sec.Translucent) = ExportMaterial(mat, texDir, log);
            if (sec.Texture != "") sec.Texture = texRel + "/" + sec.Texture;
        }
        Write(meshPath, simplified);
        log($"mesh {id}: LOD{lodIndex}/{lods.Length} {srcTris} tris -> {simplified.Sections.Sum(s => s.Indices.Count / 3)} tris, " +
            $"{simplified.Positions.Count} verts, {simplified.Sections.Count} slots, size {Size(simplified)}");
    }

    public sealed class Section
    {
        public int MaterialIndex;
        public List<int> Indices = new();
        public string Slot = "";
        public string Texture = "";
        public float[] Tint = { 1, 1, 1, 1 };
        public bool Translucent;
    }

    public sealed class MeshData
    {
        public List<(float x, float y, float z)> Positions = new();
        public List<(float x, float y, float z)> Normals = new();
        public List<(float u, float v)> Uvs = new();
        public List<Section> Sections = new();
    }

    /// Unreal (cm, X forward, Y right, Z up) -> Unity (m, Y up), then turn so the long axis is +Z.
    static MeshData Extract(FStaticMeshLODResources lod, string forwardAxis)
    {
        var d = new MeshData();
        var verts = lod.PositionVertexBuffer!.Verts;
        var uvItems = lod.VertexBuffer!.UV;
        for (int i = 0; i < verts.Length; i++)
        {
            var p = verts[i];
            var n = uvItems[i].Normal[2];
            var uv = uvItems[i].UV[0];
            d.Positions.Add(Axis(p.Y * 0.01f, p.Z * 0.01f, p.X * 0.01f, forwardAxis));
            d.Normals.Add(Axis(n.Y, n.Z, n.X, forwardAxis));
            d.Uvs.Add((uv.U, 1f - uv.V));
        }
        var ib = lod.IndexBuffer!;
        foreach (var s in lod.Sections!)
        {
            var sec = new Section { MaterialIndex = s.MaterialIndex };
            for (int t = 0; t < s.NumTriangles; t++)
            {
                // Unreal winds clockwise when seen from Unity's side after the axis swap; reverse each triangle.
                int b = s.FirstIndex + t * 3;
                sec.Indices.Add(ib[b]); sec.Indices.Add(ib[b + 2]); sec.Indices.Add(ib[b + 1]);
            }
            d.Sections.Add(sec);
        }
        return d;
    }

    // After the swap Unreal X (forward) is Unity Z and Unreal Y is Unity X.
    static (float, float, float) Axis(float x, float y, float z, string forward) => forward switch
    {
        "+X" or "native" => (x, y, z),
        "+Y" => (-z, y, x),
        _ => throw new InvalidDataException($"forward_axis '{forward}' not handled")
    };

    static readonly Regex Diffuse = new(CUE4Parse.UE4.Assets.Exports.Material.CMaterialParams2.RegexDiffuse, RegexOptions.IgnoreCase);
    static readonly Regex TintName = new("(?i)(base_?colou?r|albedo|tint|paint|diffuse).*");
    static readonly Dictionary<string, (string, float[], bool)> Done = new();

    static (string texture, float[] tint, bool translucent) ExportMaterial(UMaterialInterface mat, string texDir, Action<string> log)
    {
        var key = mat.GetPathName();
        if (Done.TryGetValue(key, out var cached)) return cached;
        var p = new CMaterialParams2();
        mat.GetParams(p, EMaterialDepth.AllLayersNoRef);
        UTexture? tex = null;
        foreach (var (name, obj) in p.Textures)
            if (Diffuse.IsMatch(name) && obj.TryLoad<UTexture>(out var t) && t is UTexture2D) { tex = t; break; }
        if (tex == null)
            foreach (var (_, obj) in p.Textures)
                if (obj.TryLoad<UTexture>(out var t) && t is UTexture2D && Regex.IsMatch(t.Name, "(_D|_BC|_Albedo|_BaseColor)(_PNG)?$", RegexOptions.IgnoreCase)) { tex = t; break; }
        var tint = new[] { 1f, 1f, 1f, 1f };
        foreach (var (name, c) in p.Colors)
            if (TintName.IsMatch(name)) { tint = new[] { c.R, c.G, c.B, c.A <= 0 ? 1f : c.A }; break; }

        var file = "";
        if (tex is UTexture2D t2 && t2.Decode(1024) is { } decoded)
        {
            file = t2.Name + ".png";
            var full = Path.Combine(texDir, file);
            if (!File.Exists(full))
            {
                using var bmp = decoded.ToSkBitmap();
                using var data = bmp.Encode(SKEncodedImageFormat.Png, 90);
                File.WriteAllBytes(full, data.ToArray());
            }

        }
        log($"   material {mat.Name}: texture={(file == "" ? "(none)" : file)} tint={string.Join(",", tint.Select(v => v.ToString("0.00")))} params=[{string.Join(", ", p.Textures.Keys)}] colors=[{string.Join(", ", p.Colors.Keys)}]");
        var translucent = p.BlendMode is not (EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked);
        return Done[key] = (file, tint, translucent);
    }

    static string Size(MeshData d)
    {
        float min = float.MaxValue, max = float.MinValue, minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var (x, y, z) in d.Positions) { min = Math.Min(min, z); max = Math.Max(max, z); minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        return $"{maxX - minX:0.0} x {maxY - minY:0.0} x {max - min:0.0} m (w x h x l)";
    }

    static void Write(string path, MeshData d)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("7RM2"u8.ToArray());
        w.Write(d.Positions.Count);
        w.Write(d.Sections.Count);
        for (int i = 0; i < d.Positions.Count; i++)
        {
            w.Write(d.Positions[i].x); w.Write(d.Positions[i].y); w.Write(d.Positions[i].z);
            w.Write(d.Normals[i].x); w.Write(d.Normals[i].y); w.Write(d.Normals[i].z);
            w.Write(d.Uvs[i].u); w.Write(d.Uvs[i].v);
        }
        foreach (var s in d.Sections)
        {
            w.Write(s.Slot); w.Write(s.Texture);
            foreach (var c in s.Tint) w.Write(c);
            w.Write(s.Translucent);
            w.Write(s.Indices.Count);
            foreach (var i in s.Indices) w.Write(i);
        }
    }
}
