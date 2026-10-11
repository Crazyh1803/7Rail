using System;
using System.Collections.Generic;
using UnityEngine;

namespace SevenRail
{
    /// Shapes 7 Days terrain to the railway: an embankment of bed_block under the rails, and a cutting (a tunnel where
    /// the hill rises above the clearance) through terrain, plants and whole trees above them. Buildings are never
    /// touched. It works ahead of the train, keeping the chunks there loaded, and the train will not run onto track
    /// that is not shaped yet (Train asks ClearAhead).
    public sealed class Earthworks
    {
        // Small batches often: 7 Days rebuilds the meshes of every chunk touched, so big batches stutter.
        const float AroundPlayer = 90f, AheadOfTrain = 400f, BehindTrain = 40f, TickSeconds = 0.25f;
        const int BudgetPerTick = 2500;
        readonly RailNetwork net;
        readonly HashSet<string> done;       // "line:sample" already shaped (saved)
        readonly BlockValue bed;
        readonly int halfWidth, clearance;
        readonly HashSet<Vector3i> cleared = new HashSet<Vector3i>();
        ChunkManager.ChunkObserver observer;
        float timer;

        public Earthworks(RailNetwork net, HashSet<string> done)
        {
            this.net = net; this.done = done;
            var row = TrackRow.TrackRing;
            bed = Block.GetBlockValue(row.BedBlock);
            halfWidth = Mathf.CeilToInt((float)(row.GaugeWidthM / 2 + row.ClearCorridorM));
            clearance = Mathf.CeilToInt((float)row.ClearanceM);
        }

        static string Key(RailLine line, int i) => line.Id + ":" + i;

        /// True when every sample from s0 to s1 along the line is shaped (the train may run there).
        public bool ClearAhead(RailLine line, float s0, float s1)
        {
            for (float s = s0; s <= s1; s += RailLine.Step)
            {
                if (!line.Loop && (s < 0 || s > line.Length)) continue;
                var i = Mathf.FloorToInt((line.Loop ? Mathf.Repeat(s, line.Length) : s) / RailLine.Step);
                if (!done.Contains(Key(line, i))) return false;
            }
            return true;
        }

        public Func<float, List<(RailLine line, float s0, float s1)>> PathAhead;

        public void Update(Vector3 player, RailLine trainLine, float trainS, float dt)
        {
            timer += dt;
            if (timer < TickSeconds) return;
            timer = 0;
            var world = GameManager.Instance.World;
            // Keep the chunks ahead of the train loaded, wherever the player is (the cab, a wagon room or far away).
            var ahead = trainLine.At(trainS + 60f, out _);
            if (observer == null) observer = GameManager.Instance.AddChunkObserver(ahead, true, 6, -1);
            else observer.position = ahead;

            var changes = new List<BlockChangeInfo>();
            cleared.Clear();
            // The train's own path first, nearest first; then anything around the player.
            var path = PathAhead?.Invoke(AheadOfTrain) ?? new List<(RailLine, float, float)> { (trainLine, trainS, trainS + AheadOfTrain) };
            path.Insert(0, (trainLine, trainS - BehindTrain, trainS));
            foreach (var (line, s0, s1) in path)
                for (float s = s0; s <= s1 && changes.Count < BudgetPerTick; s += RailLine.Step)
                {
                    if (!line.Loop && (s < 0 || s > line.Length)) continue;
                    var i = Mathf.FloorToInt((line.Loop ? Mathf.Repeat(s, line.Length) : s) / RailLine.Step);
                    TryShape(world, line, i, changes);
                }
            foreach (var line in net.Lines)
                for (int i = 0; i < line.Points.Count && changes.Count < BudgetPerTick; i++)
                {
                    var p = line.Points[i];
                    if (Mathf.Abs(p.x - player.x) > AroundPlayer || Mathf.Abs(p.z - player.z) > AroundPlayer) continue;
                    TryShape(world, line, i, changes);
                }
            if (changes.Count > 0) world.SetBlocksRPC(changes);
        }

        void TryShape(World world, RailLine line, int i, List<BlockChangeInfo> changes)
        {
            var key = Key(line, i);
            if (done.Contains(key)) return;
            var p = line.Points[i];
            if (!world.IsChunkAreaLoaded(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.z))) return;
            Shape(world, line, i, changes);
            done.Add(key);
        }

        void Shape(World world, RailLine line, int i, List<BlockChangeInfo> changes)
        {
            var p = line.At(i * RailLine.Step, out var fwd);
            var right = Vector3.Cross(Vector3.up, fwd).normalized;
            // Rails sit on the top face of the bed: the bed's top block is the one just below the rail height.
            int top = Mathf.FloorToInt(p.y - (float)TrackRow.TrackRing.HeightOffsetM) - 1;
            // Half-metre steps across and along, so a diagonal corridor leaves no columns of rock standing.
            var columns = new HashSet<(int, int, bool, bool)>();
            for (float w = -halfWidth; w <= halfWidth; w += 0.5f)
                for (float a = -RailLine.Step; a <= 0; a += 0.5f)
                {
                    var cc = p + right * w + fwd * a;
                    columns.Add((Mathf.FloorToInt(cc.x), Mathf.FloorToInt(cc.z), Mathf.Abs(w) <= TrackRow.TrackRing.GaugeWidthM / 2 + 1, Mathf.Abs(w) <= TrackRow.TrackRing.TunnelHalfWidthM));
                }
            foreach (var (x, z, isBed, inTunnel) in columns)
                {
                    if (net.InsidePoi(x + 0.5f, z + 0.5f)) continue;   // never dig or fill inside a POI
                    // Under a hill (solid rock above the clearance) only the tunnel lane is cut: far fewer blocks.
                    if (!inTunnel && !world.GetBlock(new Vector3i(x, top + clearance + 2, z)).isair && Natural(world.GetBlock(new Vector3i(x, top + clearance + 2, z)))) { if (!isBed) continue; }
                    bool bedLane = isBed;
                    // Cut: everything natural from the rail level up to the clearance (a tunnel inside hills).
                    for (int y = top + 1; y <= top + clearance + 1; y++)
                    {
                        var pos = new Vector3i(x, y, z);
                        var bv = world.GetBlock(pos);
                        if (bv.isair || !Natural(bv)) continue;
                        // Trees are multi-blocks: take the whole tree out from its base block.
                        if (bv.ischild) { pos += bv.parent; bv = world.GetBlock(pos); if (bv.isair) continue; }
                        if (!cleared.Add(pos)) continue;
                        changes.Add(new BlockChangeInfo(pos, BlockValue.Air, MarchingCubes.DensityAir));
                    }
                    if (!bedLane) continue;
                    // Fill: an embankment down to the ground.
                    for (int y = top; y > top - 40 && y > 1; y--)
                    {
                        var bv = world.GetBlock(new Vector3i(x, y, z));
                        if (!bv.isair && !Natural(bv, plantsOnly: true)) break;
                        changes.Add(new BlockChangeInfo(new Vector3i(x, y, z), bed, MarchingCubes.DensityTerrain));
                    }
                }
        }

        public static bool Natural(BlockValue bv, bool plantsOnly = false)
        {
            var b = bv.Block;
            if (b == null) return false;
            var tree = b.blockMaterial.id.StartsWith("Mtree") || b.GetBlockName().StartsWith("tree");
            var plant = tree || b.bIsPlant || b.blockMaterial.IsPlant || b.IsTerrainDecoration || b.blockMaterial.IsGroundCover || (b.IsDecoration && !b.isMultiBlock);
            return plantsOnly ? plant : plant || b.shape.IsTerrain();
        }

        public void Clear()
        {
            if (observer != null) GameManager.Instance.RemoveChunkObserver(observer);
            observer = null;
        }
    }
}
