using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SevenRail
{
    /// Saved state of the train (Save.cs writes it next to the world save).
    public sealed class TrainState
    {
        public string Line = "spur_spawn";
        public float S = 0;
        public float Speed;          // m/s along the line, negative = reverse
        public float Throttle;       // -1..1
        public bool EngineOn;
        public double Energy;        // EverRail energy units
        public bool Found;           // the player has fuelled it for the first time
        public bool Riding;          // the player was at the controls when the game was saved
        public List<CarState> Cars = new List<CarState>();
        public Dictionary<string, bool> Switches = new Dictionary<string, bool>();
    }

    public sealed class CarState
    {
        public string Id;            // train_cars row id
        public float Hp;
        public int SolarCells;
        public int GasInSlot;
        public float GasInSlotTimer;
    }

    /// The EverRail train in the 7 Days world: EverRail's models and sounds, run on EverRail's own train rules.
    public sealed class Train
    {
        public readonly TrainState State;
        readonly RailNetwork net;
        readonly Transform root;
        readonly List<GameObject> carObjects = new List<GameObject>();
        readonly float scale = (float)TrackRow.TrackRing.WorldScale;
        public bool Riding { get => State.Riding; set => State.Riding = value; }
        float brake, derailTimer, zombieTick, generatorTick;
        public string Hint = "";
        public string Alert = "";
        float alertUntil;
        AudioSource engineSlow, engineFast, rumble, oneShots, hornLoop, hum;
        bool hornHeld;

        public Train(RailNetwork net, Transform root, TrainState state)
        {
            this.net = net; this.root = root; State = state;
            // Cars from an older 7Rail version that no longer exist are dropped, not fatal.
            State.Cars.RemoveAll(c => !TrainCarsRow.All.Any(r => r.Id == c.Id));
            if (State.Cars.Count == 0)
            {
                // A new world: EverRail's locomotive waits, out of energy, near the turn-around loop of the spawn spur.
                var spawn = net.Spurs.FirstOrDefault(s => s.Id == "spur_spawn") ?? net.Ring;
                State.Line = spawn.Id;
                State.S = spawn.Loop ? 0 : Mathf.Max(CarLength(TrainCarsRow.CarLocomotive) + 5, spawn.LoopStartS - 5);
                foreach (var r in TrainCarsRow.All)
                    for (int n = 0; n < (int)r.StartCount; n++) State.Cars.Add(new CarState { Id = r.Id, Hp = (float)r.MaxHp });
            }
            foreach (var c in State.Cars) carObjects.Add(BuildCar(Row(c)));
            foreach (var go in carObjects) AddGuardRails(go);
            for (int i = 0; i < State.Cars.Count; i++)
            {
                var cells = State.Cars[i].SolarCells; State.Cars[i].SolarCells = 0;
                for (int k = 0; k < cells; k++) { State.Cars[i].SolarCells++; AddSolarPanel(i); }
            }
            BuildAudio();
        }

        // ---- EverRail rules (read from the player's EverRail by the converter) ----
        static double R(EverrailRulesRow r) => EverRailContent.Rule(r);
        double MaxBattery => R(EverrailRulesRow.RuleMaxBattery) + R(EverrailRulesRow.RuleCellCapacity) *
            4 * (Interiors?.BatteryBanks ?? 0);
        /// Net energy flow last frame (E/s): negative while standing or crawling.
        public double NetWatts;
        /// Seconds until the battery is empty at the current net flow, or -1 while charging.
        public float SecondsLeft => NetWatts < -1 ? (float)(State.Energy / -NetWatts) : -1f;
        double MassKg => R(EverrailRulesRow.RuleWagonMass) * 1000 * (1 + R(EverrailRulesRow.RuleStartingWagons) + State.Cars.Count - 1);
        float TopSpeed => (float)(R(EverrailRulesRow.RuleIdealSpeed) / 3.6) * scale;
        double FullThrottleDrain => R(EverrailRulesRow.RuleBaseEnergy) + R(EverrailRulesRow.RuleDriveEnergy);

        public static TrainCarsRow Row(CarState c) => TrainCarsRow.All.First(r => r.Id == c.Id);
        float CarLength(TrainCarsRow r) => (float)r.LengthM;
        public float EnergyFraction => (float)(State.Energy / MaxBattery);
        public bool Wrecked => State.Cars.Any(c => c.Hp <= 0);

        RailLine Line(string id) => net.Lines.First(l => l.Id == id);
        public RailLine CurrentLine => Line(State.Line);

        /// Where the front of the train has been, newest first, every TrailStep metres: the cars follow it, so they
        /// take the same way through junctions and loops as the locomotive did.
        const float TrailStep = 0.5f;
        readonly List<Vector3> trail = new List<Vector3>();

        void SeedTrail()
        {
            // On load: walk back along the line the train is on (and onto the ring before a spur's junction).
            trail.Clear();
            var line = Line(State.Line);
            var total = State.Cars.Sum(c => CarLength(Row(c)) + 0.5f) + 20;
            for (float d = 0; d <= total; d += TrailStep)
            {
                var s = State.S - d;
                trail.Add(!line.Loop && s < 0 ? net.Ring.At(line.JunctionS + s, out _) : line.At(s, out _));
            }
        }

        void UpdateTrail(Vector3 front)
        {
            if (trail.Count == 0) { SeedTrail(); return; }
            // Backing up: drop the points the front has moved back past.
            while (trail.Count > 2 && (front - trail[1]).sqrMagnitude < (trail[0] - trail[1]).sqrMagnitude * 0.25f) trail.RemoveAt(0);
            if ((front - trail[0]).sqrMagnitude >= TrailStep * TrailStep) trail.Insert(0, front);
            else trail[0] = front;
            var keep = (int)((State.Cars.Sum(c => CarLength(Row(c)) + 0.5f) + 20) / TrailStep) + 4;
            if (trail.Count > keep) trail.RemoveRange(keep, trail.Count - keep);
        }

        /// World position and forward of a point `back` metres behind the front of the train.
        Vector3 PointBehind(float back, out Vector3 fwd)
        {
            if (trail.Count < 2) SeedTrail();
            float walked = 0;
            for (int i = 0; i + 1 < trail.Count; i++)
            {
                var seg = Vector3.Distance(trail[i], trail[i + 1]);
                if (walked + seg >= back && seg > 1e-4f)
                {
                    fwd = (trail[i] - trail[i + 1]) / seg;
                    return Vector3.Lerp(trail[i], trail[i + 1], (back - walked) / seg);
                }
                walked += seg;
            }
            fwd = (trail[0] - trail[trail.Count - 1]).normalized;
            return trail[trail.Count - 1];
        }

        public Vector3 FrontPosition => Line(State.Line).At(State.S, out _);

        /// The layer the local player's controller (layer 20) collides with: 7 Days' moving-platform layer (28)
        /// carries a player standing on it; the vehicle layer (21) is the fallback.
        static int solidLayer = -1;
        public static int SolidLayerPublic => SolidLayer;
        static int SolidLayer
        {
            get
            {
                if (solidLayer >= 0) return solidLayer;
                foreach (var l in new[] { 28, 21, 16, 0 })
                    if (!Physics.GetIgnoreLayerCollision(20, l)) { solidLayer = l; break; }
                Log.Out("[7Rail] train colliders on layer " + solidLayer);
                return solidLayer;
            }
        }

        GameObject BuildCar(TrainCarsRow row)
        {
            var go = new GameObject("7Rail_" + row.Id);
            go.transform.SetParent(root, false);
            if (row.Layout != "none") return BuildFromLayout(go, row);
            Bounds? b = null;
            foreach (var id in row.Meshes.Split(';'))
            {
                var asset = EverrailAssetsRow.All.First(a => a.Id == id);
                var (mesh, mats) = EverRailContent.Mesh(asset);
                var part = new GameObject(id);
                part.transform.SetParent(go.transform, false);
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                part.AddComponent<MeshRenderer>().sharedMaterials = mats;
                if (b == null) b = mesh.bounds; else { var bb = b.Value; bb.Encapsulate(mesh.bounds); b = bb; }
                if (id == "mesh_wagon_connector")
                    part.transform.localPosition = new Vector3(0, 0, -(float)row.EverrailLengthM / 2); // coupling at the back
            }
            // Centre the EverRail model on the car's middle; EverRail's rail level (its Z = 0) sits on our rails.
            var inner = go.transform;
            foreach (Transform t in inner) t.localPosition += new Vector3(-b.Value.center.x, 0, -b.Value.center.z);
            go.transform.localScale = Vector3.one * scale;
            var col = go.AddComponent<BoxCollider>();
            col.center = new Vector3(0, b.Value.center.y, 0);
            col.size = new Vector3(b.Value.size.x * 0.9f, b.Value.size.y * 0.9f, b.Value.size.z * 0.95f);
            go.layer = SolidLayer;
            return go;
        }

        /// A car assembled from its EverRail blueprint (the locomotive: engine shell, cab interior, cupola glass), with
        /// the extra meshes placed where the blueprint puts the engine.
        GameObject BuildFromLayout(GameObject go, TrainCarsRow row)
        {
            var layout = Layout.Get(LayoutsRow.All.First(l => l.Id == row.Layout));
            var frame = new GameObject("frame").transform;
            frame.SetParent(go.transform, false);
            layout.Build(frame);
            var engine = layout.Parts.First(p => p.mesh == "SM_GlazeEngine").m;
            foreach (var id in row.Meshes.Split(';').Where(m => m != "none"))
            {
                var (mesh, mats) = EverRailContent.Mesh(EverrailAssetsRow.All.First(a => a.Id == id));
                Layout.Place(frame, id, mesh, mats, engine);
            }
            var b = layout.PartBounds("SM_GlazeEngine");
            frame.localPosition = new Vector3(-b.center.x, 0, -b.center.z);
            go.transform.localScale = Vector3.one * scale;
            var col = go.AddComponent<BoxCollider>();
            col.center = new Vector3(0, b.center.y, 0);
            col.size = new Vector3(b.size.x * 0.9f, b.size.y * 0.9f, b.size.z * 0.95f);
            go.layer = SolidLayer;
            return go;
        }

        public Interiors Interiors;
        public Transform CarTransform(int car) => car >= 0 && car < carObjects.Count ? carObjects[car].transform : null;
        public float CarHalfWidth(int car) { var c = carObjects[car].GetComponent<BoxCollider>(); return c.size.x * scale / 2; }
        public Earthworks Earthworks;

        /// The stretches of track the train will run over in the next `dist` metres (through junctions and spur exits).
        public List<(RailLine line, float s0, float s1)> PathAhead(float dist)
        {
            var path = new List<(RailLine, float, float)>();
            var line = Line(State.Line); float s = State.S;
            for (int guard = 0; guard < 4 && dist > 0; guard++)
            {
                if (line.Loop)
                {
                    var next = net.Spurs.Where(SwitchSet).Select(sp => (sp, d: Mathf.Repeat(sp.JunctionS - s, line.Length))).Where(x => x.d <= dist).OrderBy(x => x.d).FirstOrDefault();
                    if (next.sp == null) { path.Add((line, s, s + dist)); break; }
                    path.Add((line, s, s + next.d)); dist -= next.d; line = next.sp; s = 0;
                }
                else
                {
                    var left = line.Length - s;
                    if (left >= dist) { path.Add((line, s, s + dist)); break; }
                    path.Add((line, s, line.Length)); dist -= left; s = line.ExitS; line = net.Ring;
                }
            }
            return path;
        }
        public bool UsedThisFrame;
        float? lastYaw;
        float clearTimer; bool lineBlocked;

        /// The cab seat of the real, moving locomotive (EverRail's BP_Locomotive_01 cockpit).
        Vector3 CabSeat()
        {
            var layout = Layout.Get(LayoutsRow.All.First(l => l.Id == TrainCarsRow.CarLocomotive.Layout));
            var frame = carObjects[0].transform.Find("frame");
            // EverRail's own driver's seat (SeatEnter); the player's feet go a little below the seat point.
            var spot = Layout.Pos(layout.Spots["SeatEnter"]) - new Vector3(0, 0.3f, 0);
            return frame.TransformPoint(spot) + Origin.position;
        }

        public void Tell(string text) => Say(text);

        /// A direct move when the destination's chunks are loaded (no teleport screen), the game's teleport otherwise.
        public static void MovePlayer(EntityPlayerLocal player, Vector3 to)
        {
            if (GameManager.Instance.World.IsChunkAreaLoaded(Mathf.FloorToInt(to.x), 0, Mathf.FloorToInt(to.z)))
            {
                player.SetPosition(to); player.motion = Vector3.zero; player.fallDistance = 0;
            }
            else player.TeleportToPosition(to);
        }

        /// On top of a car, between its guard rails.
        public Vector3 RoofSpot(int car)
        {
            var go = carObjects[Mathf.Clamp(car, 0, carObjects.Count - 1)].transform;
            var col = go.GetComponent<BoxCollider>();
            return go.TransformPoint(col.center + new Vector3(0, col.size.y / 2 + 0.3f / scale, 0)) + Origin.position;
        }

        public void TakeControls(EntityPlayerLocal player, bool allowEmpty = false)
        {
            holdBrake = false;
            if (State.Energy <= 0 && !allowEmpty) { Say("Out of energy: refuel the locomotive outside with gas, coal or oil."); return; }
            Riding = true;
            if (!State.EngineOn && State.Energy > 0) ToggleEngine();
            MovePlayer(player, CabSeat());
            Say("You have the controls. W/S throttle, Shift brake, Space horn, E to leave them (the train keeps going).", 6);
        }

        /// Where the player comes out of the train: beside the car when stopped, on its roof when moving.
        public Vector3 OutsideExit(int car)
        {
            var go = carObjects[Mathf.Clamp(car, 0, carObjects.Count - 1)].transform;
            var col = go.GetComponent<BoxCollider>();
            // Beside the car, on the first solid block below rail height (the embankment or the ground): no fall.
            var world = GameManager.Instance.World;
            foreach (var extra in new[] { 1.5f, 3f, 5f })
            {
                var side = go.TransformPoint(col.center + new Vector3(col.size.x / 2 + extra / scale, 0, 0)) + Origin.position;
                var rail = go.position.y + Origin.position.y;
                for (int y = Mathf.FloorToInt(rail) + 2; y > Mathf.FloorToInt(rail) - 30; y--)
                {
                    var below = world.GetBlock(new Vector3i(Mathf.FloorToInt(side.x), y, Mathf.FloorToInt(side.z)));
                    var at = world.GetBlock(new Vector3i(Mathf.FloorToInt(side.x), y + 1, Mathf.FloorToInt(side.z)));
                    if (!below.isair && at.isair) return new Vector3(side.x, y + 1.05f, side.z);
                }
            }
            return go.TransformPoint(col.center + new Vector3(0, col.size.y / 2 + 0.5f, 0)) + Origin.position;
        }

        // ---- the roof: walkable while moving, with guard rails ----
        readonly List<Matrix4x4> prevCar = new List<Matrix4x4>();
        float plowTimer;
        int roofCar = -1;   // the car whose roof the player is standing on

        /// A player standing on a car roof moves (and turns) with the car.
        void CarryOnRoof(EntityPlayerLocal player)
        {
            roofCar = -1;
            var unity = player.position - Origin.position;
            for (int i = 0; i < carObjects.Count && i < prevCar.Count; i++)
            {
                var col = carObjects[i].GetComponent<BoxCollider>();
                var local = prevCar[i].inverse.MultiplyPoint3x4(unity);
                var top = col.center.y + col.size.y / 2;
                if (Mathf.Abs(local.x) > col.size.x / 2 || Mathf.Abs(local.z) > col.size.z / 2) continue;
                if (local.y < top - 0.6f / scale || local.y > top + 3f / scale) continue;
                var moved = carObjects[i].transform.localToWorldMatrix.MultiplyPoint3x4(local);
                if ((moved - unity).sqrMagnitude < 1e-6f) return;
                roofCar = i;
                player.SetPosition(moved + Origin.position, false);
                var yaw = Mathf.DeltaAngle(prevCar[i].rotation.eulerAngles.y, carObjects[i].transform.eulerAngles.y);
                if (Mathf.Abs(yaw) > 0.01f) { var r = player.rotation; r.y += yaw; player.SetRotation(r); }
                player.fallDistance = 0;
                return;
            }
        }

        /// Guard rails along both roof edges: solid for the player, drawn as simple metal bars.
        void AddGuardRails(GameObject car)
        {
            var col = car.GetComponent<BoxCollider>();
            var h = (float)RoofRow.RoofRailHeight.Value / scale;
            var inset = (float)RoofRow.RoofRailInset.Value / scale;
            var top = col.center.y + col.size.y / 2;
            var mat = new Material(Shader.Find("Standard")) { color = new Color(0.55f, 0.6f, 0.62f) };
            foreach (var side in new[] { -1f, 1f })
            {
                var x = side * (col.size.x / 2 - inset);
                var rail = new GameObject("guard_rail");
                rail.transform.SetParent(car.transform, false);
                rail.transform.localPosition = new Vector3(x, top + h / 2, col.center.z);
                var bc = rail.AddComponent<BoxCollider>();
                bc.size = new Vector3(0.15f / scale, h, col.size.z * 0.98f);
                rail.layer = SolidLayer;
                foreach (var y in new[] { h / 2, 0f })
                {
                    var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    UnityEngine.Object.Destroy(bar.GetComponent<Collider>());
                    bar.transform.SetParent(rail.transform, false);
                    bar.transform.localPosition = new Vector3(0, y, 0);
                    bar.transform.localScale = new Vector3(0.06f / scale, 0.06f / scale, col.size.z * 0.98f);
                    bar.GetComponent<MeshRenderer>().sharedMaterial = mat;
                }
                for (float z = -col.size.z * 0.48f; z <= col.size.z * 0.48f; z += 3f / scale)
                {
                    var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    UnityEngine.Object.Destroy(post.GetComponent<Collider>());
                    post.transform.SetParent(rail.transform, false);
                    post.transform.localPosition = new Vector3(0, 0, z - col.center.z);
                    post.transform.localScale = new Vector3(0.06f / scale, h, 0.06f / scale);
                    post.GetComponent<MeshRenderer>().sharedMaterial = mat;
                }
            }
        }

        /// EverRail's snow plow: blocks that fell onto the line ahead (rock, rubble, missed spots) are cleared as the
        /// train runs, each costing the locomotive EverRail's track-obstacle damage.
        void Plow()
        {
            if (State.Speed < 0.5f) return;
            var world = GameManager.Instance.World;
            var loco = carObjects[0].transform;
            var col = loco.GetComponent<BoxCollider>();
            var hw = col.size.x * scale / 2;
            var height = col.size.y * scale;
            var front = FrontPosition;
            CurrentLine.At(State.S, out var fwd);
            var right = Vector3.Cross(Vector3.up, fwd).normalized;
            var changes = new List<BlockChangeInfo>();
            var seen = new HashSet<Vector3i>();
            for (float d = 0; d <= 12; d += 1f)
                for (float w = -hw; w <= hw; w += 0.5f)
                {
                    var c = front + fwd * d + right * w;
                    for (float y = 0.6f; y < height && changes.Count < 300; y += 1f)
                    {
                        var pos = new Vector3i(Mathf.FloorToInt(c.x), Mathf.FloorToInt(c.y + y), Mathf.FloorToInt(c.z));
                        if (!seen.Add(pos)) continue;
                        var bv = world.GetBlock(pos);
                        if (bv.isair || !Earthworks.Natural(bv) || net.InsidePoi(pos.x + 0.5f, pos.z + 0.5f)) continue;
                        if (bv.ischild) { pos += bv.parent; if (!seen.Add(pos)) continue; }
                        changes.Add(new BlockChangeInfo(pos, BlockValue.Air, MarchingCubes.DensityAir));
                    }
                }
            if (changes.Count == 0) return;
            world.SetBlocksRPC(changes);
            State.Cars[0].Hp = Mathf.Max(1, State.Cars[0].Hp - changes.Count * 2f * (float)R(EverrailRulesRow.RuleObstacleDamage));
            Play(EverrailAssetsRow.SndBrake, 0.4f);
        }

        void BuildAudio()
        {
            AudioSource Src(EverrailAssetsRow row, bool loop)
            {
                var a = carObjects[0].AddComponent<AudioSource>();
                a.clip = row == null ? null : EverRailContent.Sound(row);
                a.loop = loop; a.spatialBlend = 1; a.minDistance = 8; a.maxDistance = 140; a.rolloffMode = AudioRolloffMode.Linear; a.volume = 0;
                if (loop && a.clip != null) a.Play();
                return a;
            }
            engineSlow = Src(EverrailAssetsRow.SndEngineSlow, true);
            engineFast = Src(EverrailAssetsRow.SndEngineFast, true);
            rumble = Src(EverrailAssetsRow.SndRumble, true);
            hornLoop = Src(EverrailAssetsRow.SndHornLoop, true);
            hum = Src(EverrailAssetsRow.SndElectricHum, true);
            oneShots = Src(null, false); oneShots.volume = 1;
        }

        void Play(EverrailAssetsRow row, float vol = 1) => oneShots.PlayOneShot(EverRailContent.Sound(row), vol);

        void Say(string text, float seconds = 4) { Alert = text; alertUntil = Time.time + seconds; }

        // ---- every frame ----
        public void Update(float dt, EntityPlayerLocal player)
        {
            if (Time.time > alertUntil) Alert = "";
            Simulate(dt);
            for (int i = 0; i < carObjects.Count; i++) { if (prevCar.Count <= i) prevCar.Add(Matrix4x4.identity); prevCar[i] = carObjects[i].transform.localToWorldMatrix; }
            Place();
            if (player != null && !Riding) CarryOnRoof(player);
            plowTimer += dt;
            if (plowTimer >= 0.25f) { plowTimer = 0; Plow(); }
            Sounds();
            if (player != null) Interact(player, dt);
            zombieTick += dt;
            if (zombieTick >= 0.5f) { ZombieDamage(zombieTick); zombieTick = 0; }
            generatorTick += dt;
            if (generatorTick >= 1f) { PowerCarriages(generatorTick); generatorTick = 0; }
        }

        void Simulate(float dt)
        {
            var line = Line(State.Line);
            line.At(State.S, out var fwd);
            var slopeDeg = Mathf.Asin(Mathf.Clamp(fwd.y, -1, 1)) * Mathf.Rad2Deg * Mathf.Sign(State.Speed == 0 ? 1 : State.Speed);
            bool canDrive = State.EngineOn && State.Energy > 0 && !Wrecked;
            float throttle = canDrive ? State.Throttle : 0;
            // Never run onto track the earthworks have not shaped yet: brake to a stop short of it.
            float effBrake = holdBrake ? 1 : brake;
            if (Earthworks != null && State.Speed > 0.1f)
            {
                var decel = (float)(R(EverrailRulesRow.RuleBrakeForce) / MassKg);
                var stop = State.Speed * State.Speed / (2 * decel) + 25f;
                clearTimer -= dt;
                if (clearTimer <= 0) { clearTimer = 0.2f; lineBlocked = !PathAhead(stop).All(seg => Earthworks.ClearAhead(seg.line, seg.s0, seg.s1)); }
                if (lineBlocked)
                {
                    throttle = 0; effBrake = 1;
                    Say("Clearing the line ahead...", 1);
                }
            }

            // EverRail: pull force at full throttle, falling linearly to zero at top speed.
            var vmax = TopSpeed;
            var pull = (float)R(EverrailRulesRow.RuleThrottleForce) * throttle * Mathf.Max(0, 1 - Mathf.Abs(State.Speed) / vmax);
            // EverRail charges slopes in energy (rule_slope_up/down below), not as a force on the train.
            var force = pull;
            var accel = force / (float)MassKg;
            if (!canDrive && Mathf.Abs(State.Speed) < 0.5f) { State.Speed = 0; accel = 0; } // parking brake
            State.Speed += accel * dt;
            if (effBrake > 0 || (!canDrive && Mathf.Abs(State.Speed) > 0))
            {
                var b = (float)(R(EverrailRulesRow.RuleBrakeForce) * Mathf.Max(effBrake, canDrive ? 0 : 0.02f) / MassKg) * dt;
                State.Speed = Mathf.Abs(State.Speed) <= b ? 0 : State.Speed - Mathf.Sign(State.Speed) * b;
            }
            State.Speed = Mathf.Clamp(State.Speed, -vmax * 0.5f, vmax);

            // EverRail energy: base drain while on, drive drain with throttle, slope and regenerative braking.
            // EverRail: its systems (heating included) drain all the time; moving near the ideal speed charges it.
            // EverRail's cost covers its 1 + StartingWagonCount car train: scale it to this train's size.
            var sizeShare = State.Cars.Count / (1 + R(EverrailRulesRow.RuleStartingWagons));
            double watts = State.Energy > 0 ? -R(EverrailRulesRow.RuleBaseEnergy) * sizeShare : 0;
            var band = (float)(R(EverrailRulesRow.RuleIdealSpeed) / 3.6) * scale;
            var ideal = vmax * 0.75f;
            var v = Mathf.Abs(State.Speed);
            if (State.EngineOn && v > 0.5f)
                watts += R(EverrailRulesRow.RuleDriveEnergy) * Mathf.Clamp01(1 - Mathf.Abs(ideal - v) / band);
            if (Mathf.Abs(State.Speed) > 0.3f)
                watts += slopeDeg < 0 ? R(EverrailRulesRow.RuleSlopeDown) * -slopeDeg : -R(EverrailRulesRow.RuleSlopeUp) * slopeDeg;
            if (effBrake > 0 && Mathf.Abs(State.Speed) > 0.3f) watts += R(EverrailRulesRow.RuleRegenBrake) * effBrake;
            var wasEmpty = State.Energy <= 0;
            NetWatts = watts;
            State.Energy = Math.Max(0, Math.Min(MaxBattery, State.Energy + watts * dt));
            if (!wasEmpty && State.Energy <= 0 && State.EngineOn) { Play(EverrailAssetsRow.SndEngineStop); Say("Out of energy: refuel the locomotive (gas, coal or oil)."); }

            // EverRail derailing: above the curve limit its timer runs, faster the further over.
            var limit = Mathf.Sqrt((float)TrackRow.TrackRing.MaxLateralAccel * line.Radius(State.S));
            var over = Mathf.Abs(State.Speed) - limit;
            if (over > 0)
            {
                var t = Mathf.Lerp((float)R(EverrailRulesRow.RuleDerailMax), (float)R(EverrailRulesRow.RuleDerailMin), Mathf.Clamp01(over / limit));
                derailTimer += dt / t;
                if (derailTimer > 0.5f) Say("Too fast for this curve!", 1);
                if (derailTimer >= 1) { derailTimer = 0; State.Speed = 0; foreach (var c in State.Cars) c.Hp = Mathf.Max(1, c.Hp - (float)Row(c).MaxHp * 0.3f); Play(EverrailAssetsRow.SndBrake); Say("Derailed! The train is damaged.", 5); }
            }
            else derailTimer = Mathf.Max(0, derailTimer - dt * 0.5f);

            // Move, switching lines at junctions and stopping at buffer stops.
            State.S += State.Speed * dt;
            if (line.Loop)
            {
                State.S = Mathf.Repeat(State.S, line.Length);
                if (State.Speed > 0)
                    foreach (var spur in net.Spurs)
                    {
                        var prev = State.S - State.Speed * dt;
                        if (SwitchSet(spur) && Crossed(prev, State.S, spur.JunctionS, line.Length))
                        {
                            State.Line = spur.Id; State.S = Mathf.Repeat(State.S - spur.JunctionS, line.Length);
                            Say("Switched onto " + spur.Label, 3);
                            break;
                        }
                    }
            }
            else
            {
                if (State.S < 0) { State.Line = net.Ring.Id; State.S = Mathf.Repeat(line.JunctionS + State.S, net.Ring.Length); }
                else if (State.S >= line.Length) { State.Line = net.Ring.Id; State.S = Mathf.Repeat(line.ExitS + State.S - line.Length, net.Ring.Length); Say("Back on the Ring line", 3); }
            }
            UpdateTrail(Line(State.Line).At(State.S, out _));
        }

        static bool Crossed(float a, float b, float x, float len) => a <= b ? a < x && x <= b : (a < x && x <= len) || x <= b;

        public bool SwitchSet(RailLine spur) => State.Switches.TryGetValue(spur.Id, out var on) && on;

        public RailLine NextSwitch()
        {
            if (State.Line != net.Ring.Id) return null;
            return net.Spurs.OrderBy(s => Mathf.Repeat(s.JunctionS - State.S, net.Ring.Length)).FirstOrDefault();
        }

        readonly List<Bounds> boxes = new List<Bounds>();

        public void AddCollision(Entity e, Bounds query, List<Bounds> result)
        {
            if (Riding && e is EntityPlayerLocal) return; // the driver sits in the cab
            foreach (var b in boxes) if (b.Intersects(query)) result.Add(b);
        }

        void Place()
        {
            boxes.Clear();
            float back = 0;
            for (int i = 0; i < State.Cars.Count; i++)
            {
                var len = CarLength(Row(State.Cars[i]));
                var front = PointBehind(back, out _);
                var rear = PointBehind(back + len, out _);
                var go = carObjects[i];
                go.transform.localPosition = (front + rear) * 0.5f;
                var dir = front - rear;
                if (dir.sqrMagnitude > 0.01f) go.transform.localRotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
                // Collision: 2 m slices along the car, each an axis-aligned box in 7 Days world space.
                var col = go.GetComponent<BoxCollider>();
                var w = col.size.x * scale * 0.8f; var h = col.size.y * scale;
                for (float d = 0; d < len; d += 2f)
                {
                    var c = PointBehind(back + d + 1f, out var f);
                    var r = Vector3.Cross(Vector3.up, f).normalized * (w / 2);
                    var bb = new Bounds(c + Vector3.up * (h / 2), Vector3.zero);
                    bb.Encapsulate(c + r + f); bb.Encapsulate(c - r - f); bb.Encapsulate(c + r - f); bb.Encapsulate(c - r + f);
                    bb.Encapsulate(c + Vector3.up * h); bb.Encapsulate(c + Vector3.up * 0.3f);
                    boxes.Add(bb);
                }
                back += len + 0.5f;
            }
        }

        void Sounds()
        {
            var moving = Mathf.Abs(State.Speed) / Mathf.Max(0.1f, TopSpeed);
            var on = State.EngineOn && State.Energy > 0 ? 1f : 0f;
            engineSlow.volume = on * Mathf.Lerp(0.5f, 0.15f, moving);
            engineFast.volume = on * Mathf.Lerp(0f, 0.6f, moving);
            engineFast.pitch = 0.8f + 0.4f * moving;
            rumble.volume = Mathf.Clamp01(moving * 1.2f) * 0.7f;
            hornLoop.volume = hornHeld ? 1 : 0;
            hum.volume = State.Cars.Any(c => c.SolarCells > 0) || (Interiors?.Generators().Any(g => g.IsOn && g.CurrentFuel > 0) ?? false) ? 0.25f : 0;
        }

        // ---- player ----
        int NearCar(Vector3 p, float reach)
        {
            for (int i = 0; i < carObjects.Count; i++)
            {
                var local = carObjects[i].transform.InverseTransformPoint(p - Origin.position);
                var col = carObjects[i].GetComponent<BoxCollider>();
                var ext = col.size * 0.5f + Vector3.one * (reach / scale);
                var d = local - col.center;
                if (Mathf.Abs(d.x) <= ext.x && Mathf.Abs(d.y) <= ext.y && Mathf.Abs(d.z) <= ext.z) return i;
            }
            return -1;
        }

        Vector3 SeatWorld()
        {
            var loco = carObjects[0].transform;
            var col = loco.GetComponent<BoxCollider>();
            // On the cab roof walkway near the front: the player rides outside, 7 Days style, watching for zombies.
            var local = new Vector3(0, col.center.y + col.size.y * 0.5f, col.size.z * 0.3f);
            return loco.TransformPoint(local) + Origin.position;
        }

        void Interact(EntityPlayerLocal player, float dt)
        {
            var input = player.playerInput;
            bool use = input.Activate.WasPressed || input.PermanentActions.Activate.WasPressed;
            UsedThisFrame = false;
            var held = player.inventory.holdingItemItemValue;
            var heldName = held.ItemClass?.GetItemName() ?? "";
            Hint = "";
            if (Riding)
            {
                player.SetPosition(CabSeat());

                player.motion = Vector3.zero;
                player.fallDistance = 0;
                if (input.MoveForward.IsPressed) State.Throttle = Mathf.Min(1, State.Throttle + dt * 0.5f);
                if (input.MoveBack.IsPressed) State.Throttle = Mathf.Max(-1, State.Throttle - dt * 0.5f);
                brake = input.Run.IsPressed ? 1 : 0;
                if (input.Run.WasPressed && Mathf.Abs(State.Speed) > 1) Play(EverrailAssetsRow.SndBrake, 0.7f);
                if (input.Jump.WasPressed) Play(EverrailAssetsRow.SndHornStart);
                if (input.Jump.WasReleased) Play(EverrailAssetsRow.SndHornEnd);
                hornHeld = input.Jump.IsPressed;
                if (hornHeld) AttractZombies(player);
                if (input.Reload.WasPressed && NextSwitch() is RailLine sw) { State.Switches[sw.Id] = !SwitchSet(sw); Say($"Switch to {sw.Label}: {(SwitchSet(sw) ? "TURN OFF" : "STRAIGHT ON")}", 3); }
                if (input.Crouch.WasPressed) ToggleEngine();
                if (use)
                {
                    Riding = false; brake = 0; hornHeld = false; UsedThisFrame = true;
                    var room = Interiors.FirstRoom();
                    if (room >= 0) Interiors.EnterRoom(player, room, true); else MovePlayer(player, RoofSpot(0));
                }
                return;
            }
            brake = 0; hornHeld = false; lastYaw = null;
            if (roofCar >= 0)
            {
                var isLoco = roofCar == 0;
                Hint = isLoco ? "[Use] Climb down into the cockpit" : "[Use] Climb down into the " + Localization.Get(Row(State.Cars[roofCar]).NameKey);
                if (use) { UsedThisFrame = true; if (isLoco || !Interiors.HasRoom(roofCar)) TakeControls(player, allowEmpty: true); else Interiors.EnterRoom(player, roofCar, true); }
                return;
            }
            var near = NearCar(player.position, 2.5f);
            if (use)
                Log.Out($"[7Rail] Use pressed: near car {near}, holding '{heldName}' x{player.inventory.holdingItemStack.count}, player {player.position}, train front {FrontPosition}, energy {State.Energy:0}");
            if (near < 0) return;
            var car = State.Cars[near];
            var row = Row(car);
            var fuel = PowerSourcesRow.All.FirstOrDefault(f => f.Kind == "fuel_item" && f.SevendaysItem == heldName);
            if (row.Id == TrainCarsRow.CarLocomotive.Id && fuel != null && State.Energy < MaxBattery * 0.999)
            {
                Hint = $"[Use] Refuel with {held.ItemClass.GetLocalizedItemName()}";
                if (use) Refuel(player, fuel);
            }
            else if (heldName.StartsWith("meleeToolRepair") && car.Hp < row.MaxHp)
            {
                Hint = "[Use] Repair (1 forged iron)";
                if (use) Repair(player, car);
            }
            else if (RecipesRow.All.FirstOrDefault(r => r.ResultItem == heldName) is RecipesRow kit)
            {
                Hint = "[Use] Couple " + Localization.Get(TrainCarsRow.All.First(c => c.Id == kit.Car).NameKey);
                if (use) Couple(player, kit);
            }
            else if (heldName == PowerSourcesRow.PowSolar.SevendaysItem && car.SolarCells < 6)
            {
                Hint = $"[Use] Mount a solar panel on the roof ({car.SolarCells}/6)";
                if (use && player.inventory.DecHoldingItem(1)) { car.SolarCells++; AddSolarPanel(near); Play(EverrailAssetsRow.SndRepair); }
            }            else if (row.Id == TrainCarsRow.CarLocomotive.Id)
            {
                Hint = State.Energy <= 0 ? "[Use] Climb into the cockpit (out of energy: refuel it with gas, coal or oil)" : "[Use] Climb into the cockpit";
                if (use) { UsedThisFrame = true; TakeControls(player, allowEmpty: true); }
            }
            else if (Interiors.HasRoom(near))
            {
                Hint = "[Use] Go inside the " + Localization.Get(row.NameKey);
                if (use) { UsedThisFrame = true; Interiors.EnterRoom(player, near, true); }
            }
        }

        void ToggleEngine()
        {
            State.EngineOn = !State.EngineOn;
            Play(State.EngineOn ? EverrailAssetsRow.SndEngineStart : EverrailAssetsRow.SndEngineStop);
            Say(State.EngineOn ? "Engine on" : "Engine off", 2);
        }

        void Refuel(EntityPlayerLocal player, PowerSourcesRow fuel)
        {
            if (State.Energy >= MaxBattery) { Say("The battery is full."); return; }
            var perItem = fuel.ThrottleSeconds * FullThrottleDrain;
            var want = (int)Math.Ceiling((MaxBattery - State.Energy) / perItem);
            var n = Math.Min(want, player.inventory.holdingItemStack.count);
            if (n <= 0 || !player.inventory.DecHoldingItem(n)) return;
            State.Energy = Math.Min(MaxBattery, State.Energy + n * perItem);
            Play(EverrailAssetsRow.SndRefuel);
            if (!State.Found) { State.Found = true; Say("EverRail's locomotive hums back to life. Climb aboard!", 6); }
            else Say($"Refuelled with {n}: battery {EnergyFraction * 100:0}%");
        }

        void Repair(EntityPlayerLocal player, CarState car)
        {
            var iron = ItemClass.GetItem("resourceForgedIron");
            var got = player.bag.DecItem(iron, 1);
            if (got < 1) got = player.inventory.DecItem(iron, 1);
            if (got < 1) { Say("Repairing needs forged iron."); return; }
            car.Hp = Mathf.Min((float)Row(car).MaxHp, car.Hp + 250);
            Play(EverrailAssetsRow.SndRepair);
        }

        void Couple(EntityPlayerLocal player, RecipesRow kit)
        {
            if (State.Cars.Count >= 7) { Say("The locomotive can pull 6 carriages at most."); return; }
            if (Mathf.Abs(State.Speed) > 0.2f) { Say("Stop the train to couple a carriage."); return; }
            if (!player.inventory.DecHoldingItem(1)) return;
            var row = TrainCarsRow.All.First(c => c.Id == kit.Car);
            State.Cars.Add(new CarState { Id = row.Id, Hp = (float)row.MaxHp });
            carObjects.Add(BuildCar(row));
            AddGuardRails(carObjects[carObjects.Count - 1]);
            SeedTrail();
            Interiors.Ensure(State.Cars.Count - 1);
            Play(EverrailAssetsRow.SndRepair);
            Say(Localization.Get(row.NameKey) + " coupled.");
        }

        /// Late-game add-ons: solar panels on the roofs (by day) and 7 Days generator banks built in wagon rooms.
        void PowerCarriages(float dt)
        {
            if (GameManager.Instance.World.IsDaytime())
                foreach (var c in State.Cars)
                    if (c.SolarCells > 0) State.Energy = Math.Min(MaxBattery, State.Energy + R(EverrailRulesRow.RuleBaseEnergy) / 3 * c.SolarCells * dt);
            if (Interiors == null) return;
            foreach (var te in Interiors.Generators())
            {
                if (!te.IsOn || te.CurrentFuel == 0 || State.Energy >= MaxBattery) continue;
                State.Energy = Math.Min(MaxBattery, State.Energy + R(EverrailRulesRow.RuleIsoEnergyRate) * 1000 * dt);
                generatorBurn += dt / 2f;   // 1 fuel every 2 s
                if (generatorBurn >= 1) { generatorBurn -= 1; te.CurrentFuel--; te.SetModified(); }
            }
        }
        float generatorBurn;

        /// 7 Days' own solar panel model on a car's roof, one per installed cell.
        void AddSolarPanel(int car)
        {
            if (solarPrefab == null) solarPrefab = DataLoader.LoadAsset<GameObject>("@:Entities/Electrical/solar_panelPrefab.prefab");
            if (solarPrefab == null) return;
            var go = carObjects[car];
            var col = go.GetComponent<BoxCollider>();
            int k = State.Cars[car].SolarCells - 1;
            var panel = UnityEngine.Object.Instantiate(solarPrefab, go.transform);
            foreach (var c in panel.GetComponentsInChildren<Collider>()) UnityEngine.Object.Destroy(c);
            panel.transform.localScale = Vector3.one / scale;
            var along = (k - 2.5f) / 6f * col.size.z * 0.8f;
            panel.transform.localPosition = new Vector3(0, col.center.y + col.size.y / 2, along);
            panel.transform.localRotation = Quaternion.Euler(0, 90, 0);
        }
        static GameObject solarPrefab;

        /// Nobody aboard: throttle off, brakes on, until someone takes the controls again.
        public void NobodyAboard() { State.Throttle = 0; holdBrake = true; }
        bool holdBrake;
        readonly List<Entity> found = new List<Entity>();

        void ZombieDamage(float dt)
        {
            var world = GameManager.Instance.World;
            for (int i = 0; i < carObjects.Count; i++)
            {
                var col = carObjects[i].GetComponent<BoxCollider>();
                var b = col.bounds; b.center += Origin.position; b.Expand(1.5f);
                found.Clear();
                world.GetEntitiesInBounds(typeof(EntityZombie), b, found);
                var alive = found.OfType<EntityAlive>().Count(z => !z.IsDead());
                if (alive == 0) continue;
                var car = State.Cars[i];
                var was = car.Hp;
                car.Hp = Mathf.Max(0, car.Hp - 10 * alive * dt);
                if (was > 0 && car.Hp <= 0) Say(Localization.Get(Row(car).NameKey) + " is wrecked! Repair it to move again.", 6);
                else if (Time.frameCount % 4 == 0) Say($"Zombies are tearing at the {Localization.Get(Row(car).NameKey)}!", 1);
            }
        }

        void AttractZombies(EntityPlayerLocal player)
        {
            // The horn carries: zombies within 40 m come for the train.
            var b = new Bounds(player.position, Vector3.one * 80);
            found.Clear();
            GameManager.Instance.World.GetEntitiesInBounds(typeof(EntityZombie), b, found);
            foreach (var z in found.OfType<EntityAlive>()) if (!z.IsDead()) z.SetInvestigatePosition(player.position, 200);
        }

        public void Destroy()
        {
            foreach (var go in carObjects) UnityEngine.Object.Destroy(go);
            carObjects.Clear();
        }
    }
}






