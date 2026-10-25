using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MGSC;
using QuasimorphCoop.Lockstep;

namespace QuasimorphCoop.Net
{
    // Inventory and health actions whose effects go beyond item storages (eating, medkits, repairs,
    // reloads, the "an action was taken" bookkeeping). They are sent as calls and replayed on the
    // other machine; storage changes in between travel as ItemSync payloads.
    internal enum InventoryOp : byte
    {
        Items = 1,     // ItemSync changes
        Consumable,    // item, spendTurn            InventoryScreen.PutConsumableInCharacter
        Device,        // item, spendTurn            InventoryScreen.ExecuteDeviceUsage
        ExpGainer,     // item, spendTurn            InventoryScreen.InteractWithCharacter
        RemoveFire,    // item, spendTurn
        Disassemble,   // item, count, spendTurn
        Spill,         // item, count
        Repair,        // target, repair kit
        StartReload,   // weapon, ammo
        Propagate,     // PlayerActionHappened       EffectsController.PropagateAction
        EndTurnCall,   // PlayerEndTurnContext       PlayerInteractionSystem.EndPlayerTurn
        TurnEnd,       // the action point ended outside the simulation (inventory closed, corpse opened)
        Wound          // wound slot id, mode, item, spendTurn   HealthScreen.WoundSlotOnClicked
    }

    internal static class InventoryOps
    {
        public static string Describe(InventoryOp op, byte[] data) => op == InventoryOp.Items ? "Items[" + ItemSync.Describe(data) + "]" : op.ToString();

        public static void Execute(State state, InventoryOp op, byte[] data)
        {
            if (op == InventoryOp.Items)
            {
                ItemSync.Apply(state, data);
                return;
            }
            Creatures creatures = state.Get<Creatures>();
            Player player = creatures.Player;
            InventoryScreen inventory = UI.Get<InventoryScreen>();
            GameplayRng.Enter();
            try
            {
                using (BinaryReader r = new BinaryReader(new MemoryStream(data)))
                {
                    switch (op)
                    {
                        case InventoryOp.Consumable:
                            inventory.PutConsumableInCharacter(ItemSync.ReadItem(r, state), r.ReadBoolean());
                            break;
                        case InventoryOp.Device:
                            AccessTools.Method(typeof(InventoryScreen), "ExecuteDeviceUsage").Invoke(inventory, new object[] { ItemSync.ReadItem(r, state), r.ReadBoolean() });
                            break;
                        case InventoryOp.ExpGainer:
                            inventory.InteractWithCharacter(ItemSync.ReadItem(r, state), r.ReadBoolean());
                            break;
                        case InventoryOp.RemoveFire:
                            inventory.RemoveFire(ItemSync.ReadItem(r, state), r.ReadBoolean());
                            break;
                        case InventoryOp.Disassemble:
                            inventory.DisassembleItem(ItemSync.ReadItem(r, state), r.ReadInt16(), r.ReadBoolean(), playSound: false);
                            break;
                        case InventoryOp.Spill:
                            inventory.SpillOnTheFloor(ItemSync.ReadItem(r, state), r.ReadInt16());
                            break;
                        case InventoryOp.Repair:
                            Repair(state, player, ItemSync.ReadItem(r, state), ItemSync.ReadItem(r, state));
                            break;
                        case InventoryOp.StartReload:
                            player.StartReload(ItemSync.ReadItem(r, state), ItemSync.ReadItem(r, state));
                            break;
                        case InventoryOp.Propagate:
                            player.CreatureData.EffectsController.PropagateAction((PlayerActionHappened)r.ReadInt32());
                            break;
                        case InventoryOp.EndTurnCall:
                            PlayerInteractionSystem.EndPlayerTurn(creatures, (PlayerEndTurnContext)r.ReadInt32());
                            break;
                        case InventoryOp.TurnEnd:
                            player.AnyActionProcessedFlag = true;
                            player.IsProcessing = false;
                            break;
                        case InventoryOp.Wound:
                            Wound(player, r.ReadString(), (HealthScreen.ApplyMedkitMode)r.ReadInt32(), ItemSync.ReadItem(r, state), r.ReadBoolean());
                            break;
                        default:
                            throw new InvalidDataException("Unknown inventory op " + op);
                    }
                }
            }
            finally
            {
                GameplayRng.Exit();
            }
            ItemSync.RefreshScreens();
        }

        // DragController.RepairOtherSlot without the UI. Storage side effects (a used-up kit, a
        // weapon broken into scrap) arrive separately as storage changes.
        private static void Repair(State state, Player player, BasePickupItem target, BasePickupItem kit)
        {
            Mercenary mercenary = player.Mercenary;
            if (ItemInteractionSystem.Repair(target, kit, mercenary.CreatureData.Inventory, out bool _))
            {
                PerkFactory perks = state.Get<PerkFactory>();
                CreatureSystem.RefreshResists(mercenary.CreatureData, mercenary);
                PerkSystem.RaisePerkAction(PerkLevelUpActionType.RepairItem, mercenary, perks);
                if (ItemInteractionSystem.IsRitualItem(kit))
                {
                    PerkSystem.RaisePerkAction(PerkLevelUpActionType.UseRitualItem, mercenary, perks);
                }
            }
            else
            {
                CreatureSystem.RefreshResists(mercenary.CreatureData, mercenary);
            }
        }

        // Clicks a wound slot on this machine's (possibly hidden) health screen, set up the way the
        // other player's screen was.
        private static void Wound(Player player, string slotId, HealthScreen.ApplyMedkitMode mode, BasePickupItem item, bool spendTurn)
        {
            HealthScreen screen = UI.Get<HealthScreen>();
            AccessTools.Method(typeof(HealthScreen), "RefreshWoundSlots").Invoke(screen, new object[] { player.CreatureData });
            screen.StartApplyMedkitMode(item, mode, spendTurn);
            List<WoundSlot> slots = Traverse.Create(screen).Field("_woundSlots").GetValue<List<WoundSlot>>();
            WoundSlot slot = slots.FirstOrDefault(s => s.WoundSlotId == slotId) ?? throw new InvalidDataException("No wound slot " + slotId);
            AccessTools.Method(typeof(HealthScreen), "WoundSlotOnClicked").Invoke(screen, new object[] { slot });
        }
    }

    // ------------------------------------------------------------------------ capture patches
    //
    // Each prefix asks NetSession whether to record (the local player's merc is waiting for input
    // and this is the outermost UI-initiated call), to block (the merc on screen belongs to the
    // other player) or to let it through untouched (not in a session, inside the simulation, or a
    // replay). Recorded calls run inside the gameplay RNG scope, like their replay.

    internal static class OpPatch
    {
        public static bool Begin(InventoryOp op, Action<BinaryWriter, State> args, out bool recorded) => NetSession.BeginOp(op, args, out recorded);

        public static Exception End(Exception exception, bool recorded)
        {
            if (recorded)
            {
                NetSession.EndOp();
            }
            return exception;
        }
    }

    [HarmonyPatch(typeof(InventoryScreen), nameof(InventoryScreen.PutConsumableInCharacter))]
    internal static class ConsumableOpPatch
    {
        private static bool Prefix(BasePickupItem item, bool spendTurn, out bool __state) =>
            OpPatch.Begin(InventoryOp.Consumable, (w, s) => { ItemSync.WriteItem(w, s, item); w.Write(spendTurn); }, out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(InventoryScreen), "ExecuteDeviceUsage")]
    internal static class DeviceOpPatch
    {
        private static bool Prefix(BasePickupItem item, bool spendTurn, out bool __state) =>
            OpPatch.Begin(InventoryOp.Device, (w, s) => { ItemSync.WriteItem(w, s, item); w.Write(spendTurn); }, out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    // Only experience gainers are replayed through InteractWithCharacter; its other branches end up
    // in patched calls (consumables, devices behind a confirm dialog, placeables as a command).
    [HarmonyPatch(typeof(InventoryScreen), nameof(InventoryScreen.InteractWithCharacter))]
    internal static class ExpGainerOpPatch
    {
        private static bool Prefix(BasePickupItem item, bool spendTurn, out bool __state)
        {
            __state = false;
            if (item == null || !item.Is<ExpGainerRecord>())
            {
                return true;
            }
            return OpPatch.Begin(InventoryOp.ExpGainer, (w, s) => { ItemSync.WriteItem(w, s, item); w.Write(spendTurn); }, out __state);
        }

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(InventoryScreen), nameof(InventoryScreen.RemoveFire))]
    internal static class RemoveFireOpPatch
    {
        private static bool Prefix(BasePickupItem item, bool spendTurn, out bool __state) =>
            OpPatch.Begin(InventoryOp.RemoveFire, (w, s) => { ItemSync.WriteItem(w, s, item); w.Write(spendTurn); }, out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(InventoryScreen), nameof(InventoryScreen.DisassembleItem))]
    internal static class DisassembleOpPatch
    {
        private static bool Prefix(BasePickupItem item, short disassembleCount, bool spendTurn, out bool __state) =>
            OpPatch.Begin(InventoryOp.Disassemble, (w, s) => { ItemSync.WriteItem(w, s, item); w.Write(disassembleCount); w.Write(spendTurn); }, out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(InventoryScreen), nameof(InventoryScreen.SpillOnTheFloor))]
    internal static class SpillOpPatch
    {
        private static bool Prefix(BasePickupItem item, short count, out bool __state) =>
            OpPatch.Begin(InventoryOp.Spill, (w, s) => { ItemSync.WriteItem(w, s, item); w.Write(count); }, out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(DragController), "RepairOtherSlot")]
    internal static class RepairOpPatch
    {
        private static bool Prefix(DragController __instance, ItemSlot otherSlot, out bool __state)
        {
            BasePickupItem target = otherSlot?.Item;
            BasePickupItem kit = __instance.DraggableItem;
            return OpPatch.Begin(InventoryOp.Repair, (w, s) => { ItemSync.WriteItem(w, s, target); ItemSync.WriteItem(w, s, kit); }, out __state);
        }

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(Player), nameof(Player.StartReload))]
    internal static class StartReloadOpPatch
    {
        private static bool Prefix(Player __instance, BasePickupItem weaponToReload, BasePickupItem ammo, out bool __state)
        {
            __state = false;
            if (__instance != CoopSession.Creatures?.Player)
            {
                return true;
            }
            return OpPatch.Begin(InventoryOp.StartReload, (w, s) => { ItemSync.WriteItem(w, s, weaponToReload); ItemSync.WriteItem(w, s, ammo); }, out __state);
        }

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(EffectsController), nameof(EffectsController.PropagateAction))]
    internal static class PropagateOpPatch
    {
        private static bool Prefix(EffectsController __instance, PlayerActionHappened actionHappened, out bool __state)
        {
            __state = false;
            if (__instance != CoopSession.Creatures?.Player?.CreatureData.EffectsController)
            {
                return true;
            }
            return OpPatch.Begin(InventoryOp.Propagate, (w, s) => w.Write((int)actionHappened), out __state);
        }

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(PlayerInteractionSystem), nameof(PlayerInteractionSystem.EndPlayerTurn))]
    internal static class EndTurnCallOpPatch
    {
        private static bool Prefix(PlayerEndTurnContext context, out bool __state) =>
            OpPatch.Begin(InventoryOp.EndTurnCall, (w, s) => w.Write((int)context), out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(HealthScreen), "WoundSlotOnClicked")]
    internal static class WoundOpPatch
    {
        private static bool Prefix(HealthScreen __instance, WoundSlot slot, out bool __state)
        {
            __state = false;
            Traverse t = Traverse.Create(__instance);
            if (slot == null || !t.Property("IsApplyMedkitMode").GetValue<bool>())
            {
                return true;
            }
            string slotId = slot.WoundSlotId;
            HealthScreen.ApplyMedkitMode mode = t.Field("_medkitMode").GetValue<HealthScreen.ApplyMedkitMode>();
            BasePickupItem item = t.Field("_medkitItem").GetValue<BasePickupItem>();
            bool spendTurn = t.Field("_spendTurnWhenFixWound").GetValue<bool>();
            return OpPatch.Begin(InventoryOp.Wound, (w, s) => { w.Write(slotId); w.Write((int)mode); ItemSync.WriteItem(w, s, item); w.Write(spendTurn); }, out __state);
        }

        private static Exception Finalizer(Exception __exception, bool __state) => OpPatch.End(__exception, __state);
    }

    // The game ends an action point outside the simulation when the inventory closes after it was
    // used, and when a corpse is opened. The owner's machine sends that as TurnEnd; the other
    // machine must not end the turn on its own (it would run ahead while the owner is still in
    // their inventory).
    [HarmonyPatch(typeof(Player), nameof(Player.IsProcessing), MethodType.Setter)]
    internal static class TurnEndPatch
    {
        private static bool Prefix(Player __instance, bool value) => value || NetSession.OnPlayerTurnEnding(__instance);
    }

    // That happens in DungeonGameMode.Update, outside the simulation scopes; other places that end
    // a turn outside them (animation callbacks) are reproduced by both machines on their own.
    [HarmonyPatch(typeof(DungeonGameMode), "Update")]
    internal static class DungeonUpdateScopePatch
    {
        private static void Prefix() => NetSession.InDungeonUpdate++;

        private static Exception Finalizer(Exception __exception)
        {
            NetSession.InDungeonUpdate--;
            return __exception;
        }
    }

    // Replayed screen methods run on screens that may be hidden on this machine; skip their refresh.
    [HarmonyPatch(typeof(InventoryScreen), nameof(InventoryScreen.RefreshItemsList))]
    internal static class HiddenInventoryRefreshPatch
    {
        private static bool Prefix(InventoryScreen __instance) => __instance.isActiveAndEnabled || !NetSession.Replaying;
    }

    // While the other player's merc is on screen, inventory and health screens show it read-only:
    // every UI action there checks CanProcessPlayerTurn first. (World input is gated separately.)
    [HarmonyPatch(typeof(TurnSystem), nameof(TurnSystem.CanProcessPlayerTurn))]
    internal static class ReadOnlyScreensPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (__result && NetSession.ScreensReadOnly)
            {
                __result = false;
            }
        }
    }
}
