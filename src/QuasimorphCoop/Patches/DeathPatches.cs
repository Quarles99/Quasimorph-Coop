using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Patches
{
    // Player.OnDead hides the HUD and shows the death screen for any Player.
    // While someone in the party is still standing, undo that and hand them the slot;
    // DungeonGameMode.Update stops processing entirely if the slot merc is dead.
    [HarmonyPatch(typeof(Player), "OnDead")]
    internal static class PlayerOnDeadPatch
    {
        private static void Postfix(Player __instance)
        {
            if (!CoopSession.Active || !CoopSession.IsMember(__instance) || __instance.CreatureData.Health.Alive)
            {
                return;
            }
            Player survivor = CoopSession.FirstAliveOtherThan(__instance);
            if (survivor == null)
            {
                Log.Info($"Merc '{__instance.Mercenary?.ProfileId}' died; no survivors, normal death flow.");
                return;
            }
            Log.Info($"Merc '{__instance.Mercenary?.ProfileId}' died; '{survivor.Mercenary?.ProfileId}' still alive, mission continues.");
            UI.Hide<PreDeathBlockScreen>();
            UI.Chain<DungeonHudScreen>().Show();
            if (CoopSession.Creatures.Player == __instance)
            {
                CoopSession.MakeActive(survivor, "previous active merc died");
            }
        }
    }

    // The game subscribes to the primary merc's PlayerDied only. Don't end the mission while anyone is alive.
    [HarmonyPatch(typeof(DungeonGameMode), "OnPlayerDied")]
    internal static class DungeonGameModeOnPlayerDiedPatch
    {
        private static bool Prefix()
        {
            if (CoopSession.Active && CoopSession.AnyAlive())
            {
                Log.Info("Suppressed game over: a party member is still alive.");
                return false;
            }
            return true;
        }
    }
}
