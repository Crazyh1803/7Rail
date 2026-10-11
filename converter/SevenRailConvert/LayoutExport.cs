using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace SevenRail.Convert;

/// Reads an EverRail blueprint's static mesh parts and named spots with their transforms (parent chain composed),
/// converts them to Unity space (metres, Y up) and writes layouts/<id>.json. The meshes go to meshes/native/ in
/// EverRail's own frame, so the layout's transforms place them exactly as EverRail does.
static class LayoutExport
{
    public sealed record Part(string Mesh, float[] Matrix);   // Unity 3x4, columns right, up, forward, position
    public sealed record Spot(string Name, float[] Matrix, float[]? Extent);   // Extent: half size in Unity axes, metres

    public static void Run(DefaultFileProvider provider, LayoutsRow row, string outDir, Action<string> log)
    {
        var spotNames = row.Spots == "none" ? Array.Empty<string>() : row.Spots.Split(';');
        // Several blueprints may share one frame (a child after its parent): their parts are merged.
        var exports = row.Blueprint.Split(';').SelectMany(bp => provider.LoadPackage(bp + ".uasset").GetExports()).ToList();
        var byName = new Dictionary<string, UObject>();
        foreach (var e in exports) byName[e.Name] = e;
        // The blueprint's construction tree (SCS nodes): child component template -> parent component template.
        var scsParent = new Dictionary<string, string>();
        foreach (var node in exports.Where(x => x.ExportType == "SCS_Node"))
        {
            if (!node.TryGetValue(out FPackageIndex tmpl, "ComponentTemplate") || tmpl.IsNull) continue;
            foreach (var child in node.GetOrDefault("ChildNodes", Array.Empty<FPackageIndex>()))
                if (child.TryLoad<UObject>(out var childNode) && childNode.TryGetValue(out FPackageIndex childTmpl, "ComponentTemplate"))
                    scsParent[childTmpl.Name] = tmpl.Name;
        }
        var parts = new List<Part>();
        var spots = new List<Spot>();
        var meshNames = new HashSet<string>();
        foreach (var e in exports)
        {
            if (e is not UObject comp) continue;
            var isSpot = spotNames.Contains(comp.Name);
            if (!isSpot && !comp.Name.EndsWith("_GEN_VARIABLE") && comp.ExportType is not ("StaticMeshComponent" or "SceneComponent")) continue;
            // EverRail's hidden-by-default parts (damage debris and the like) stay out.
            var shown = comp.TryGetValue(out FPackageIndex sm, "StaticMesh") && row.Show.Split(';').Contains(sm.Name);
            if (!isSpot && !shown && (!comp.GetOrDefault("bVisible", true) || comp.GetOrDefault("bHiddenInGame", false))) continue;
            var world = World(comp, byName, scsParent);
            if (comp.TryGetValue(out FPackageIndex meshRef, "StaticMesh") && meshRef.TryLoad<UStaticMesh>(out var mesh))
            {
                var name = mesh.Name;
                if (meshNames.Add(name)) MeshExport.RunNative(mesh, Path.Combine(outDir, "meshes", "native", name + ".7rm"), 60000, log);
                if (comp is UInstancedStaticMeshComponent ism && ism.GetInstances() is { Length: > 0 } instances)
                    foreach (var inst in instances) parts.Add(new Part(name, ToUnity(inst.TransformData.ToMatrixWithScale() * world)));
                else
                    parts.Add(new Part(name, ToUnity(world)));
            }
            if (isSpot)
            {
                float[]? extent = null;
                if (comp.TryGetValue(out FVector box, "BoxExtent")) extent = new[] { box.Y * 0.01f, box.Z * 0.01f, box.X * 0.01f };
                spots.Add(new Spot(comp.Name.Replace("_GEN_VARIABLE", ""), ToUnity(world), extent));
            }
        }
        var missing = spotNames.Select(s => s.Replace("_GEN_VARIABLE", "")).Except(spots.Select(s => s.Name)).ToList();
        if (missing.Count > 0) throw new InvalidDataException($"{row.Id}: spots not found in the blueprint: {string.Join(", ", missing)}");
        var file = Path.Combine(outDir, "layouts", row.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(new { parts, spots }, new JsonSerializerOptions { WriteIndented = true }));
        log($"layout {row.Id}: {parts.Count} parts ({string.Join(", ", meshNames)}), spots {string.Join(", ", spots.Select(s => s.Name))}");
    }

    /// UE transform of a component in the blueprint's frame: its relative transform under each attach parent.
    static FMatrix World(UObject comp, Dictionary<string, UObject> byName, Dictionary<string, string> scsParent)
    {
        var m = Local(comp);
        var cur = comp;
        for (int guard = 0; guard < 16; guard++)
        {
            string parentName;
            if (scsParent.TryGetValue(cur.Name, out var p)) parentName = p;
            else if (cur.TryGetValue(out FPackageIndex parentRef, "AttachParent") && !parentRef.IsNull) parentName = parentRef.Name;
            else break;
            if (!byName.TryGetValue(parentName, out var parent)) break;
            m = m * Local(parent);
            cur = parent;
        }
        return m;
    }

    static FMatrix Local(UObject c)
    {
        var loc = c.GetOrDefault("RelativeLocation", FVector.ZeroVector);
        var rot = c.GetOrDefault("RelativeRotation", FRotator.ZeroRotator);
        var scl = c.GetOrDefault("RelativeScale3D", FVector.OneVector);
        return new FTransform(rot.Quaternion(), loc, scl).ToMatrixWithScale();
    }

    /// Unreal (cm; X fwd, Y right, Z up) to Unity (m; the same cyclic swap the meshes use: Unity = (UE.Y, UE.Z, UE.X)).
    static float[] ToUnity(FMatrix m)
    {
        // Row vectors: M[0] = local X axis, M[1] = local Y, M[2] = local Z, M[3] = origin (UE row-vector convention).
        (float, float, float) P(float x, float y, float z, float k) => (y * k, z * k, x * k);
        var ax = P(m.M00, m.M01, m.M02, 1); var ay = P(m.M10, m.M11, m.M12, 1); var az = P(m.M20, m.M21, m.M22, 1);
        var o = P(m.M30, m.M31, m.M32, 0.01f);
        // Mesh vertex UE (x,y,z) sits at Unity (y,z,x): Unity right = UE Y axis, up = UE Z axis, forward = UE X axis.
        return new[] { ay.Item1, ay.Item2, ay.Item3, az.Item1, az.Item2, az.Item3, ax.Item1, ax.Item2, ax.Item3, o.Item1, o.Item2, o.Item3 };
    }
}
