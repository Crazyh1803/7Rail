using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Textures;

static class Decode
{
    public static void Run(DefaultFileProvider provider, string kind, string path, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var name = Path.GetFileName(path);
        var exports = provider.LoadPackage(path + ".uasset").GetExports().ToList();
        switch (kind)
        {
            case "mesh":
            {
                var sm = exports.OfType<UStaticMesh>().First();
                var lod = sm.RenderData!.LODs[0];
                var pos = lod.PositionVertexBuffer!.Verts;
                var vb = lod.VertexBuffer!;
                var bounds = sm.RenderData.Bounds;
                Console.WriteLine($"mesh {name}: verts={pos.Length} tris={lod.IndexBuffer!.Length / 3} sections={lod.Sections!.Length} uvs={vb.NumTexCoords} " +
                                  $"bounds origin={bounds!.Origin} extent={bounds.BoxExtent} materials={sm.StaticMaterials?.Length}");
                foreach (var m in sm.StaticMaterials ?? []) Console.WriteLine($"   material {m.MaterialSlotName} -> {m.MaterialInterface?.ResolvedObject?.GetPathName()}");
                break;
            }
            case "texture":
            {
                var tex = exports.OfType<UTexture2D>().First();
                var c = tex.Decode(1024);
                Console.WriteLine($"texture {name}: {tex.PlatformData.SizeX}x{tex.PlatformData.SizeY} fmt={tex.Format} decoded={(c != null ? $"{c.Width}x{c.Height} {c.PixelFormat}" : "null")}");
                break;
            }
            case "sound":
            {
                var sw = exports.OfType<USoundWave>().First();
                sw.Decode(true, out var fmt, out var data);
                Console.WriteLine($"sound {name}: format={fmt} bytes={data?.Length ?? 0} head={(data is { Length: > 4 } ? System.Text.Encoding.ASCII.GetString(data, 0, 4) : "")}");
                if (data != null) File.WriteAllBytes(Path.Combine(outDir, name + "." + fmt.ToLowerInvariant()), data);
                break;
            }
        }
    }
}
