using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Net
{
    // Map station menus (autodoc, repair and recharge stations, scanners, workbenches) and the
    // shuttle. Opening one is a world command that both machines replay, but the buttons inside
    // are plain UI code, so each button's effect is sent as an InventoryOp and replayed through the
    // station's own methods. On the machine that is only watching, the menu the replayed command
    // opens is closed again right away: it belongs to the other player.
    internal static class StationOps
    {
        public static void Execute(State state, InventoryOp op, BinaryReader r)
        {
            Creatures creatures = state.Get<Creatures>();
            Player player = creatures.Player;
            switch (op)
            {
                case InventoryOp.HealHP:
                    Station<HealStation>(state, r).HealHP();
                    break;
                case InventoryOp.HealWounds:
                    Station<HealStation>(state, r).HealAllWounds();
                    break;
                case InventoryOp.Recharge:
                    Station<RechargeStation>(state, r).RechargeImplants();
                    break;
                case InventoryOp.RepairAll:
                    Station<RepairStation>(state, r).RepairItems();
                    break;
                case InventoryOp.Battery:
                    Battery(creatures, player, Obstacle(state, r.ReadInt32()));
                    break;
                case InventoryOp.Craft:
                    Craft(state, creatures, player, Obstacle(state, r.ReadInt32()), r.ReadString(), r.ReadInt32());
                    break;
                case InventoryOp.MissionExit:
                    AccessTools.Method(typeof(DungeonGameMode), "SpaceshipIntConOnSpaceshipUsed").Invoke(SingletonMonoBehaviour<DungeonGameMode>.Instance, Array.Empty<object>());
                    break;
                case InventoryOp.Abort:
                    AccessTools.Method(typeof(DungeonGameMode), "EscScreenOnAbortMission").Invoke(SingletonMonoBehaviour<DungeonGameMode>.Instance, new object[] { r.ReadBoolean() });
                    break;
            }
        }

        private static MapObstacle Obstacle(State state, int id) => state.Get<MapObstacles>().Get(id) ?? throw new InvalidDataException("No obstacle " + id);

        private static T Station<T>(State state, BinaryReader r) where T : MapObstacleComponent
        {
            MapObstacle obstacle = Obstacle(state, r.ReadInt32());
            return obstacle.GetComponent<T>() ?? throw new InvalidDataException($"Obstacle {obstacle.UniqueId} has no {typeof(T).Name}");
        }

        // BaseMapObstacleWindow's battery button without the window (plus the scanner window's
        // override of its hook).
        private static void Battery(Creatures creatures, Player player, MapObstacle obstacle)
        {
            UsesEnergy energy = obstacle.GetComponent<UsesEnergy>();
            Inventory inventory = player.CreatureData.Inventory;
            if (energy == null || energy.MaxEnergy == energy.Energy || !inventory.ContainsAnyOfSpecificAmmo(energy.BatteryIds, energy.RechargeCost))
            {
                return;
            }
            inventory.RemoveAnyOfSpecificAmmo(energy.BatteryIds, (short)energy.RechargeCost);
            energy.IncreaseEnergy(1);
            obstacle.GetComponent<StationScanner>()?.UpdateLocationMetadata();
            PlayerInteractionSystem.EndPlayerTurn(creatures, PlayerEndTurnContext.InventoryInteraction);
        }

        // WorkbenchWindow.ContextMenuOnCmdSelected without the window.
        private static void Craft(State state, Creatures creatures, Player player, MapObstacle obstacle, string receiptId, int count)
        {
            Workbench workbench = obstacle.GetComponent<Workbench>() ?? throw new InvalidDataException($"Obstacle {obstacle.UniqueId} has no workbench");
            WorkbenchReceiptRecord receipt = Data.WorkbenchReceipts.GetById(receiptId) ?? throw new InvalidDataException("No receipt " + receiptId);
            string workbenchId = Traverse.Create(workbench).Field("_workbenchId").GetValue<string>();
            Inventory inventory = player.CreatureData.Inventory;
            UsesEnergy energy = workbench.GetComponent<UsesEnergy>();
            foreach (ItemQuantity required in receipt.RequiredItems)
            {
                inventory.RemoveSpecificItem(required.ItemId, (short)(count * required.Count));
            }
            ItemsPrices prices = Global<ItemsPrices>(state);
            List<BasePickupItem> crafted = new List<BasePickupItem>(count);
            for (int i = 0; i < count; i++)
            {
                BasePickupItem item = SingletonMonoBehaviour<ItemFactory>.Instance.CreateForInventory(receipt.OutputItem);
                item.StackCount = 1;
                player.OnCraft(workbenchId, prices?.GetPrice(receipt.OutputItem) ?? 0f);
                crafted.Add(item);
            }
            foreach (BasePickupItem item in crafted)
            {
                if (!inventory.TakeOrEquip(item, putIfSlotBusy: true))
                {
                    ItemOnFloorSystem.SpawnItem(state.Get<ItemsOnFloor>(), state.Get<MapGrid>(), item, player.CreatureData.Position);
                }
            }
            UI.Get<DungeonHudScreen>().RefreshAllHud();
            if (energy != null)
            {
                energy.Energy--;
            }
            PlayerInteractionSystem.EndPlayerTurn(creatures, PlayerEndTurnContext.InventoryInteraction);
        }

        // Ship-side objects (prices, cargo) live in the game-wide state, not the level's.
        internal static T Global<T>(State state) where T : class => state.Get<T>() ?? NetSession.GetGlobalState()?.Get<T>();

        internal static void Write(BinaryWriter w, MapObstacleComponent station) => w.Write(station.MapObstacle.UniqueId);

        // OpPatch.Begin, telling the player why a button did nothing. Station windows can stay open
        // past the merc's action point, and their buttons don't check whose turn it is.
        internal static bool Begin(InventoryOp op, Action<BinaryWriter, State> args, out bool recorded)
        {
            bool allowed = OpPatch.Begin(op, args, out recorded);
            if (!allowed && !NetSession.WatchingOther)
            {
                NetSession.Notify("Co-op: station menus work on your merc's turn.");
            }
            return allowed;
        }
    }

    // ------------------------------------------------------------------------ capture patches

    [HarmonyPatch(typeof(HealStation), nameof(HealStation.HealHP))]
    internal static class HealHPOpPatch
    {
        private static bool Prefix(HealStation __instance, out bool __state) =>
            StationOps.Begin(InventoryOp.HealHP, (w, s) => StationOps.Write(w, __instance), out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(HealStation), nameof(HealStation.HealAllWounds))]
    internal static class HealWoundsOpPatch
    {
        private static bool Prefix(HealStation __instance, out bool __state) =>
            StationOps.Begin(InventoryOp.HealWounds, (w, s) => StationOps.Write(w, __instance), out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(RechargeStation), nameof(RechargeStation.RechargeImplants))]
    internal static class RechargeOpPatch
    {
        private static bool Prefix(RechargeStation __instance, out bool __state) =>
            StationOps.Begin(InventoryOp.Recharge, (w, s) => StationOps.Write(w, __instance), out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(RepairStation), nameof(RepairStation.RepairItems))]
    internal static class RepairAllOpPatch
    {
        private static bool Prefix(RepairStation __instance, out bool __state) =>
            StationOps.Begin(InventoryOp.RepairAll, (w, s) => StationOps.Write(w, __instance), out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(BaseMapObstacleWindow), "OnBatteryButtonClicked", typeof(CommonButton), typeof(int))]
    internal static class BatteryOpPatch
    {
        private static readonly AccessTools.FieldRef<BaseMapObstacleWindow, MapObstacle> ObstacleField = AccessTools.FieldRefAccess<BaseMapObstacleWindow, MapObstacle>("_obstacle");

        private static bool Prefix(BaseMapObstacleWindow __instance, out bool __state)
        {
            MapObstacle obstacle = ObstacleField(__instance);
            return StationOps.Begin(InventoryOp.Battery, (w, s) => w.Write(obstacle.UniqueId), out __state);
        }

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(WorkbenchWindow), "ContextMenuOnCmdSelected")]
    internal static class CraftOpPatch
    {
        private static bool Prefix(WorkbenchWindow __instance, out bool __state)
        {
            __state = false;
            Traverse t = Traverse.Create(__instance);
            WorkbenchReceiptPanel panel = t.Field("_workbenchReceiptToCraft").GetValue<WorkbenchReceiptPanel>();
            Workbench workbench = t.Field("_workbench").GetValue<Workbench>();
            Player player = CoopSession.Creatures?.Player;
            if (panel == null || workbench == null || player == null)
            {
                return true;
            }
            // The window refuses (with a sound) when parts or energy ran out; that needs no sending.
            panel.Refresh(player.CreatureData.Inventory);
            UsesEnergy energy = workbench.GetComponent<UsesEnergy>();
            if (!panel.HasCompsToCraft || (energy != null && energy.Energy <= 0))
            {
                return true;
            }
            string receiptId = panel.Receipt.Id;
            int count = UI.Get<CommonContextMenu>().SliderButtonSelectedValue;
            return StationOps.Begin(InventoryOp.Craft, (w, s) => { w.Write(workbench.MapObstacle.UniqueId); w.Write(receiptId); w.Write(count); }, out __state);
        }

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    // Leaving by shuttle ends the level for both: send it before this machine tears the level down.
    [HarmonyPatch(typeof(ShuttleWindow), "MissionExit")]
    internal static class MissionExitOpPatch
    {
        private static bool Prefix(out bool __state)
        {
            bool allowed = OpPatch.Begin(InventoryOp.MissionExit, (w, s) => { }, out __state);
            if (__state)
            {
                NetSession.Flush();
            }
            else if (!allowed)
            {
                NetSession.Notify("Co-op: you can leave by shuttle on your merc's turn.");
            }
            return allowed;
        }

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(DungeonGameMode), "EscScreenOnAbortMission")]
    internal static class AbortOpPatch
    {
        private static bool Prefix(bool isStationLeave, out bool __state)
        {
            __state = false;
            string blocked = NetSession.Replaying ? null : NetSession.AbortBlockedReason();
            if (blocked != null)
            {
                NetSession.Notify("Co-op: " + blocked);
                return false;
            }
            bool allowed = OpPatch.Begin(InventoryOp.Abort, (w, s) => w.Write(isStationLeave), out __state);
            if (__state)
            {
                NetSession.Flush();
            }
            else if (!allowed)
            {
                NetSession.Notify("Co-op: you can abort on your merc's turn.");
            }
            return allowed;
        }

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    // ------------------------------------------------------------------------ menus on the watching machine

    [HarmonyPatch]
    internal static class StationMenuWatcherPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(HealStation), nameof(MapObstacleComponent.DoInteraction));
            yield return AccessTools.Method(typeof(RepairStation), nameof(MapObstacleComponent.DoInteraction));
            yield return AccessTools.Method(typeof(RechargeStation), nameof(MapObstacleComponent.DoInteraction));
            yield return AccessTools.Method(typeof(StationScanner), nameof(MapObstacleComponent.DoInteraction));
            yield return AccessTools.Method(typeof(Workbench), nameof(MapObstacleComponent.DoInteraction));
            yield return AccessTools.Method(typeof(Shuttle), nameof(MapObstacleComponent.DoInteraction));
        }

        // The interaction itself still runs (the scanner updates what it reveals, a workbench may
        // roll its recipe); only the menu goes away.
        private static void Postfix()
        {
            if (!NetSession.WatchingOther)
            {
                return;
            }
            Hide<HealStationWindow>();
            Hide<RepairStationWindow>();
            Hide<RechargeStationWindow>();
            Hide<StationScannerWindow>();
            Hide<WorkbenchWindow>();
            Hide<ShuttleWindow>();
        }

        private static void Hide<T>() where T : UnityEngine.MonoBehaviour
        {
            if (UI.IsShowing<T>())
            {
                UI.Hide<T>();
            }
        }
    }
}
