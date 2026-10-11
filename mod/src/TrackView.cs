using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SevenRail
{
    /// Draws EverRail's own rail module bent along each line, in batches near the player.
    public sealed class TrackView
    {
        const int ModulesPerBatch = 16;
        const float ShowWithin = 500f, HideBeyond = 700f;
        readonly RailNetwork net;
        readonly Transform root;
        readonly Dictionary<(string, int), GameObject> batches = new Dictionary<(string, int), GameObject>();
        readonly Mesh module; readonly Material[] mats;
        readonly float scale, minZ, lenZ;
        readonly float moduleLen;

        public TrackView(RailNetwork net, Transform root)
        {
            this.net = net; this.root = root;
            (module, mats) = EverRailContent.Mesh(EverrailAssetsRow.MeshRailModule);
            scale = (float)TrackRow.TrackRing.WorldScale;
            moduleLen = (float)TrackRow.TrackRing.ModuleLengthM;
            // Lay modules at the length of the rails themselves (material slot 0), not the whole model.
            var verts = module.vertices; float lo = float.MaxValue, hi = float.MinValue;
            foreach (var i in module.GetTriangles(0)) { lo = Mathf.Min(lo, verts[i].z); hi = Mathf.Max(hi, verts[i].z); }
            minZ = lo; lenZ = hi - lo;
            moduleLen = lenZ * scale;
            Log.Out($"[7Rail] rail module: rails {lenZ:0.00} m of {module.bounds.size.z:0.00} m; laid every {moduleLen:0.00} m");
        }

        public void Update(Vector3 player)
        {
            foreach (var line in net.Lines)
            {
                int count = Mathf.CeilToInt(line.Length / (moduleLen * ModulesPerBatch));
                for (int k = 0; k < count; k++)
                {
                    var mid = line.At((k + 0.5f) * moduleLen * ModulesPerBatch, out _);
                    var dist = Vector2.Distance(new Vector2(mid.x, mid.z), new Vector2(player.x, player.z));
                    var key = (line.Id, k);
                    var has = batches.TryGetValue(key, out var go);
                    if (!has && dist < ShowWithin) batches[key] = Build(line, k);
                    else if (has && dist > HideBeyond) { Object.Destroy(go.GetComponent<MeshFilter>().sharedMesh); Object.Destroy(go); batches.Remove(key); }
                }
            }
        }

        GameObject Build(RailLine line, int batch)
        {
            var src = module.vertices; var srcN = module.normals; var srcUv = module.uv;
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
            var subs = new List<int>[module.subMeshCount];
            for (int s = 0; s < subs.Length; s++) subs[s] = new List<int>();
            // EverRail's rail module carries a snow layer as its own material: only lay it in the snow biome.
            var mid = line.At((batch + 0.5f) * moduleLen * ModulesPerBatch, out _);
            var snowy = GameManager.Instance.World.GetBiomeInWorld(Mathf.FloorToInt(mid.x), Mathf.FloorToInt(mid.z))?.m_sBiomeName == "snow";
            for (int m = 0; m < ModulesPerBatch; m++)
            {
                float s0 = (batch * ModulesPerBatch + m) * moduleLen;
                if (!line.Loop && s0 >= line.Length) break;
                int baseV = verts.Count;
                for (int i = 0; i < src.Length; i++)
                {
                    var v = src[i];
                    float along = s0 + (v.z - minZ) / lenZ * moduleLen;
                    var p = line.At(along, out var fwd);
                    var right = Vector3.Cross(Vector3.up, fwd).normalized;
                    var up = Vector3.Cross(fwd, right);
                    verts.Add(p + right * (v.x * scale) + up * (v.y * scale));
                    var n = srcN[i];
                    norms.Add(right * n.x + up * n.y + fwd * n.z);
                    uvs.Add(srcUv[i]);
                }
                for (int s = 0; s < subs.Length; s++)
                {
                    if (!snowy && mats[s].name.IndexOf("Snow", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    foreach (var idx in module.GetTriangles(s)) subs[s].Add(baseV + idx);
                }
            }
            var mesh = new Mesh { name = $"7Rail_track_{line.Id}_{batch}", indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(verts); mesh.SetNormals(norms); mesh.SetUVs(0, uvs);
            mesh.subMeshCount = subs.Length;
            for (int s = 0; s < subs.Length; s++) mesh.SetTriangles(subs[s], s);
            mesh.RecalculateBounds();
            var go = new GameObject(mesh.name);
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;   // flat track: receives, does not cast
            return go;
        }

        public void Clear()
        {
            foreach (var go in batches.Values) if (go != null) { Object.Destroy(go.GetComponent<MeshFilter>().sharedMesh); Object.Destroy(go); }
            batches.Clear();
        }
    }
}


