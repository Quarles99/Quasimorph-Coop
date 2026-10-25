using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Lockstep
{
    // Test-only: presses station menu buttons (autodoc, repair, recharge, battery, workbench) through
    // the real window handlers, so record/playback covers StationOps. The stations can be anywhere
    // on the map; their effects only look at the merc in the slot.
    internal sealed class StationBot
    {
        private readonly Action<string> _log;
        private int _step;

        public StationBot(Action<string> log)
        {
            _log = log;
        }

        private static readonly string[] Actions = { "healhp", "battery", "repair", "healwounds", "recharge", "craft" };

        public static string Inventory(State state)
        {
            MapObstacles obstacles = state.Get<MapObstacles>();
            int Count<T>() where T : MapObstacleComponent => obstacles.Obstacles.Count(o => o != null && o.GetComponent<T>() != null);
            return $"heal={Count<HealStation>()} repair={Count<RepairStation>()} recharge={Count<RechargeStation>()} scanner={Count<StationScanner>()} workbench={Count<Workbench>()} shuttle={Count<Shuttle>()}";
        }

        public void Act(State state, Player player)
        {
            string action = Actions[_step++ % Actions.Length];
            string result;
            try
            {
                result = Do(action, state, player);
            }
            catch (Exception e)
            {
                result = "error " + e.GetType().Name + ": " + e.Message;
                Log.Warn("Station bot: " + e);
            }
            _log($"bot P{player.CreatureData.UniqueId} station {action}: {result}");
        }

        private static MapObstacle Find<T>(State state) where T : MapObstacleComponent =>
            state.Get<MapObstacles>().Obstacles.Where(o => o != null && o.GetComponent<T>() != null).OrderBy(o => o.UniqueId).FirstOrDefault();

        private static int Energy(MapObstacle o) => o.GetComponent<UsesEnergy>()?.Energy ?? -1;

        private string Do(string action, State state, Player player)
        {
            CreatureData d = player.CreatureData;
            switch (action)
            {
                case "healhp":
                case "healwounds":
                {
                    MapObstacle o = Find<HealStation>(state);
                    if (o == null)
                    {
                        return "no autodoc";
                    }
                    int hp = d.Health.Value, e = Energy(o);
                    Click<HealStationWindow>(w => w.Configure(o), action == "healhp" ? "OnHealHPClicked" : "OnHealWoundsClicked");
                    return $"hp {hp}->{d.Health.Value} energy {e}->{Energy(o)}";
                }
                case "repair":
                {
                    MapObstacle o = Find<RepairStation>(state);
                    if (o == null)
                    {
                        return "no repair station";
                    }
                    int e = Energy(o);
                    Click<RepairStationWindow>(w => w.Configure(o), "OnRepairClicked");
                    return $"energy {e}->{Energy(o)}";
                }
                case "recharge":
                {
                    MapObstacle o = Find<RechargeStation>(state);
                    if (o == null)
                    {
                        return "no recharge station";
                    }
                    int e = Energy(o);
                    Click<RechargeStationWindow>(w => w.Configure(o), "OnRechargeClicked");
                    return $"energy {e}->{Energy(o)}";
                }
                case "battery":
                {
                    MapObstacle o = state.Get<MapObstacles>().Obstacles
                        .Where(x => x != null && x.GetComponent<HealStation>() != null && x.GetComponent<UsesEnergy>() != null)
                        .OrderBy(x => x.UniqueId).FirstOrDefault();
                    if (o == null)
                    {
                        return "no powered autodoc";
                    }
                    int e = Energy(o);
                    UsesEnergy energy = o.GetComponent<UsesEnergy>();
                    string info = $"cost {energy.RechargeCost} max {energy.MaxEnergy} batteries {d.Inventory.ContainsAnyOfSpecificAmmo(energy.BatteryIds, energy.RechargeCost)}";
                    Click<HealStationWindow>(w => w.Configure(o), "OnBatteryButtonClicked", typeof(CommonButton), typeof(int));
                    return $"energy {e}->{Energy(o)} ({info})";
                }
                case "craft":
                {
                    MapObstacle o = Find<Workbench>(state);
                    if (o == null)
                    {
                        return "no workbench";
                    }
                    Workbench workbench = o.GetComponent<Workbench>();
                    string id = Traverse.Create(workbench).Field("_workbenchId").GetValue<string>();
                    WorkbenchWindow window = UI.Get<WorkbenchWindow>();
                    UI.Chain<WorkbenchWindow>().Invoke(v => v.Configure(id, o)).Show();
                    try
                    {
                        List<WorkbenchReceiptPanel> panels = Traverse.Create(window).Field("_receiptPanels").GetValue<List<WorkbenchReceiptPanel>>();
                        WorkbenchReceiptPanel panel = panels?.FirstOrDefault(p => p.HasCompsToCraft);
                        if (panel == null)
                        {
                            return $"nothing craftable ({panels?.Count ?? 0} receipts)";
                        }
                        AccessTools.Method(typeof(WorkbenchWindow), "OnReceiptPanelStartProduction").Invoke(window, new object[] { panel });
                        AccessTools.Method(typeof(WorkbenchWindow), "ContextMenuOnCmdSelected").Invoke(window, new object[] { 0 });
                        return $"crafted {panel.Receipt.OutputItem}";
                    }
                    finally
                    {
                        if (UI.IsShowing<CommonContextMenu>())
                        {
                            UI.Hide<CommonContextMenu>();
                        }
                        UI.Hide<WorkbenchWindow>();
                    }
                }
            }
            return "unknown";
        }

        private static void Click<T>(Action<T> configure, string handler, params Type[] signature) where T : BaseMapObstacleWindow
        {
            T window = UI.Get<T>();
            UI.Chain<T>().Invoke(configure).Show();
            try
            {
                Type[] args = signature.Length > 0 ? signature : new[] { typeof(CommonButton), typeof(int) };
                AccessTools.Method(typeof(T), handler, args).Invoke(window, new object[] { null, 0 });
            }
            finally
            {
                UI.Hide<T>();
            }
        }

        // exitat=N: leave by shuttle, through the shuttle window's own exit.
        public static string Exit()
        {
            AccessTools.Method(typeof(ShuttleWindow), "MissionExit").Invoke(UI.Get<ShuttleWindow>(), Array.Empty<object>());
            return "mission exit pressed";
        }
    }

    internal static class AbortBot
    {
        // abortat=N: abort the mission the way the pause menu's button does.
        public static string Abort()
        {
            AccessTools.Method(typeof(DungeonGameMode), "EscScreenOnAbortMission").Invoke(SingletonMonoBehaviour<DungeonGameMode>.Instance, new object[] { false });
            return "abort pressed";
        }
    }

    // Lets the harness see a level end however it ends. During a test the level isn't really
    // finished: that would save the result (an extraction, a dead merc) over the test slot.
    [HarmonyPatch(typeof(DungeonGameMode), "FinishGame")]
    internal static class FinishGameProbe
    {
        public static string Reason;

        private static bool Prefix(DungeonFinishedData dungeonFinishedData)
        {
            Reason ??= dungeonFinishedData.Reason.ToString();
            return !LockstepTest.Active;
        }
    }
}
