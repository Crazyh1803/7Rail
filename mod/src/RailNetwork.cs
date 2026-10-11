using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SevenRail
{
    /// One line of track: points every Step metres in 7 Days world space, with the running distance.
    public sealed class RailLine
    {
        public const float Step = 2f;
        public readonly string Id;
        public readonly bool Loop;
        public readonly List<Vector3> Points = new List<Vector3>();
        public float Length => Loop ? Points.Count * Step : (Points.Count - 1) * Step;
        // A spur's junction on the ring: where its distance 0 is.
        public float JunctionS;
        // Where a balloon spur rejoins the ring, and where its loop begins.
        public float ExitS, LoopStartS;
        public string Label = "";

        public RailLine(string id, bool loop) { Id = id; Loop = loop; }

        public Vector3 At(float s, out Vector3 forward)
        {
            int n = Points.Count;
            if (Loop) s = Mathf.Repeat(s, Length); else s = Mathf.Clamp(s, 0, Length - 0.001f);
            int i = Mathf.FloorToInt(s / Step);
            float t = s / Step - i;
            var a = Points[i % n];
            var b = Points[Loop ? (i + 1) % n : Math.Min(i + 1, n - 1)];
            forward = (b - a).normalized;
            if (forward == Vector3.zero) forward = Vector3.forward;
            return Vector3.Lerp(a, b, t);
        }

        /// Distance along this line closest to a world position (flat).
        public float Nearest(Vector3 p)
        {
            float best = float.MaxValue; int bi = 0;
            for (int i = 0; i < Points.Count; i++)
            {
                var d = Points[i] - p; d.y = 0;
                var m = d.sqrMagnitude;
                if (m < best) { best = m; bi = i; }
            }
            return bi * Step;
        }

        /// Radius of the curve at s (metres), from the turn over +-10 m.
        public float Radius(float s)
        {
            At(s - 10, out var f0); At(s + 10, out var f1);
            f0.y = 0; f1.y = 0;
            var ang = Vector3.Angle(f0, f1) * Mathf.Deg2Rad;
            return ang < 1e-4f ? 1e6f : 20f / ang;
        }
    }

    /// The whole railway for this world: the ring plus spurs, laid from the world's own heightmap so it is the
    /// same for every player of the save and known before chunks load.
    public sealed class RailNetwork
    {
        public RailLine Ring;
        public readonly List<RailLine> Spurs = new List<RailLine>();
        public IEnumerable<RailLine> Lines => new[] { Ring }.Concat(Spurs);
        ushort[] heights; int hmSize;

        public static RailNetwork Build(World world, string worldDir, Vector3 firstSpawn)
        {
            var net = new RailNetwork();
            net.LoadHeights(worldDir);
            net.LoadPois();
            var ringRow = TrackRow.TrackRing;
            var spurRow = TrackRow.TrackBranch;
            world.GetWorldExtent(out var min, out var max);
            float w = max.x - min.x, d = max.z - min.z;
            float insetX = w * 0.15f, insetZ = d * 0.15f;
            var r = Mathf.Max((float)ringRow.MinCurveRadiusM, Mathf.Min(w, d) * 0.1f);
            net.Ring = new RailLine("ring", true) { Label = "Ring line" };
            RoundedRect(net.Ring, min.x + insetX, min.z + insetZ, max.x - insetX, max.z - insetZ, r);
            net.AvoidPois(net.Ring, 0);
            net.Lift(net.Ring, ringRow);

            // Spurs: one to the player's first spawn (the stalled locomotive waits there), one into each biome.
            net.AddSpur("spawn", "Spawn halt", firstSpawn, spurRow);
            foreach (var biome in spurRow.Biome.Split(';'))
            {
                var target = FindBiomeStop(world, biome, net.Ring, min, max, worldDir);
                if (target.HasValue) net.AddSpur(biome, Localize(biome) + " halt", target.Value, spurRow);
            }
            Log.Out($"[7Rail] railway: ring {net.Ring.Length / 1000f:0.0} km, {net.Spurs.Count} spurs ({string.Join(", ", net.Spurs.Select(s => $"{s.Id} {s.Length:0} m"))})");
            return net;
        }

        static string Localize(string biome) => biome.Replace('_', ' ').Substring(0, 1).ToUpper() + biome.Replace('_', ' ').Substring(1);

        void LoadHeights(string worldDir)
        {
            var dtm = Path.Combine(worldDir, "dtm.raw");
            if (!File.Exists(dtm)) { Log.Warning("[7Rail] no dtm.raw in " + worldDir + "; using live terrain heights"); return; }
            heights = HeightMapUtils.LoadHeightMapRAW(dtm);
            hmSize = (int)Mathf.Sqrt(heights.Length);
        }

        // ---- POIs: the railway goes around them, and the earthworks never touch them ----
        readonly List<Rect> pois = new List<Rect>();
        readonly Dictionary<(int, int), List<int>> poiGrid = new Dictionary<(int, int), List<int>>();
        const float PoiCell = 64f;

        void LoadPois()
        {
            var deco = GameManager.Instance.GetDynamicPrefabDecorator();
            if (deco == null) return;
            foreach (var pi in deco.allPrefabs)
            {
                if (pi.boundingBoxSize.x < 4 && pi.boundingBoxSize.z < 4) continue;
                var r = new Rect(pi.boundingBoxPosition.x, pi.boundingBoxPosition.z, pi.boundingBoxSize.x, pi.boundingBoxSize.z);
                int id = pois.Count; pois.Add(r);
                for (int cx = Mathf.FloorToInt((r.xMin - 40) / PoiCell); cx <= Mathf.FloorToInt((r.xMax + 40) / PoiCell); cx++)
                    for (int cz = Mathf.FloorToInt((r.yMin - 40) / PoiCell); cz <= Mathf.FloorToInt((r.yMax + 40) / PoiCell); cz++)
                    {
                        if (!poiGrid.TryGetValue((cx, cz), out var l)) poiGrid[(cx, cz)] = l = new List<int>();
                        l.Add(id);
                    }
            }
            Log.Out($"[7Rail] {pois.Count} POIs to keep the railway away from");
        }

        IEnumerable<Rect> PoisNear(float x, float z) =>
            poiGrid.TryGetValue((Mathf.FloorToInt(x / PoiCell), Mathf.FloorToInt(z / PoiCell)), out var l) ? l.Select(i => pois[i]) : Enumerable.Empty<Rect>();

        public bool InsidePoi(float x, float z) => PoisNear(x, z).Any(r => r.Contains(new Vector2(x, z)));

        /// Pushes track points sideways out of POI footprints (plus the corridor), smoothing the line between passes.
        void AvoidPois(RailLine line, int pinEnds)
        {
            var margin = (float)(TrackRow.TrackRing.GaugeWidthM / 2 + TrackRow.TrackRing.ClearCorridorM) + 2f;
            var pts = line.Points.Select(p => new Vector2(p.x, p.z)).ToList();
            int n = pts.Count, moved = 0;
            for (int iter = 0; iter < 40; iter++)
            {
                bool any = false;
                for (int i = pinEnds; i < n - pinEnds; i++)
                {
                    var prev = pts[line.Loop ? (i - 1 + n) % n : Math.Max(0, i - 1)];
                    var next = pts[line.Loop ? (i + 1) % n : Math.Min(n - 1, i + 1)];
                    var tan = (next - prev).normalized; var perp = new Vector2(-tan.y, tan.x);
                    foreach (var r in PoisNear(pts[i].x, pts[i].y))
                    {
                        var e = new Rect(r.xMin - margin, r.yMin - margin, r.width + 2 * margin, r.height + 2 * margin);
                        if (!e.Contains(pts[i])) continue;
                        // The shorter sideways way out of the expanded footprint.
                        float best = float.MaxValue; Vector2 to = pts[i];
                        foreach (var sgn in new[] { 1f, -1f })
                            for (float d = 1; d < 400; d += 1)
                            {
                                var c = pts[i] + perp * sgn * d;
                                if (!e.Contains(c)) { if (d < best) { best = d; to = c; } break; }
                            }
                        pts[i] = Vector2.Lerp(pts[i], to, 0.5f) + (to - pts[i]).normalized * 0.5f;
                        any = true; moved++;
                    }
                }
                // Smooth the bends the pushes made.
                for (int pass = 0; pass < 4; pass++)
                    for (int i = pinEnds; i < n - pinEnds; i++)
                    {
                        var prev = pts[line.Loop ? (i - 1 + n) % n : Math.Max(0, i - 1)];
                        var next = pts[line.Loop ? (i + 1) % n : Math.Min(n - 1, i + 1)];
                        pts[i] = Vector2.Lerp(pts[i], (prev + next) / 2, 0.5f);
                    }
                if (!any) break;
            }
            if (moved == 0) return;
            if (line.Loop) pts.Add(pts[0]);
            line.Points.Clear();
            Resample(line, pts);
        }

        public float TerrainHeight(float x, float z)
        {
            if (heights == null) return GameManager.Instance.World.GetHeightAt(x, z);
            int px = Mathf.Clamp(Mathf.RoundToInt(x + hmSize / 2f), 0, hmSize - 1);
            int pz = Mathf.Clamp(Mathf.RoundToInt(z + hmSize / 2f), 0, hmSize - 1);
            return heights[px + pz * hmSize] / 256f;
        }

        static void RoundedRect(RailLine line, float x0, float z0, float x1, float z1, float r)
        {
            // Counter-clockwise seen from above, starting mid-south.
            var path = new List<Vector2>();
            void Arc(float cx, float cz, float a0)
            {
                for (int i = 0; i <= 32; i++) { var a = (a0 + 90f * i / 32) * Mathf.Deg2Rad; path.Add(new Vector2(cx + r * Mathf.Cos(a), cz + r * Mathf.Sin(a))); }
            }
            Arc(x1 - r, z0 + r, -90); Arc(x1 - r, z1 - r, 0); Arc(x0 + r, z1 - r, 90); Arc(x0 + r, z0 + r, 180);
            path.Add(path[0]);
            Resample(line, path);
        }

        static void Resample(RailLine line, List<Vector2> path)
        {
            float carry = 0;
            line.Points.Add(new Vector3(path[0].x, 0, path[0].y));
            for (int i = 1; i < path.Count; i++)
            {
                var a = path[i - 1]; var b = path[i]; var seg = Vector2.Distance(a, b);
                float t = RailLine.Step - carry;
                while (t <= seg) { var p = Vector2.Lerp(a, b, t / seg); line.Points.Add(new Vector3(p.x, 0, p.y)); t += RailLine.Step; }
                carry = seg - (t - RailLine.Step);
            }
            if (line.Loop) line.Points.RemoveAt(line.Points.Count - 1);
        }

        /// Heights: terrain, smoothed over 60 m, then limited to the sheet's max grade both ways.
        void Lift(RailLine line, TrackRow row, float? startHeight = null, float? endHeight = null)
        {
            int n = line.Points.Count;
            var raw = line.Points.Select(p => TerrainHeight(p.x, p.z)).ToArray();
            var h = new float[n];
            int win = 5; // +-10 m: follow the ground; earthworks make up the rest
            for (int i = 0; i < n; i++)
            {
                float sum = 0; int c = 0;
                for (int k = -win; k <= win; k++)
                {
                    int j = line.Loop ? (i + k + n) % n : Mathf.Clamp(i + k, 0, n - 1);
                    sum += raw[j]; c++;
                }
                h[i] = sum / c;
            }
            if (startHeight.HasValue) h[0] = startHeight.Value;
            float g = (float)row.MaxGrade * RailLine.Step;
            for (int pass = 0; pass < (line.Loop ? 3 : 1); pass++)
            {
                for (int i = 1; i < n; i++) h[i] = Mathf.Clamp(h[i], h[i - 1] - g, h[i - 1] + g);
                if (line.Loop) h[0] = Mathf.Clamp(h[0], h[n - 1] - g, h[n - 1] + g);
                if (!startHeight.HasValue)
                    for (int i = n - 2; i >= 0; i--) h[i] = Mathf.Clamp(h[i], h[i + 1] - g, h[i + 1] + g);
            }
            if (endHeight.HasValue)
            {
                // Meet the ring again at the exit: ease the last stretch onto its height.
                int blend = Math.Min(n - 1, 75);
                var diff = endHeight.Value - h[n - 1];
                for (int k = 0; k <= blend; k++) h[n - 1 - k] += diff * (1 - k / (float)blend);
            }
            for (int i = 0; i < n; i++) line.Points[i] = new Vector3(line.Points[i].x, h[i] + (float)row.HeightOffsetM, line.Points[i].z);
        }

        /// A spur leaves the ring heading the ring's way, runs out to the target, turns on a balloon loop and runs
        /// back on a parallel track to rejoin the ring further on, so a train never has to reverse.
        void AddSpur(string id, string label, Vector3 target, TrackRow row)
        {
            var nearest = Ring.Nearest(target);
            var ringPt = Ring.At(nearest, out var tr);
            var flat = target - ringPt; flat.y = 0;
            var dist = flat.magnitude;
            if (dist < 150) return; // already beside the ring
            float R = (float)row.MinCurveRadiusM;
            float lead = Mathf.Clamp(dist * 0.8f, 200f, 500f);
            float j1 = nearest - lead, j2 = nearest + lead;
            var p0 = Ring.At(j1, out var t0); var p3 = Ring.At(j2, out var t3);
            t0.y = 0; t0.Normalize(); t3.y = 0; t3.Normalize(); tr.y = 0; tr.Normalize();
            var toT = flat / dist;
            var side = Vector3.Cross(Vector3.up, toT);
            if (Vector3.Dot(side, tr) < 0) side = -side; // the outbound leg is the one nearer the junction
            var c = new Vector3(target.x, 0, target.z);
            var a = c - side * R; var b = c + side * R;
            var path = new List<Vector2>();
            void Bezier(Vector3 q0, Vector3 q1, Vector3 q2, Vector3 q3)
            {
                for (int i = 0; i <= 200; i++)
                {
                    float t = i / 200f, u = 1 - t;
                    var p = u * u * u * q0 + 3 * u * u * t * q1 + 3 * u * t * t * q2 + t * t * t * q3;
                    path.Add(new Vector2(p.x, p.z));
                }
            }
            var lOut = Vector3.Distance(p0, a); var lBack = Vector3.Distance(b, p3);
            Bezier(p0, p0 + t0 * lOut * 0.4f, a - toT * lOut * 0.4f, a);
            int loopStart = path.Count;
            for (int i = 1; i < 64; i++)
            {
                var ang = Mathf.PI * (1 - i / 64f);
                var p = c + side * R * Mathf.Cos(ang) + toT * R * Mathf.Sin(ang);
                path.Add(new Vector2(p.x, p.z));
            }
            Bezier(b, b - toT * lBack * 0.4f, p3 - t3 * lBack * 0.4f, p3);
            var line = new RailLine("spur_" + id, false) { JunctionS = Mathf.Repeat(j1, Ring.Length), ExitS = Mathf.Repeat(j2, Ring.Length), Label = label };
            Resample(line, path);
            AvoidPois(line, 20);
            line.LoopStartS = line.Nearest(new Vector3(a.x, 0, a.z));
            Lift(line, row, p0.y - (float)row.HeightOffsetM, p3.y - (float)row.HeightOffsetM);
            Spurs.Add(line);
        }
        /// A stop for a biome: the POI in that biome closest to the ring but at least 300 m inside it.
        static Vector3? FindBiomeStop(World world, string biome, RailLine ring, Vector3i min, Vector3i max, string worldDir)
        {
            var pois = new List<Vector3>();
            var prefabs = Path.Combine(worldDir, "prefabs.xml");
            if (File.Exists(prefabs))
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(prefabs), "name=\"(?!part_)([^\"]+)\" position=\"(-?\\d+),(-?\\d+),(-?\\d+)\""))
                    pois.Add(new Vector3(int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)));
            Vector3? best = null; float bestD = float.MaxValue;
            int inBiome = 0;
            foreach (var p in pois)
            {
                var b = world.GetBiomeInWorld((int)p.x, (int)p.z);
                if (b == null || b.m_sBiomeName != biome) continue;
                inBiome++;
                var onRing = ring.At(ring.Nearest(p), out _);
                var d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(onRing.x, onRing.z));
                if (d < 300 || d > bestD) continue;
                if (p.x < min.x + 200 || p.x > max.x - 200 || p.z < min.z + 200 || p.z > max.z - 200) continue;
                bestD = d; best = p + (onRing - p).normalized * 40f; // stop 40 m short of the POI, on the ring side
            }
            Log.Out($"[7Rail] biome {biome}: {pois.Count} POIs in world, {inBiome} in this biome, stop {(best.HasValue ? $"at {best.Value} ({bestD:0} m from the ring)" : "none")}");
            return best;
        }
    }
}

