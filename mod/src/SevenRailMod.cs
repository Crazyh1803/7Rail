using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace SevenRail
{
    /// 7 Days to Die loads this from Mods/7Rail (IModApi). Everything EverRail comes from the converter's cache.
    public sealed class SevenRailMod : IModApi
    {
        public static string ModPath;

        public void InitMod(Mod mod)
        {
            ModPath = mod.Path;
            EverRailContent.Init(ModPath);
            new HarmonyLib.Harmony("gg.melty.7rail").PatchAll(typeof(SevenRailMod).Assembly);
            ModEvents.PlayerSpawnedInWorld.RegisterHandler(OnSpawned);
            ModEvents.WorldShuttingDown.RegisterHandler(OnShutdown);
            Log.Out("[7Rail] loaded from " + ModPath);
        }

        static void OnSpawned(ref ModEvents.SPlayerSpawnedInWorldData data)
        {
            if (!data.IsLocalPlayer || GameManager.IsDedicatedServer) return;
            if (Runner.Instance != null) return;
            var runner = new GameObject("7Rail").AddComponent<Runner>();
            try { runner.Begin(data.Position.ToVector3()); }
            catch (Exception e)
            {
                // Never leave a half-started train running: log once, tell the player, switch off.
                Log.Error("[7Rail] could not start: " + e);
                var p = GameManager.Instance.World?.GetPrimaryPlayer();
                if (p != null) GameManager.ShowTooltip(p, "7Rail could not start (see the log). Press Play in Melty again to re-read EverRail.", true, false, 10);
                UnityEngine.Object.Destroy(runner.gameObject);
            }
        }

        static void OnShutdown(ref ModEvents.SWorldShuttingDownData data)
        {
            if (Runner.Instance != null) { Runner.Instance.SaveNow(); UnityEngine.Object.Destroy(Runner.Instance.gameObject); }
        }
    }

    /// What 7Rail saves next to the world save: the railway's anchor and the train.
    public sealed class SaveData
    {
        public int Version = 1;
        public float[] FirstSpawn;
        public TrainState Train = new TrainState();
        public System.Collections.Generic.HashSet<string> Shaped = new System.Collections.Generic.HashSet<string>();
        public System.Collections.Generic.HashSet<int> InteriorsBuilt = new System.Collections.Generic.HashSet<int>();
        public bool DecorationsCleared;
    }

    public sealed class Runner : MonoBehaviour
    {
        public static Runner Instance;
        RailNetwork net;
        TrackView track;
        Earthworks earthworks;
        Interiors interiors;
        Train train;
        SaveData save;
        string savePath;
        float saveTimer, buffTimer;
        GUIStyle style, big;

        public void Begin(Vector3 spawn)
        {
            Instance = this;
            var world = GameManager.Instance.World;
            if (!EverRailContent.Ready)
            {
                GameManager.ShowTooltip(world.GetPrimaryPlayer(), EverRailContent.Problem, true, false, 15);
                return;
            }
            savePath = Path.Combine(GameIO.GetSaveGameDir(), "7rail.json");
            save = File.Exists(savePath) ? JsonConvert.DeserializeObject<SaveData>(File.ReadAllText(savePath)) : new SaveData();
            if (save.FirstSpawn == null) save.FirstSpawn = new[] { spawn.x, spawn.y, spawn.z };
            var worldName = GamePrefs.GetString(EnumGamePrefs.GameWorld);
            var worldDir = PathAbstractions.WorldsSearchPaths.GetLocation(worldName).FullPath;
            var t0 = Time.realtimeSinceStartup;
            net = RailNetwork.Build(world, worldDir, new Vector3(save.FirstSpawn[0], save.FirstSpawn[1], save.FirstSpawn[2]));
            // Trees and other decoration along the whole railway (7 Days' DecoManager, which also places them in
            // chunks the earthworks have not reached yet): cleared once per world.
            if (!save.DecorationsCleared && DecoManager.Instance != null)
            {
                var half = Mathf.CeilToInt((float)(TrackRow.TrackRing.GaugeWidthM / 2 + TrackRow.TrackRing.ClearCorridorM)) + 2;
                int n = 0;
                foreach (var line in net.Lines)
                    for (int i = 0; i < line.Points.Count; i += 3)
                    {
                        var pt = line.Points[i];
                        DecoManager.Instance.ClearDecoObjectsInArea(new Vector3i(Mathf.FloorToInt(pt.x) - half, 0, Mathf.FloorToInt(pt.z) - half), new Vector3i(half * 2, 256, half * 2));
                        n++;
                    }
                save.DecorationsCleared = true;
                Log.Out($"[7Rail] cleared trees and decoration along the railway ({n} spots)");
            }
            track = new TrackView(net, transform);
            earthworks = new Earthworks(net, save.Shaped);
            train = new Train(net, transform, save.Train);
            // Quit while driving: sit back in the cab at once, before the game lets the player fall.
            train.Riding = save.Train.Riding;
            interiors = new Interiors(train, transform, save.InteriorsBuilt);
            train.Interiors = interiors;
            train.Earthworks = earthworks;
            earthworks.PathAhead = train.PathAhead;
            Log.Out($"[7Rail] world '{worldName}' ready in {Time.realtimeSinceStartup - t0:0.00}s; train on {save.Train.Line} at {save.Train.S:0} m");
            var p = world.GetPrimaryPlayer();
            Log.Out($"[7Rail] height check at spawn: dtm {net.TerrainHeight(p.position.x, p.position.z):0.0}, live {world.GetHeightAt(p.position.x, p.position.z):0.0}, player y {p.position.y:0.0}");
            if (!save.Train.Found)
                GameManager.ShowTooltip(p, "A stalled EverRail locomotive waits on the rails near here. Bring it gas, coal or oil.", true, false, 10);
        }

        bool failed;

        void Update()
        {
            if (train == null || interiors == null || failed) return;
            try { Tick(); }
            catch (Exception e) { failed = true; Log.Error("[7Rail] stopped after an error: " + e); }
        }

        void Tick()
        {
            transform.position = -Origin.position;
            var player = GameManager.Instance.World?.GetPrimaryPlayer();
            if (player == null) return;
            track.Update(player.position);
            earthworks.Update(player.position, train.CurrentLine, train.State.S, Time.deltaTime);
            train.Update(Mathf.Min(Time.deltaTime, 0.1f), player.IsDead() ? null : player);
            interiors.Tick();
            // Aboard (cockpit or a wagon room) EverRail's heated train shields you from 7 Days weather.
            buffTimer += Time.deltaTime;
            if (buffTimer > 1f)
            {
                buffTimer = 0;
                if (!player.IsDead() && Aboard(player))
                    player.Buffs.AddBuff(BuffsRow.BuffCabin.BuffName);
            }
            if (!player.IsDead() && !train.Riding && !train.UsedThisFrame)
            {
                var input = player.playerInput;
                interiors.Update(player, input.Activate.WasPressed || input.PermanentActions.Activate.WasPressed);
            }
            saveTimer += Time.deltaTime;
            if (saveTimer > 30) { saveTimer = 0; SaveNow(); }
        }

        public void SaveNow()
        {
            if (save == null) return;
            try { File.WriteAllText(savePath, JsonConvert.SerializeObject(save, Formatting.Indented)); }
            catch (Exception e) { Log.Warning("[7Rail] could not save: " + e.Message); }
        }

        void OnDestroy()
        {
            track?.Clear();
            interiors?.Clear();
            earthworks?.Clear();
            train?.Destroy();
            if (Instance == this) Instance = null;
        }

        void OnGUI()
        {
            if (train == null) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = 18, richText = true };
                style.normal.textColor = Color.white;
                big = new GUIStyle(style) { fontSize = 24, alignment = TextAnchor.MiddleCenter };
            }
            var s = train.State;
            if (train.Riding)
            {
                var hp = string.Join("  ", s.Cars.ConvertAll(c => $"{Localization.Get(Train.Row(c).NameKey)} {c.Hp:0}"));
                var sw = train.NextSwitch();
                var lowE = s.Energy <= EverRailContent.Rule(EverrailRulesRow.RuleCriticalEnergy);
                var text =
                    $"<b>EverRail locomotive</b>   {Mathf.Abs(s.Speed) * 3.6f:0} km/h {(s.Speed < -0.1f ? "(reverse)" : "")}\n" +
                    $"Throttle {s.Throttle * 100:0}%   Engine {(s.EngineOn ? "on" : "off")}\n" +
                    $"Energy <color={(lowE ? "#ff6060" : "#9fe6ff")}>{train.EnergyFraction * 100:0.0}%</color>  {(train.NetWatts >= 0 ? "charging" : "draining")} {Mathf.Abs((float)train.NetWatts) / 1000f:0.0} kW\n" +
                    $"{hp}\n" +
                    (sw != null ? $"Next switch: {sw.Label} - {(train.SwitchSet(sw) ? "TURN OFF" : "straight on")} [Reload]\n" : "") +
                    "<size=14>W/S throttle | Shift brake | Space horn | C engine | R switch | E leave</size>";
                GUI.Box(new Rect(20, 20, 560, 200), "");
                GUI.Label(new Rect(32, 28, 540, 190), text, style);
            }
            // EverRail's energy countdown: while the battery is falling, show when it runs out.
            var player = GameManager.Instance.World?.GetPrimaryPlayer();
            if (player != null && (train.Riding || interiors.Inside(player.position, out _) || Vector3.Distance(player.position, train.FrontPosition) < 80))
            {
                var left = train.SecondsLeft;
                if (s.Energy <= 0) GUI.Label(new Rect(20, train.Riding ? 226 : 20, 900, 40), "<color=#ff5050>Train out of energy: heating off. Jump-start it with gas, coal or oil.</color>", big);
                else if (left >= 0) GUI.Label(new Rect(20, train.Riding ? 226 : 20, 900, 40), $"<color={(left < 60 ? "#ff5050" : "#ffd27a")}>Energy runs out in {(int)left / 60}:{(int)left % 60:00}. Keep moving to recharge.</color>", big);
            }
            var hint = train.Hint != "" ? train.Hint : train.Riding ? "" : interiors?.Hint ?? "";
            if (hint != "") GUI.Label(new Rect(0, Screen.height * 0.6f, Screen.width, 40), hint, big);
            if (train.Alert != "") GUI.Label(new Rect(0, Screen.height * 0.25f, Screen.width, 40), train.Alert, big);
        }

        // Debug helpers for testing (console: 7rail goto | status)
        public string Status() => train == null ? EverRailContent.Problem ?? "not started" :
            $"train on {train.State.Line} at {train.State.S:0} m, speed {train.State.Speed * 3.6f:0} km/h, energy {train.EnergyFraction * 100:0}%, front at {train.FrontPosition}";
        public Vector3 TrainFront => train.FrontPosition;
        public bool Running => train != null && !failed;
        public void AddCollision(Entity e, Bounds query, System.Collections.Generic.List<Bounds> result) => train?.AddCollision(e, query, result);
        /// Aboard and the train still has energy (EverRail's heating runs on it).
        public bool Aboard(EntityPlayerLocal p) => train != null && train.State.Energy > 0 && (train.Riding || interiors.Inside(p.position, out _));
    }

    public sealed class ConsoleCmd7Rail : ConsoleCmdAbstract
    {
        public override string[] getCommands() => new[] { "7rail" };
        public override string getDescription() => "7Rail: '7rail status', '7rail goto' (teleport beside the EverRail train), '7rail kit' (test: 200 gas cans), '7rail kits' (test: wagon kit, generator bank, battery bank, batteries, solar cells, gas)";
        public override bool IsExecuteOnClient => true;
        public override void Execute(System.Collections.Generic.List<string> _params, CommandSenderInfo _senderInfo)
        {
            var r = Runner.Instance;
            if (r == null || !r.Running) { SdtdConsole.Instance.Output("7Rail is not running: " + (EverRailContent.Problem ?? "see the log")); return; }
            if (_params.Count > 0 && _params[0] == "kits")
            {
                // Test helper: one of each carriage kit (normally crafted at a workbench).
                var p = GameManager.Instance.World.GetPrimaryPlayer();
                // Test helper: everything needed to try the add-ons, straight into the backpack (or at your feet if full).
                var give = new System.Collections.Generic.List<(string name, int count)>();
                foreach (var kit in RecipesRow.All) give.Add((kit.ResultItem, 1));
                give.Add((PowerSourcesRow.PowGenerator.SevendaysItem, 1));
                give.Add((PowerSourcesRow.PowBattery.SevendaysItem, 1));
                give.Add(("carBattery", 4));
                give.Add((PowerSourcesRow.PowSolar.SevendaysItem, 6));
                give.Add(("ammoGasCan", 100));
                foreach (var (name, count) in give)
                {
                    var iv = ItemClass.GetItem(name);
                    if (iv.IsEmpty()) { SdtdConsole.Instance.Output("7Rail: unknown item " + name); continue; }
                    var stack = new ItemStack(iv, count);
                    var where = p.bag.AddItem(stack) ? "backpack" : "at your feet";
                    if (where != "backpack") GameManager.Instance.ItemDropServer(stack, p.position, Vector3.zero);
                    SdtdConsole.Instance.Output($"7Rail: {count} x {iv.ItemClass.GetLocalizedItemName()} -> {where}");
                }
            }
            if (_params.Count > 0 && _params[0] == "kit")
            {
                // Test helper: 200 gas cans into the backpack.
                var p = GameManager.Instance.World.GetPrimaryPlayer();
                var ok = p.bag.AddItem(new ItemStack(ItemClass.GetItem("ammoGasCan"), 200));
                SdtdConsole.Instance.Output(ok ? "7Rail: 200 gas cans added to your backpack" : "7Rail: backpack full");
                Log.Out("[7Rail] kit given: " + ok);
            }
            if (_params.Count > 0 && _params[0] == "goto")
            {
                var p = GameManager.Instance.World.GetPrimaryPlayer();
                p.TeleportToPosition(r.TrainFront + Vector3.up * 3 + Vector3.right * 8);
            }
            SdtdConsole.Instance.Output(r.Status());
        }
    }
}



