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
        private int _apSeq;
        private int _playerAps;
        private float _lastApTime;
        private float _startTime;
        private int _attemptsThisAp;
        private TurnController _turnController;

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
            if (selftest.Length > 0)
            {
                StartSelftest(state, selftest);
                return;
            }

            int seed = int.Parse(Get("seed", "424242"), CultureInfo.InvariantCulture);
            UnityEngine.Random.InitState(seed);
            GameplayRng.Reset(seed);
            _botRng = new System.Random(seed);

            _turnController = state.Get<TurnController>();
            SpawnTestMonsters(state);

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

        private void OnActionPointProcessed(ITurnable turnable)
        {
            State state = CoopSession.State;
            if (!_running || state == null)
            {
                return;
            }
            _apSeq++;
            _attemptsThisAp = 0;
            _lastApTime = Time.realtimeSinceStartup;
            string who = turnable is Creature c ? $"{(turnable is Player ? "P" : "M")}{c.CreatureData.UniqueId}" : turnable?.GetType().Name ?? "null";
            string description = StateHash.Describe(state.Get<Creatures>(), state.Get<RaidMetadata>().TurnNumber);
            Append($"{_apSeq} {who} {StateHash.Hash(description)} {description}");
            if (turnable is Player)
            {
                _playerAps++;
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
            if (now - _lastApTime > 60f)
            {
                var views = Traverse.Create(SingletonMonoBehaviour<UI>.Instance).Field("_showingViews").GetValue<IEnumerable>();
                string showing = string.Join(",", views.Cast<object>().Select(v => Traverse.Create(v).Field("View").GetValue()?.GetType().Name));
                Finish($"STALL no AP for 60s; current={_turnController.CurrentProcessable?.GetType().Name} showing=[{showing}]");
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
            if (!TurnSystem.CanProcessPlayerTurn(_turnController, state.Get<TurnMetadata>(), creatures) || !queue.Empty
                || UI.IsAnyShowing(typeof(DungeonHudScreen)))
            {
                return;
            }
            Player player = creatures.Player;
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
