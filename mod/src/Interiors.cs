using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SevenRail
{
    /// The wagons' insides, as EverRail does it: each wagon's interior is a fixed room in the 7 Days world (underground,
    /// out of sight), built from EverRail's interior blueprint. The room is exactly EverRail's own building floor (the
    /// wagon's FloorGridVolumeComponent) at the lifts' floor level, floored and walled with unbreakable blocks just
    /// outside EverRail's walls, so building there is plain 7 Days. The train keeps running outside meanwhile.
    public sealed class Interiors
    {
        const float FloorY = 12f;           // the rooms' floor surface, world y
        const float Spacing = 60f;          // rooms stand in a row along -z, one per car index

        sealed class Room
        {
            public int Car;
            public InteriorsRow Row;
            public Layout Layout;
            public Transform Root;          // the blueprint's frame at world_scale, placed in 7 Days world space
            public Bounds Box;              // the walkable room, 7 Days world space
            public bool Carved;
            public float Scale, EndDepth;
            public readonly List<(Transform quad, bool left, Vector3 local)> Windows = new List<(Transform, bool, Vector3)>();
        }

        readonly Train train;
        readonly Transform root;
        readonly HashSet<int> carved;       // saved: car indexes whose room was already cut out
        readonly List<Room> rooms = new List<Room>();
        readonly List<ChunkManager.ChunkObserver> observers = new List<ChunkManager.ChunkObserver>();
        readonly float scale = (float)TrackRow.TrackRing.WorldScale;
        Vector3? arriving;                  // a teleport waiting for its room to be ready
        public string Hint = "";

        public Interiors(Train train, Transform root, HashSet<int> carved)
        {
            this.train = train; this.root = root; this.carved = carved;
            for (int i = 0; i < train.State.Cars.Count; i++) Ensure(i);
        }

        static InteriorsRow RowFor(string carId) => InteriorsRow.All.FirstOrDefault(r => r.Car.Split(';').Contains(carId));
        public bool HasRoom(int car) => car >= 0 && car < train.State.Cars.Count && RowFor(train.State.Cars[car].Id) != null;
        public int FirstRoom() => Enumerable.Range(0, train.State.Cars.Count).Where(HasRoom).DefaultIfEmpty(-1).First();

        /// Makes the car's room and keeps its chunks loaded, so it is ready before anyone steps in.
        public void Ensure(int car)
        {
            if (!HasRoom(car) || rooms.Any(r => r.Car == car)) return;
            var row = RowFor(train.State.Cars[car].Id);
            var layout = Layout.Get(LayoutsRow.All.First(l => l.Id == row.Layout));
            var grid = Layout.Pos(layout.Spots[row.WalkMesh]);
            var half = layout.Extents[row.WalkMesh];
            float floor = Layout.Pos(layout.Spots["Lift01"]).y;
            float ceiling = layout.PartBounds("SM_Wagon_Generic_Interior_01").max.y;
            var go = new GameObject($"7Rail_room_{car}");
            go.transform.SetParent(root, false);
            // Scale so EverRail's build grid covers whole 7 Days blocks: its walls then coincide with the block walls.
            float width = Mathf.Max(1, Mathf.Round(half.x * 2 * scale));
            float s = width / (half.x * 2);
            float length = Mathf.Round(half.z * 2 * s);
            go.transform.localScale = Vector3.one * s;
            layout.Build(go.transform);
            // EverRail's grid centre goes to (0, FloorY, -car * Spacing), offset half a block where a side is odd.
            var centre = new Vector3(width % 2 == 1 ? 0.5f : 0f, FloorY, -car * Spacing + (length % 2 == 1 ? 0.5f : 0f));
            go.transform.localPosition = centre - new Vector3(grid.x, floor, grid.z) * s;
            var size = new Vector3(width, Mathf.Round((ceiling - floor) * s), length);
            var box = new Bounds(centre + new Vector3(0, size.y / 2, 0), size);
            // The ends: EverRail's wagon connector (its vestibule and interior doors) flush against each end wall.
            var connector = Layout.Get(LayoutsRow.All.First(l => l.Id == row.Ends));
            var cb = Bounds(connector);
            foreach (var front in new[] { true, false })
            {
                var c = new GameObject(front ? "connector_front" : "connector_back").transform;
                c.SetParent(root, false);
                c.localScale = Vector3.one * s;
                c.localRotation = Quaternion.identity;
                // Its lower floor tier (the lowest floor tile) on the room floor, its far side against the end wall.
                float cFloor = connector.Parts.Where(p => p.mesh == "SM_FloorTile01").Select(p => connector.PartBounds(p.mesh, p.m).min.y).Min();
                c.localPosition = new Vector3(centre.x, FloorY - cFloor * s, front ? box.max.z - cb.max.z * s : box.min.z - cb.min.z * s);
                connector.Build(c);
                // EverRail's connector doors slide sideways: show them open.
                foreach (Transform d in c)
                    if (d.name == "SM_InteriorDoor")
                    {
                        var mid = c.InverseTransformPoint(d.TransformPoint(d.GetComponent<MeshFilter>().sharedMesh.bounds.center));
                        d.localPosition += new Vector3(Mathf.Sign(mid.x) * 0.95f, 0, 0);
                    }
                c.SetParent(go.transform, true);
            }
            // Furnishing: EverRail's passenger wagon profile puts four Passenger Compartments in a wagon. They stand
            // along the side walls in the front half; the back half stays free for building.
            var unit = Layout.Get(LayoutsRow.All.First(l => l.Id == "lay_passenger_unit"));
            var ub = Bounds(unit);
            var len = ub.size.z * s;
            int compartments = row.Furnishing.StartsWith("none") ? 0 : (car == FirstRoom() ? 4 : 0);
            for (int k = 0; k < compartments; k++)
            {
                bool left = k % 2 == 0;
                float z = box.max.z - cb.size.z * s - len * (k / 2 + 0.5f) - 0.2f;
                var u = new GameObject("compartment_" + k).transform;
                u.SetParent(root, false);
                u.localScale = Vector3.one * s;
                u.localRotation = Quaternion.Euler(0, left ? 0 : 180, 0);
                var x = left ? box.min.x + ub.size.x * s / 2 : box.max.x - ub.size.x * s / 2;
                u.localPosition = new Vector3(x, FloorY, z) - u.localRotation * new Vector3(ub.center.x, 0, ub.center.z) * s;
                unit.Build(u);
                // Solid furniture: a box collider on each larger piece (bed, desk, stools, walls).
                foreach (Transform piece in u)
                {
                    var mf = piece.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh.bounds.size.magnitude < 0.6f) continue;
                    var bc = piece.gameObject.AddComponent<BoxCollider>();
                    bc.center = mf.sharedMesh.bounds.center; bc.size = mf.sharedMesh.bounds.size;
                    piece.gameObject.layer = Train.SolidLayerPublic;
                }
                u.SetParent(go.transform, true);
            }
            // Live windows in the free half: what a camera on the real, moving wagon sees outside.
            var windowsFrom = box.min.z + cb.size.z * s + 1f; var windowsTo = box.max.z - cb.size.z * s - (compartments > 0 ? len * 2 : 0) - 1f;
            var windows = new List<(Vector3 local, bool left)>();
            for (int k = 0; k < (int)row.Windows; k++)
            {
                var z = Mathf.Lerp(windowsFrom, windowsTo, (k + 0.5f) / (float)row.Windows);
                windows.Add((new Vector3(box.min.x + 0.02f, FloorY + 1.6f, z), true));
                windows.Add((new Vector3(box.max.x - 0.02f, FloorY + 1.6f, z), false));
            }            for (float z = box.min.z + 2; z < box.max.z; z += (float)row.LightSpacingM)
            {
                var l = new GameObject("light").AddComponent<Light>();
                l.transform.SetParent(go.transform, true);
                l.transform.localPosition = go.transform.InverseTransformPoint(root.TransformPoint(new Vector3(0, box.max.y - 0.5f, z)));
                l.type = LightType.Point; l.range = 10; l.intensity = 1.4f; l.color = new Color(1f, 0.9f, 0.78f);
            }
            var room = new Room { Car = car, Row = row, Layout = layout, Root = go.transform, Box = box, Carved = carved.Contains(car), Scale = s, EndDepth = cb.size.z * s };
            foreach (var (wl, left) in windows) room.Windows.Add(MakeWindow(go.transform, wl, left));
            rooms.Add(room);
            observers.Add(GameManager.Instance.AddChunkObserver(box.center, true, 2, -1));
        }

        Room Get(int car) { Ensure(car); return rooms.First(r => r.Car == car); }

        static Bounds Bounds(Layout l)
        {
            Bounds? b = null;
            foreach (var (mesh, m) in l.Parts)
            {
                var pb = l.PartBounds(mesh, m);
                if (b == null) b = pb; else { var x = b.Value; x.Encapsulate(pb); b = x; }
            }
            return b ?? new Bounds();
        }

        static Material windowLeft, windowRight;
        static RenderTexture rtLeft, rtRight;
        static Camera camLeft, camRight;
        float windowTimer;

        (Transform, bool, Vector3) MakeWindow(Transform parent, Vector3 worldLocal, bool left)
        {
            if (windowLeft == null)
            {
                rtLeft = new RenderTexture(640, 320, 24); rtRight = new RenderTexture(640, 320, 24);
                var unlit = Shader.Find("Unlit/Texture") ?? Shader.Find("Standard");
                windowLeft = new Material(unlit) { mainTexture = rtLeft };
                windowRight = new Material(unlit) { mainTexture = rtRight };
            }
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(q.GetComponent<Collider>());
            q.name = left ? "window_left" : "window_right";
            q.transform.SetParent(root, false);
            q.transform.localPosition = worldLocal;
            q.transform.localRotation = Quaternion.Euler(0, left ? -90 : 90, 0);   // facing into the room
            q.transform.localScale = new Vector3(2.4f, 1.2f, 1f);
            q.GetComponent<MeshRenderer>().sharedMaterial = left ? windowLeft : windowRight;
            q.transform.SetParent(parent, true);
            return (q.transform, left, worldLocal);
        }

        /// While the player is in a room, two cameras ride on the real wagon and look out of its sides.
        void UpdateWindows(Room r, bool active)
        {
            if (camLeft == null)
            {
                camLeft = new GameObject("7Rail_window_cam_left").AddComponent<Camera>();
                camRight = new GameObject("7Rail_window_cam_right").AddComponent<Camera>();
                foreach (var c in new[] { camLeft, camRight }) { c.fieldOfView = 45; c.nearClipPlane = 0.5f; c.farClipPlane = 1500; c.enabled = false; }
                camLeft.targetTexture = rtLeft; camRight.targetTexture = rtRight;
            }
            // Rendered by hand at 15 frames a second: two extra full views every frame would cost too much.
            camLeft.enabled = camRight.enabled = false;
            if (!active) return;
            windowTimer += Time.deltaTime;
            if (windowTimer < 1f / 15f) return;
            windowTimer = 0;
            var wagon = train.CarTransform(r.Car);
            if (wagon == null) return;
            // Mid-height of the wagon side, a little outside its wall, looking out.
            foreach (var (cam, side) in new[] { (camLeft, -1f), (camRight, 1f) })
            {
                var half = train.CarHalfWidth(r.Car) + 0.3f;
                cam.transform.position = wagon.position + wagon.right * side * half + Vector3.up * 3.5f;
                cam.transform.rotation = Quaternion.LookRotation(wagon.right * side, Vector3.up);
                cam.Render();
            }
        }

        /// The two ends of a room at floor level, where EverRail puts its lifts (front = +z).
        Vector3 End(Room r, bool front)
        {
            var p = r.Box.center; p.y = r.Box.min.y + 0.05f;
            p.z = front ? r.Box.max.z - r.EndDepth - 0.8f : r.Box.min.z + r.EndDepth + 0.8f;
            return p;
        }

        public bool Inside(Vector3 pos, out int car)
        {
            foreach (var r in rooms) { var b = r.Box; b.Expand(1.5f); if (b.Contains(pos)) { car = r.Car; return true; } }
            car = -1; return false;
        }

        public void EnterRoom(EntityPlayerLocal player, int car, bool atFront)
        {
            var r = Get(car);
            if (!r.Carved) { train.Tell("The wagon is being prepared, try again in a moment"); return; }
            arriving = End(r, atFront);
            Train.MovePlayer(player, arriving.Value);
        }

        // ---- 7 Days power blocks built in the rooms feed the EverRail battery ----
        readonly List<Vector3i> generatorPos = new List<Vector3i>();
        public int BatteryBanks;
        float powerScan;

        public IEnumerable<TileEntityPowerSource> Generators()
        {
            var world = GameManager.Instance.World;
            foreach (var p in generatorPos)
                if (world.GetTileEntity(p) is TileEntityPowerSource te) yield return te;
        }

        void ScanPower()
        {
            var world = GameManager.Instance.World;
            generatorPos.Clear(); BatteryBanks = 0;
            foreach (var r in rooms)
            {
                if (!r.Carved) continue;
                var b = r.Box;
                for (int x = Mathf.RoundToInt(b.min.x); x < Mathf.RoundToInt(b.max.x); x++)
                    for (int y = Mathf.RoundToInt(b.min.y); y < Mathf.RoundToInt(b.max.y); y++)
                        for (int z = Mathf.RoundToInt(b.min.z); z < Mathf.RoundToInt(b.max.z); z++)
                        {
                            var bv = world.GetBlock(new Vector3i(x, y, z));
                            if (bv.isair || bv.ischild) continue;
                            var name = bv.Block.GetBlockName();
                            if (name == PowerSourcesRow.PowGenerator.SevendaysItem) generatorPos.Add(new Vector3i(x, y, z));
                            else if (name == PowerSourcesRow.PowBattery.SevendaysItem) BatteryBanks++;
                        }
            }
        }

        public void Tick()
        {
            powerScan -= Time.deltaTime;
            if (powerScan <= 0) { powerScan = 2f; ScanPower(); }
            foreach (var r in rooms) Carve(r);
            // Performance: a room is only drawn while the player is in or near it.
            var p = GameManager.Instance.World?.GetPrimaryPlayer();
            if (p == null) return;
            foreach (var r in rooms)
            {
                var near = (r.Box.center - p.position).sqrMagnitude < 150f * 150f;
                if (r.Root.gameObject.activeSelf != near) r.Root.gameObject.SetActive(near);
            }
        }

        public void Update(EntityPlayerLocal player, bool use)
        {
            Hint = "";
            if (arriving.HasValue)
            {
                if (Vector3.Distance(player.position, arriving.Value) < 3f) arriving = null;
                return;
            }
            var inRoom = Inside(player.position, out var car);
            foreach (var rr in rooms) if (rr.Car != car) { }
            if (rooms.Count > 0) UpdateWindows(inRoom ? Get(car) : rooms[0], inRoom);
            if (!inRoom) return;
            var room = Get(car);
            var pos = player.position;
            bool Near(Vector3 p) => Mathf.Abs(pos.z - p.z) < 2.0f;
            bool sideDoor = train.State.Cars[car].Id == TrainCarsRow.CarExpedition.Id
                            && Mathf.Abs(pos.z - room.Box.center.z) < 3f
                            && (pos.x - room.Box.min.x < 1.6f || room.Box.max.x - pos.x < 1.6f);
            if (sideDoor)
            {
                if (Mathf.Abs(train.State.Speed) > 0.5f)
                    Hint = "The train is moving: stop it from the cockpit to step off";
                else
                {
                    Hint = "[Use] Step off to the ground (the train waits for you)";
                    if (use) { train.NobodyAboard(); player.TeleportToPosition(train.OutsideExit(car)); }
                }
            }
            else if (Near(End(room, true)))
            {
                int prev = car - 1;
                if (!HasRoom(prev))
                {
                    Hint = "[Use] Go forward to the cockpit";
                    if (use) train.TakeControls(player);
                }
                else
                {
                    Hint = "[Use] Go forward to the " + Localization.Get(Train.Row(train.State.Cars[prev]).NameKey);
                    if (use) EnterRoom(player, prev, false);
                }
            }
            else if (Near(End(room, false)))
            {
                int next = car + 1;
                if (HasRoom(next))
                {
                    Hint = "[Use] Go back to the " + Localization.Get(Train.Row(train.State.Cars[next]).NameKey);
                    if (use) EnterRoom(player, next, true);
                }
                else
                {
                    Hint = "[Use] Climb up onto the roof";
                    if (use) Train.MovePlayer(player, train.RoofSpot(car));
                }
            }
        }

        void Carve(Room r)
        {
            if (r.Carved) return;
            var world = GameManager.Instance.World;
            var b = r.Box;
            // The box is block-aligned: air fills it exactly, the shell is the blocks around it.
            int x0 = Mathf.RoundToInt(b.min.x) - 1, x1 = Mathf.RoundToInt(b.max.x);
            int y0 = Mathf.RoundToInt(b.min.y) - 1, y1 = Mathf.RoundToInt(b.max.y);
            int z0 = Mathf.RoundToInt(b.min.z) - 1, z1 = Mathf.RoundToInt(b.max.z);
            for (int x = x0; x <= x1; x += 8)
                for (int z = z0; z <= z1; z += 8)
                    if (!world.IsChunkAreaLoaded(x, 0, z)) return;
            var shell = Block.GetBlockValue(r.Row.ShellBlock);
            var lining = Block.GetBlockValue(r.Row.LiningBlock);
            if (lining.isair) { Log.Warning("[7Rail] lining block " + r.Row.LiningBlock + " not found; using the shell block"); lining = shell; }
            var changes = new List<BlockChangeInfo>();
            // Air inside, a lining layer (what shows through EverRail's gaps), then the unbreakable shell around it.
            for (int x = x0 - 1; x <= x1 + 1; x++)
                for (int y = y0 - 1; y <= y1 + 1; y++)
                    for (int z = z0 - 1; z <= z1 + 1; z++)
                    {
                        bool outer = x == x0 - 1 || x == x1 + 1 || y == y0 - 1 || y == y1 + 1 || z == z0 - 1 || z == z1 + 1;
                        bool edge = x == x0 || x == x1 || y == y0 || y == y1 || z == z0 || z == z1;
                        var pos = new Vector3i(x, y, z);
                        if (outer) changes.Add(new BlockChangeInfo(pos, shell, MarchingCubes.DensityTerrain));
                        else if (edge) changes.Add(new BlockChangeInfo(pos, lining, MarchingCubes.DensityAir));
                        else changes.Add(new BlockChangeInfo(pos, BlockValue.Air, MarchingCubes.DensityAir));
                    }
            world.SetBlocksRPC(changes);
            r.Carved = true;
            carved.Add(r.Car);
            Log.Out($"[7Rail] room for car {r.Car} carved: {b.size.x:0.0} x {b.size.y:0.0} x {b.size.z:0.0} m, floor y {b.min.y:0.0}, centre {b.center}");
        }

        public void Clear()
        {
            foreach (var r in rooms) if (r.Root != null) Object.Destroy(r.Root.gameObject);
            foreach (var o in observers) GameManager.Instance.RemoveChunkObserver(o);
            rooms.Clear(); observers.Clear();
        }
    }
}
