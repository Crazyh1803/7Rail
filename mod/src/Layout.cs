using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SevenRail
{
    /// An EverRail blueprint as the converter wrote it: mesh parts and named spots, in Unity space at EverRail size.
    public sealed class Layout
    {
        public readonly List<(string mesh, Matrix4x4 m)> Parts = new List<(string, Matrix4x4)>();
        public readonly Dictionary<string, Matrix4x4> Spots = new Dictionary<string, Matrix4x4>();
        public readonly Dictionary<string, Vector3> Extents = new Dictionary<string, Vector3>();   // box spots: half size
        static readonly Dictionary<string, Layout> cache = new Dictionary<string, Layout>();

        public static Layout Get(LayoutsRow row)
        {
            if (cache.TryGetValue(row.Id, out var l)) return l;
            l = new Layout();
            var j = JObject.Parse(File.ReadAllText(Path.Combine(EverRailContent.CacheDir, "layouts", row.Id + ".json")));
            foreach (var p in j["parts"]) l.Parts.Add(((string)p["Mesh"], ToMatrix(p["Matrix"].ToObject<float[]>())));
            foreach (var s in j["spots"])
            {
                l.Spots[(string)s["Name"]] = ToMatrix(s["Matrix"].ToObject<float[]>());
                if (s["Extent"] is JArray e && e.Count == 3) l.Extents[(string)s["Name"]] = new Vector3((float)e[0], (float)e[1], (float)e[2]);
            }
            return cache[row.Id] = l;
        }

        static Matrix4x4 ToMatrix(float[] a)
        {
            var m = Matrix4x4.identity;
            m.SetColumn(0, new Vector4(a[0], a[1], a[2], 0));
            m.SetColumn(1, new Vector4(a[3], a[4], a[5], 0));
            m.SetColumn(2, new Vector4(a[6], a[7], a[8], 0));
            m.SetColumn(3, new Vector4(a[9], a[10], a[11], 1));
            return m;
        }

        public static Vector3 Pos(Matrix4x4 m) => m.GetColumn(3);

        /// Places a child with this matrix under parent (local space).
        public static GameObject Place(Transform parent, string name, Mesh mesh, Material[] mats, Matrix4x4 m)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Vector3 r = m.GetColumn(0), u = m.GetColumn(1), f = m.GetColumn(2);
            go.transform.localPosition = Pos(m);
            go.transform.localRotation = Quaternion.LookRotation(f.normalized, u.normalized);
            go.transform.localScale = new Vector3(r.magnitude, u.magnitude, f.magnitude);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            // Performance: small parts (props, decals, doors) do not cast shadows.
            if (mesh.bounds.size.magnitude * go.transform.lossyScale.x < 2.5f) mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        /// The bounds of one part's mesh in the layout's frame.
        public Bounds PartBounds(string mesh) => PartBounds(mesh, Parts.First(p => p.mesh == mesh).m);

        public Bounds PartBounds(string mesh, Matrix4x4 m)
        {
            var b = EverRailContent.NativeMesh(mesh).mesh.bounds;
            var outB = new Bounds(m.MultiplyPoint3x4(b.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                outB.Encapsulate(m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return outB;
        }

        /// Builds every part under parent, except the meshes listed in skip.
        public void Build(Transform parent, params string[] skip)
        {
            foreach (var (mesh, m) in Parts)
            {
                if (skip.Contains(mesh)) continue;
                var (msh, mats) = EverRailContent.NativeMesh(mesh);
                Place(parent, mesh, msh, mats, m);
            }
        }
    }
}
