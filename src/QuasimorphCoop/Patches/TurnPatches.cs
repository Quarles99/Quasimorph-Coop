using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Patches
{
    // TurnController.Tick calls ProcessActionPoint on whoever's AP comes up next.
    // Swapping here, before the merc processes its AP, means TurnSystem.CanProcessPlayerTurn
    // (current == Creatures.Player) is already true when DungeonGameMode.Update asks.
    [HarmonyPatch(typeof(Player), nameof(Player.ProcessActionPoint))]
    internal static class PlayerProcessActionPointPatch
    {
        private static void Prefix(Player __instance)
        {
            if (CoopSession.Active && CoopSession.IsMember(__instance) && __instance.CreatureData.Health.Alive)
            {
                CoopSession.MakeActive(__instance, "turn");
            }
        }

        private static void Postfix(Player __instance) => Net.NetSession.OnActionPointStarted(__instance);
    }

    // A held merc passes its action point the way a player's skip would: from DungeonGameMode.Update
    // once the merc is idle. Ending it inside ProcessActionPoint instead lets TurnController.Tick run
    // the next creature in the same frame or not, depending on animations, i.e. on frame rate.
    [HarmonyPatch(typeof(DungeonGameMode), "Update")]
    internal static class HoldSkipPatch
    {
        private static void Prefix() => Hold.Update();
    }
}
