using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using MGSC;
using UnityEngine;

namespace QuasimorphCoop.Lockstep
{
    // Offline determinism harness. Only active when lockstep_test.cfg exists in the mod folder;
    // the file is consumed (renamed) on startup so a crash can't hijack the next normal launch.
    //
    // It loads a dedicated test slot, makes sure there is a second party merc, seeds the RNG,
    // drives both mercs with a scripted bot, logs a state fingerprint after every action point
    // to lockstep_<run>.log, then quits. Two runs at different frame rates must match line for line.
    internal class LockstepTest : MonoBehaviour
    {
        private const string ConfigName = "lockstep_test.cfg";

        private static LockstepTest _instance;

        private Dictionary<string, string> _cfg;
        private string _logPath;
        private int _maxPlayerAps;
        private System.Random _botRng;
        private bool _running;
        private bool _botEnabled = true;
        private Menus.MenuTest _menuTest;
        private int _apSeq;
        private int _playerAps;
        private float _lastApTime;
        private float _startTime;
        private int _attemptsThisAp;
        private TurnController _turnController;
        private InventoryBot _inventoryBot;
        private int _inventoryEvery;
        private int _inventoryDoneAt = -1;
        private StationBot _stationBot;
        private int _stationEvery;
        private int _stationDoneAt = -1;
        private int _exitAt;
        private int _abortAt;
        private bool _exitPressed;
        private int _holdAt;
        private int _releaseAt;
        private int _treatEvery;
        private int _treatDoneAt = -1;

        public static bool Active => _instance != null;

        public static void OnMainMenu(IModContext context)
        {
            if (_instance != null)
            {
                return;
            }
            string cfgPath = Path.Combine(context.ModContentPath, ConfigName);
            if (!File.Exists(cfgPath))
            {
                return;
            }
            Dictionary<string, string> cfg = File.ReadAllLines(cfgPath)
                .Select(l => l.Split(new[] { '=' }, 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
            File.Copy(cfgPath, cfgPath + ".consumed", overwrite: true);
            File.Delete(cfgPath);

            GameObject go = new GameObject("QMCoop_LockstepTest");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<LockstepTest>();
            _instance.Configure(cfg, context.ModContentPath);
        }

        private void Configure(Dictionary<string, string> cfg, string modPath)
        {
            _cfg = cfg;
            _logPath = Path.Combine(modPath, $"lockstep_{Get("run", "A")}.log");
            File.WriteAllText(_logPath, $"# cfg {string.Join(" ", cfg.Select(kv => kv.Key + "=" + kv.Value))}\n");
            _maxPlayerAps = int.Parse(Get("aps", "120"), CultureInfo.InvariantCulture);
            GameplayRng.Enabled = Get("scoped", "0") == "1";

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = int.Parse(Get("fps", "60"), CultureInfo.InvariantCulture);
            Time.timeScale = float.Parse(Get("timescale", "1"), CultureInfo.InvariantCulture);
            _startTime = Time.realtimeSinceStartup;

            Log.Info($"LOCKSTEP test configured: {string.Join(" ", cfg.Select(kv => kv.Key + "=" + kv.Value))}");
            if (Get("menutest", "0") == "1")
            {
                File.Delete(Path.Combine(modPath, "menutest.log"));
                _menuTest = Menus.MenuTest.Start(modPath, int.Parse(Get("slot", "8"), CultureInfo.InvariantCulture));
                return;
            }
            StartCoroutine(LoadSlotFromMenu(int.Parse(Get("slot", "8"), CultureInfo.InvariantCulture)));
        }

        private string Get(string key, string fallback) => _cfg.TryGetValue(key, out string v) ? v : fallback;

        private IEnumerator LoadSlotFromMenu(int slot)
        {
            yield return new WaitForSecondsRealtime(3f);
            GameModeStateMachine machine = FindObjectOfType<GameModeStateMachine>();
            Log.Info($"LOCKSTEP loading slot {slot}.");
            AccessTools.Method(typeof(GameModeStateMachine), "MainMenuFinished").Invoke(machine, new object[] { false, slot, null });
        }

        public static void OnDungeonStarted()
        {
            if (_instance == null)
            {
                return;
            }
            if (_instance._menuTest != null)
            {
                _instance._menuTest.OnDungeonStarted();
                return;
            }
            if (_instance._selftestStage == 1)
            {
                _instance.FinishSelftest();
            }
            else if (!_instance._running)
            {
                _instance.Begin();
            }
        }

        // selftest=same|copy: the session-start path without Steam. With a 2-merc party, save, then
        // reload either the same slot (what the host does) or a copy of its files in slot 9 (what the
        // client gets). Both must come back with the same 2-merc party and state.
        private int _selftestStage;

        private void StartSelftest(State state, string variant)
        {
            int slot = state.Get<SavedGameMetadata>().Slot;
            state.Get<SaveManager>().SaveGame();
            int target = slot;
            if (variant == "copy")
            {
                target = Net.SlotFiles.ClientSlot;
                Net.SlotFiles.Write(target, Net.SlotFiles.Read(slot));
            }
            Append($"selftest {variant}: saved slot {slot} with party {CoopSession.Party.Count}; "
                + $"before {StateHash.Describe(state.Get<Creatures>(), state.Get<RaidMetadata>().TurnNumber)}; reloading slot {target}");
            _selftestStage = 1;
            StartCoroutine(Net.NetSession.ReloadSlot(target));
        }

        // selftest=enemies: the multiplier's rules, and the game's own unit generator spending about
        // twice the points with it (averaged, since groups are drawn at random).
        private static string EnemySelftest()
        {
            List<string> report = new List<string>();
            bool ok = true;
            void Check(bool condition, string what)
            {
                report.Add((condition ? "ok " : "FAILED ") + what);
                ok &= condition;
            }
            State state = CoopSession.State;
            Check(CoopSession.Active, "two mercs deployed");
            try
            {
                EnemyScaling.Override = 2f;
                Check(EnemyScaling.Multiplier("any") == 2f, "x2 during the mission");
                EnemyScaling.BeginGenerationForTest(2f, "allies");
                Check(EnemyScaling.Multiplier("enemies") == 2f && EnemyScaling.Multiplier("allies") == 1f, "generation doubles enemies, not allies");
                EnemyScaling.EndGeneration();

                string location = Data.LocationUnitDrop.Ids.FirstOrDefault(id => Data.LocationUnitDrop.Get(id)?.Count > 0);
                Check(location != null, "a unit table: " + location);
                float Spend(float multiplier)
                {
                    EnemyScaling.Override = multiplier;
                    float total = 0f;
                    for (int i = 0; i < 40; i++)
                    {
                        total += UnitGenerationSystem.GenerateUnitsByPoints(state.Get<Statistics>(), state.Get<Difficulty>(), new List<string> { location }, null, 60f, Data.Global.MaxTechLevel).Sum(u => u.Points);
                    }
                    return total / 40f;
                }
                UnityEngine.Random.State saved = UnityEngine.Random.state;
                float single = Spend(1f), doubled = Spend(2f);
                UnityEngine.Random.state = saved;
                float ratio = single > 0f ? doubled / single : 0f;
                Check(ratio > 1.6f && ratio < 2.4f, $"points spent per call: x1 {single:0.0}, x2 {doubled:0.0} (ratio {ratio:0.00})");
            }
            catch (Exception e)
            {
                Check(false, "exception " + e);
            }
            finally
            {
                EnemyScaling.Override = null;
                EnemyScaling.EndGeneration();
            }
            return (ok ? "PASS " : "FAIL ") + string.Join("; ", report);
        }

        private void FinishSelftest()
        {
            State state = CoopSession.State;
            Append($"selftest after: party {CoopSession.Party.Count} {StateHash.Describe(state.Get<Creatures>(), state.Get<RaidMetadata>().TurnNumber)}");
            _selftestStage = 2;
            Finish(CoopSession.Party.Count == 2 ? "DONE selftest party restored" : $"FAIL selftest party {CoopSession.Party.Count}");
        }

        private void Begin()
        {
            State state = CoopSession.State;
            if (CoopSession.Party.Count < 2)
            {
                Mercenary second = state.Get<Mercenaries>().Values.FirstOrDefault(m =>
                    m != null && m != CoopSession.Primary.Mercenary && m.CreatureData?.Health != null && m.CreatureData.Health.Alive);
                if (second != null && CoopSession.FindFreeCellNear(CoopSession.Primary.CreatureData.Position, out CellPosition pos))
                {
                    CoopSession.AddMerc(second, pos);
                }
            }

            string selftest = Get("selftest", "");
            if (selftest == "enemies")
            {
                string result = EnemySelftest();
                Append("enemy selftest: " + result);
                Finish(result.StartsWith("PASS") ? "DONE enemy selftest passed" : "FAIL enemy selftest");
                return;
            }
            if (selftest == "ship")
            {
                Player partner = CoopSession.Party.FirstOrDefault(p => p != CoopSession.Primary);
                string result = partner?.Mercenary == null ? "FAIL no partner merc" : Net.NetSession.ShipSelftest(partner.Mercenary);
                Append("ship selftest: " + result);
                Finish(result.StartsWith("PASS") ? "DONE ship selftest passed" : "FAIL ship selftest");
                return;
            }
            if (selftest.Length > 0)
            {
                StartSelftest(state, selftest);
                return;
            }

            int seed = int.Parse(Get("seed", "424242"), CultureInfo.InvariantCulture);
            UnityEngine.Random.InitState(seed);
            GameplayRng.Reset(seed);
            _botRng = new System.Random(seed);
            // inv=N: every Nth player action point starts with an inventory/health screen action.
            _inventoryEvery = int.Parse(Get("inv", "0"), CultureInfo.InvariantCulture);
            if (_inventoryEvery > 0)
            {
                _inventoryBot = new InventoryBot(Append, _botRng);
            }
            // stn=N: every Nth player action point (offset from inv) presses a station button.
            // exitat=N: leave by shuttle once N player action points have passed.
            _stationEvery = int.Parse(Get("stn", "0"), CultureInfo.InvariantCulture);
            if (_stationEvery > 0)
            {
                _stationBot = new StationBot(Append);
            }
            _exitAt = int.Parse(Get("exitat", "0"), CultureInfo.InvariantCulture);
            _abortAt = int.Parse(Get("abortat", "0"), CultureInfo.InvariantCulture);
            // hold=N-M: hold the partner from player action point N, release it at M (0 = never).
            string[] hold = Get("hold", "0-0").Split('-');
            _holdAt = int.Parse(hold[0], CultureInfo.InvariantCulture);
            _releaseAt = hold.Length > 1 ? int.Parse(hold[1], CultureInfo.InvariantCulture) : 0;
            // treat=N: every Nth player action point, fixate a wound on an adjacent party merc
            // through the Treat health screen, with a medkit from the acting merc's own inventory.
            _treatEvery = int.Parse(Get("treat", "0"), CultureInfo.InvariantCulture);
            FinishGameProbe.Reason = null;
            Append($"stations {StationBot.Inventory(state)}");

            _turnController = state.Get<TurnController>();
            SpawnTestMonsters(state);
            GiveTestGear(state);

            // net=record|play routes the bot's inputs through the real netcode via a file.
            string net = Get("net", "none");
            if (net != "none")
            {
                Net.NetSession.Loopback mode = net == "record" ? Net.NetSession.Loopback.Record : Net.NetSession.Loopback.Play;
                Net.NetSession.StartLoopback(mode, Path.Combine(Path.GetDirectoryName(_logPath), Get("netfile", "loopback.bin")), seed);
                _botEnabled = mode == Net.NetSession.Loopback.Record;
            }
            _turnController.OnActionPointsProcessed += OnActionPointProcessed;
            _running = true;
            _lastApTime = Time.realtimeSinceStartup;
            Log.Info($"LOCKSTEP started: party {CoopSession.Party.Count}, seed {seed}, scoped rng {GameplayRng.Enabled}.");
            Append($"start party={CoopSession.Party.Count} {StateHash.Describe(state.Get<Creatures>(), state.Get<RaidMetadata>().TurnNumber)}");
        }

        // Fixed enemy types at fixed offsets from the primary merc, spawned after seeding so their
        // gear rolls are the same every run. mobs=soldier,security,... in the config.
        private void SpawnTestMonsters(State state)
        {
            string[] mobs = Get("mobs", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            int distance = int.Parse(Get("mobdist", "7"), CultureInfo.InvariantCulture);
            int[][] offsets = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 }, new[] { 1, 1 }, new[] { -1, -1 }, new[] { 1, -1 }, new[] { -1, 1 } };
            CellPosition origin = CoopSession.Primary.CreatureData.Position;
            for (int i = 0; i < mobs.Length; i++)
            {
                int[] o = offsets[i % offsets.Length];
                CellPosition target = new CellPosition(origin.X + o[0] * distance, origin.Y + o[1] * distance);
                if (!CoopSession.FindFreeCellNear(target, out CellPosition pos))
                {
                    Append($"spawn {mobs[i]} failed near {target.X},{target.Y}");
                    continue;
                }
                Monster monster = CreatureSystem.SpawnMonsterFromMobClass(state.Get<Mercenaries>(), state.Get<PerkFactory>(), state.Get<Difficulty>(),
                    state.Get<Creatures>(), state.Get<RaidMetadata>(), _turnController, mobs[i].Trim(), pos, -1, spawnEquipment: true, -1, isLeader: false, 5);
                Append($"spawn {mobs[i]} id={monster?.CreatureData.UniqueId} at {pos.X},{pos.Y}");
            }
        }

        // gear=id,id,...: every party merc gets these items (so the inventory bot has something to
        // work with). corpse=mob: a monster of that class dies next to the primary merc, to loot.
        private void GiveTestGear(State state)
        {
            foreach (string id in Get("gear", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (Player p in CoopSession.Party)
                {
                    BasePickupItem item = SingletonMonoBehaviour<ItemFactory>.Instance.CreateForInventory(id.Trim());
                    bool taken = item != null && p.CreatureData.Inventory.TakeOrEquip(item, putIfSlotBusy: true);
                    Append($"gear P{p.CreatureData.UniqueId} {id}: {(taken ? "ok" : "failed")}");
                }
            }
            // hp=N: every party merc starts at N health (to drive deaths and the mission end).
            int hp = int.Parse(Get("hp", "0"), CultureInfo.InvariantCulture);
            if (hp > 0)
            {
                foreach (Player p in CoopSession.Party)
                {
                    p.CreatureData.Health.Value = hp;
                }
            }
            if (_treatEvery > 0 && CoopSession.Party.Count > 1)
            {
                // The partner gets wounds (as the game's rndwound command does) and every merc gets
                // a few kinds of medkit that fit the partner's body (organic or augmented).
                Player partner = CoopSession.Party[1];
                for (int i = 0; i < 3; i++)
                {
                    string slot = WoundSystem.GetRandomizedSlot(partner.CreatureData.WoundSlotMap, new List<string>());
                    List<string> damageTypes = new List<string>(Data.DamageTypes.Ids);
                    WoundSystem.AddWound(slot, WoundSystem.GetWoundTypeIdByDmgType(damageTypes[UnityEngine.Random.Range(0, damageTypes.Count)]), partner, WoundCategory.Normal);
                    Append($"wound P{partner.CreatureData.UniqueId} {slot}");
                }
                HashSet<string> natures = new HashSet<string>(partner.CreatureData.WoundSlotMap.Keys.Select(k => Data.WoundSlots.GetRecord(k)?.NatureType).Where(n => n != null));
                IDictionary records = Traverse.Create(Data.Items).Field("_records").GetValue<IDictionary>();
                foreach (string id in records.Keys.Cast<string>().OrderBy(k => k, StringComparer.Ordinal)
                    .Where(k => Data.Items.GetSimpleRecord<FixationMedicineRecord>(k)?.NatureTypes?.Any(natures.Contains) == true).Take(3))
                {
                    foreach (Player p in CoopSession.Party)
                    {
                        BasePickupItem item = SingletonMonoBehaviour<ItemFactory>.Instance.CreateForInventory(id);
                        bool taken = item != null && p.CreatureData.Inventory.TakeOrEquip(item, putIfSlotBusy: true);
                        Append($"gear P{p.CreatureData.UniqueId} {id}: {(taken ? "ok" : "failed")}");
                    }
                }
            }
            // The station bot's battery button needs batteries; hurt mercs and drained stations
            // give its other buttons something to do.
            string battery = Data.Global.ValidRechargeBatteries?.FirstOrDefault();
            if (_stationEvery > 0)
            {
                foreach (Player p in CoopSession.Party)
                {
                    p.CreatureData.Health.Value = p.CreatureData.Health.Value * 6 / 10;
                }
                foreach (MapObstacle o in state.Get<MapObstacles>().Obstacles)
                {
                    UsesEnergy energy = o == null ? null : o.GetComponent<UsesEnergy>();
                    if (energy != null)
                    {
                        energy.Energy = 1;
                    }
                }
            }
            if (_stationEvery > 0 && battery != null)
            {
                foreach (Player p in CoopSession.Party)
                {
                    BasePickupItem item = SingletonMonoBehaviour<ItemFactory>.Instance.CreateForInventory(battery);
                    if (item != null)
                    {
                        item.StackCount = 10;
                    }
                    bool taken = item != null && p.CreatureData.Inventory.TakeOrEquip(item, putIfSlotBusy: true);
                    Append($"gear P{p.CreatureData.UniqueId} {battery}x10: {(taken ? "ok" : "failed")}");
                }
            }
            string corpse = Get("corpse", "");
            if (corpse.Length > 0 && CoopSession.FindFreeCellNear(CoopSession.Primary.CreatureData.Position, out CellPosition pos))
            {
                Monster monster = CreatureSystem.SpawnMonsterFromMobClass(state.Get<Mercenaries>(), state.Get<PerkFactory>(), state.Get<Difficulty>(),
                    state.Get<Creatures>(), state.Get<RaidMetadata>(), _turnController, corpse, pos, -1, spawnEquipment: true, -1, isLeader: false, 5);
                if (monster != null)
                {
                    monster.CreatureData.Health.SetInvulnerability(val: false);
                    monster.CreatureData.Health.ReasonOfDeath = HealthInfo.DeathReason.Exploded;
                    monster.CreatureData.Health.Value = 0;
                }
                Append($"corpse {corpse} id={monster?.CreatureData.UniqueId} at {pos.X},{pos.Y}");
            }
        }

        private void OnActionPointProcessed(ITurnable turnable)
        {
            State state = CoopSession.State;
            if (!_running || state == null)
            {
                return;
            }
            _apSeq++;
            _attemptsThisAp = 0;
            // rngtrace=from-to: log simulation scopes that drew random numbers in that AP range.
            string[] trace = Get("rngtrace", "").Split('-');
            if (trace.Length == 2)
            {
                bool on = _apSeq >= int.Parse(trace[0], CultureInfo.InvariantCulture) && _apSeq <= int.Parse(trace[1], CultureInfo.InvariantCulture);
                GameplayRng.Trace = on ? (Action<string>)(line => Append($"  ap{_apSeq} f{Time.frameCount} {line}")) : null;
            }
            _lastApTime = Time.realtimeSinceStartup;
            string who = turnable is Creature c ? $"{(turnable is Player ? "P" : "M")}{c.CreatureData.UniqueId}" : turnable?.GetType().Name ?? "null";
            string description = StateHash.Describe(state.Get<Creatures>(), state.Get<RaidMetadata>().TurnNumber, state.Get<MapObstacles>());
            Append($"{_apSeq} {who} {StateHash.Hash(description)} {description}{JsonDigest()}");
            if (turnable is Player)
            {
                _playerAps++;
            }
        }

        // jsondump=1: per party merc, a hash of every carried storage in save format (catches item
        // differences the regular fingerprint doesn't look at). Random state is restored afterwards.
        private string JsonDigest()
        {
            if (Get("jsondump", "0") != "1")
            {
                return "";
            }
            UnityEngine.Random.State rng = UnityEngine.Random.state;
            try
            {
                return " json" + string.Concat(CoopSession.Party.Select(p => ":" + p.CreatureData.UniqueId + "=" + StateHash.Hash(
                    string.Join("|", p.CreatureData.Inventory.AllContainers.Select(c => c == null ? "-" : SaveToJSON.CreateNode(c).ToString()))).Substring(0, 8)));
                // (Weapon InstanceIds from the save are the same on both runs; test gear gets numbered ones.)
            }
            finally
            {
                UnityEngine.Random.state = rng;
            }
        }

        private void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _startTime > 900f)
            {
                Finish("TIMEOUT (15 min)");
                return;
            }
            if (_running && FinishGameProbe.Reason != null)
            {
                Finish($"DONE level finished ({FinishGameProbe.Reason}) after {_playerAps} player APs, {_apSeq} total APs");
                return;
            }
            if (!_running || !CoopSession.InDungeon)
            {
                return;
            }
            if (_playerAps >= _maxPlayerAps)
            {
                Finish($"DONE {_playerAps} player APs, {_apSeq} total APs");
                return;
            }
            if (!CoopSession.AnyAlive())
            {
                Finish("DONE party dead");
                return;
            }
            // Playback has nothing to wait for but the recording, so give up sooner.
            if (now - _lastApTime > (Get("net", "none") == "play" ? 25f : 60f))
            {
                var views = Traverse.Create(SingletonMonoBehaviour<UI>.Instance).Field("_showingViews").GetValue<IEnumerable>();
                string showing = string.Join(",", views.Cast<object>().Select(v => Traverse.Create(v).Field("View").GetValue()?.GetType().Name));
                Finish($"STALL no AP for {now - _lastApTime:0}s; current={_turnController.CurrentProcessable?.GetType().Name} showing=[{showing}]");
                return;
            }
            if (_botEnabled)
            {
                DriveBot();
            }
        }

        // One decision per frame while it's a player AP: shoot the nearest visible enemy, else step
        // toward the nearest enemy (with some bot-RNG wandering), else skip. Gives up on the AP after
        // a few attempts that didn't consume it.
        private void DriveBot()
        {
            State state = CoopSession.State;
            Creatures creatures = state.Get<Creatures>();
            PlayerCommandQueue queue = state.Get<PlayerCommandQueue>();
            if (!TurnSystem.CanProcessPlayerTurn(_turnController, state.Get<TurnMetadata>(), creatures) || !creatures.Player.IsProcessing || !queue.Empty
                || UI.IsAnyShowing(typeof(DungeonHudScreen)))
            {
                return;
            }
            Player player = creatures.Player;
            if (Hold.IsHeld(player))
            {
                return;
            }
            if (_holdAt > 0 && _playerAps >= _holdAt)
            {
                _holdAt = 0;
                Append($"bot hold: {Net.NetSession.RequestHold(true)}");
            }
            if (_releaseAt > 0 && _playerAps >= _releaseAt)
            {
                _releaseAt = 0;
                Append($"bot release: {Net.NetSession.RequestHold(false)}");
            }
            if (_treatEvery > 0 && _treatDoneAt != _apSeq && _playerAps % _treatEvery == 0)
            {
                _treatDoneAt = _apSeq;
                string treated = TreatBot(player);
                if (treated != null)
                {
                    Append($"bot P{player.CreatureData.UniqueId} treat {treated}");
                    return;
                }
            }
            if (_inventoryBot != null && _inventoryDoneAt != _apSeq && _playerAps % _inventoryEvery == 0)
            {
                _inventoryDoneAt = _apSeq;
                _inventoryBot.Act(state, player);
                return;
            }
            if (_stationBot != null && _stationDoneAt != _apSeq && (_playerAps + 1) % _stationEvery == 0)
            {
                _stationDoneAt = _apSeq;
                _stationBot.Act(state, player);
                return;
            }
            if (_exitAt > 0 && _playerAps >= _exitAt && !_exitPressed)
            {
                _exitPressed = true;
                Append($"bot P{player.CreatureData.UniqueId} {StationBot.Exit()}");
                return;
            }
            if (_abortAt > 0 && _playerAps >= _abortAt && !_exitPressed)
            {
                _exitPressed = true;
                Append($"bot P{player.CreatureData.UniqueId} {AbortBot.Abort()}");
                return;
            }
            if (++_attemptsThisAp > 3)
            {
                Append($"bot skip P{player.CreatureData.UniqueId}");
                _attemptsThisAp = 0;
                player.SkipTurn();
                return;
            }

            CellPosition me = player.CreatureData.Position;
            List<Creature> enemies = creatures.Monsters
                .Where(m => m != null && m.CreatureData.Health.Alive && !m.IsAlly(player))
                .OrderBy(m => Chebyshev(me, m.CreatureData.Position)).ThenBy(m => m.CreatureData.UniqueId)
                .ToList();
            Creature visible = enemies.FirstOrDefault(m => m.IsSeenByPlayer);
            if (visible != null && _attemptsThisAp == 1)
            {
                CellPosition t = visible.CreatureData.Position;
                Append($"bot P{player.CreatureData.UniqueId} shoot {t.X},{t.Y}");
                queue.Add(new ShootCommand(t.X, t.Y));
                return;
            }

            List<CellPosition> steps = new List<CellPosition>();
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    CellPosition n = new CellPosition(me.X + dx, me.Y + dy);
                    if ((dx != 0 || dy != 0) && CoopSession.WhyCellBlocked(n, allowUnexplored: true) == null)
                    {
                        steps.Add(n);
                    }
                }
            }
            if (steps.Count == 0)
            {
                return;
            }
            CellPosition step;
            if (enemies.Count > 0 && _botRng.NextDouble() < 0.7)
            {
                CellPosition goal = enemies[0].CreatureData.Position;
                step = steps.OrderBy(s => Chebyshev(s, goal)).ThenBy(s => s.X).ThenBy(s => s.Y).First();
            }
            else
            {
                step = steps[_botRng.Next(steps.Count)];
            }
            Append($"bot P{player.CreatureData.UniqueId} move {step.X},{step.Y}");
            queue.Add(new MoveCommand(step.X, step.Y));
        }

        // Opens the health screen on the first adjacent party merc with a wound to fixate and applies
        // a fitting medkit from the healer's inventory. Null if there was nobody to treat.
        private static string TreatBot(Player healer)
        {
            foreach (Player patient in Treat.Candidates().ToList())
            {
                Treat.Open(patient);
                try
                {
                    HealthScreen health = UI.Get<HealthScreen>();
                    foreach (WoundSlot slot in Traverse.Create(health).Field("_woundSlots").GetValue<List<WoundSlot>>().Where(s => s.HasWoundsToFixate))
                    {
                        BasePickupItem medkit = healer.CreatureData.Inventory.Storages.SelectMany(s => s.Items)
                            .FirstOrDefault(i => i.Record<FixationMedicineRecord>()?.NatureTypes.Contains(slot.WoundSlotNature) == true);
                        if (medkit == null)
                        {
                            continue;
                        }
                        string before = $"{Unfixated(patient)}/{Unfixated(healer)}";
                        health.StartApplyMedkitMode(medkit, HealthScreen.ApplyMedkitMode.Fixate, spendTurn: true);
                        AccessTools.Method(typeof(HealthScreen), "WoundSlotOnClicked").Invoke(health, new object[] { slot });
                        return $"P{patient.CreatureData.UniqueId} {slot.WoundSlotId} with {medkit.Id}; unfixated patient/healer {before} -> {Unfixated(patient)}/{Unfixated(healer)}";
                    }
                }
                finally
                {
                    UI.Hide<HealthScreen>();
                }
            }
            return null;
        }

        private static int Unfixated(Player p) => p.CreatureData.EffectsController.OfType<BodyPartWound>().Count(w => !w.IsFixated);

        private static int Chebyshev(CellPosition a, CellPosition b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        private void Append(string line)
        {
            File.AppendAllText(_logPath, line + "\n");
        }

        private void Finish(string reason)
        {
            _running = false;
            Append("# " + reason);
            Log.Info("LOCKSTEP " + reason);
            Application.Quit();
            enabled = false;
        }
    }
}
