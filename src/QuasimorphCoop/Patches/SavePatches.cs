using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Patches
{
    // Creatures.Player is the only saved player slot, so a save must always see the primary merc.
    // Extra mercs are not written into the dungeon save; reloading mid-mission drops them
    // (their roster entry keeps whatever gear and wounds they had).
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

    // Mission results (extraction, death, abort) are computed from Creatures.Player and MercenaryInRaid.
    [HarmonyPatch(typeof(DungeonGameMode), "FinishGame")]
    internal static class DungeonGameModeFinishGamePatch
    {
        private static void Prefix()
        {
            if (CoopSession.Active)
            {
                Log.Info("FinishGame: collapsing party to primary merc.");
                CoopSession.Collapse();
            }
        }
    }
}
