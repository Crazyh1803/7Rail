namespace SevenRail.Convert;

/// Vertex-clustering simplifier: snaps vertices to a grid, keeping hard edges apart by normal direction,
/// and searches the grid size that lands just under the triangle budget.
static class Simplify
{
    public static MeshExport.MeshData Cluster(MeshExport.MeshData src, int maxTris)
    {
        float lo = 0.001f, hi = 2f;
        MeshExport.MeshData? best = null;
        for (int iter = 0; iter < 18; iter++)
        {
            var cell = (lo + hi) / 2;
            var m = Run(src, cell);
            var tris = m.Sections.Sum(s => s.Indices.Count / 3);
            if (tris <= maxTris) { best = m; hi = cell; } else lo = cell;
        }
        return best ?? Run(src, hi);
    }

    static MeshExport.MeshData Run(MeshExport.MeshData src, float cell)
    {
        var map = new Dictionary<(int, int, int, int), int>();
        var remap = new int[src.Positions.Count];
        var outM = new MeshExport.MeshData();
        var sums = new List<(double x, double y, double z, int n)>();
        for (int i = 0; i < src.Positions.Count; i++)
        {
            var (x, y, z) = src.Positions[i];
            var key = ((int)MathF.Floor(x / cell), (int)MathF.Floor(y / cell), (int)MathF.Floor(z / cell), NormalBucket(src.Normals[i]));
            if (!map.TryGetValue(key, out var idx))
            {
                idx = outM.Positions.Count;
                map[key] = idx;
                outM.Positions.Add(src.Positions[i]);
                outM.Normals.Add(src.Normals[i]);
                outM.Uvs.Add(src.Uvs[i]);
                sums.Add((0, 0, 0, 0));
            }
            var s = sums[idx];
            sums[idx] = (s.x + x, s.y + y, s.z + z, s.n + 1);
            remap[i] = idx;
        }
        for (int i = 0; i < outM.Positions.Count; i++)
        {
            var s = sums[i];
            outM.Positions[i] = ((float)(s.x / s.n), (float)(s.y / s.n), (float)(s.z / s.n));
        }
        foreach (var sec in src.Sections)
        {
            var ns = new MeshExport.Section { MaterialIndex = sec.MaterialIndex };
            var seen = new HashSet<(int, int, int)>();
            for (int t = 0; t < sec.Indices.Count; t += 3)
            {
                int a = remap[sec.Indices[t]], b = remap[sec.Indices[t + 1]], c = remap[sec.Indices[t + 2]];
                if (a == b || b == c || a == c) continue;
                var k = a < b ? (a < c ? (a, b, c) : (c, a, b)) : (b < c ? (b, c, a) : (c, a, b));
                if (!seen.Add(k)) continue;
                ns.Indices.Add(a); ns.Indices.Add(b); ns.Indices.Add(c);
            }
            outM.Sections.Add(ns);
        }
        return outM;
    }

    static int NormalBucket((float x, float y, float z) n)
    {
        float ax = MathF.Abs(n.x), ay = MathF.Abs(n.y), az = MathF.Abs(n.z);
        if (ax >= ay && ax >= az) return n.x >= 0 ? 0 : 1;
        if (ay >= az) return n.y >= 0 ? 2 : 3;
        return n.z >= 0 ? 4 : 5;
    }
}
