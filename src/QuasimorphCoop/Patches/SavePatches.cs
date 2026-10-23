using System;
using System.Linq;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Patches
{
    // Creatures.Player is the only saved player slot, so a save must always see the primary merc.
    // The other party mercs go into a sidecar file instead (see PartyPersistence).
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveGame))]
    internal static class SaveManagerSaveGamePatch
    {
        private static void Prefix(out Player __state)
        {
            __state = null;
            if (CoopSession.Active && CoopSession.Creatures.Player != CoopSession.Primary)
            {
                __state = CoopSession.SwapSlotSilently(CoopSession.Primary);
                Log.Info("Save: primary merc swapped into slot for the duration of the save.");
            }
        }

        private static void Postfix(Player __state)
        {
            if (__state != null)
            {
                CoopSession.SwapSlotSilently(__state);
            }
        }
    }

    [HarmonyPatch(typeof(SaveManager), "SaveDungeon")]
    internal static class SaveManagerSaveDungeonPatch
    {
        private static readonly AccessTools.FieldRef<SaveManager, State> StateRef = AccessTools.FieldRefAccess<SaveManager, State>("_state");

        private static void Postfix(SaveManager __instance, SavedGameMetadata metadata)
        {
            // Never let the sidecar break a real save.
            try
            {
                string locationId = StateRef(__instance).Get<LocationMetadata>().LocationId;
                PartyPersistence.Write(metadata, locationId);
            }
            catch (Exception e)
            {
                Log.Error("Failed to write party sidecar: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.LoadDungeon))]
    internal static class SaveManagerLoadDungeonPatch
    {
        private static readonly AccessTools.FieldRef<SaveManager, State> StateRef = AccessTools.FieldRefAccess<SaveManager, State>("_state");

        private static void Postfix(SaveManager __instance, string locationId, ELoadResult __result)
        {
            if (__result != ELoadResult.Success)
            {
                return;
            }
            // A party carried over from the previous floor wins over this floor's older sidecar.
            if (PartyPersistence.Pending != null)
            {
                Log.Info($"Loaded dungeon '{locationId}'; keeping carried-over party.");
                return;
            }
            try
            {
                PartyPersistence.Pending = PartyPersistence.Read(StateRef(__instance).Get<SavedGameMetadata>(), locationId);
            }
            catch (Exception e)
            {
                Log.Error("Failed to read party sidecar: " + e);
            }
        }
    }

    // Mission results (extraction, death, abort) are computed from Creatures.Player and MercenaryInRaid,
    // so collapse to the primary merc first. FinishGame then saves (exit to menu, next floor) with the
    // party already collapsed, so snapshot it for those saves; on a floor change carry it over.
    [HarmonyPatch(typeof(DungeonGameMode), "FinishGame")]
    internal static class DungeonGameModeFinishGamePatch
    {
        private static void Prefix()
        {
            if (CoopSession.Active)
            {
                PartyPersistence.FinishSnapshot = PartyPersistence.SnapshotLiveParty(withPositions: true);
                Log.Info("FinishGame: collapsing party to primary merc.");
                CoopSession.Collapse();
            }
        }

        private static void Postfix(DungeonFinishedData dungeonFinishedData)
        {
            if (PartyPersistence.FinishSnapshot == null)
            {
                return;
            }
            if (dungeonFinishedData.Reason == GameFinishedReason.MoveNextLocation && PartyPersistence.FinishSnapshot.Count > 0)
            {
                PartyPersistence.Pending = PartyPersistence.FinishSnapshot
                    .Select(e => new PartyPersistence.Entry { ProfileId = e.ProfileId })
                    .ToList();
                Log.Info($"Next floor: carrying over {PartyPersistence.Pending.Count} party merc(s).");
            }
            PartyPersistence.FinishSnapshot = null;
        }
    }
}
