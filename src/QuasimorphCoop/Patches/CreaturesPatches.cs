using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Patches
{
    // Creatures only knows about the merc in the Player slot plus Monsters.
    // These postfixes add the other party members back so AI vision, targeting,
    // and id/position lookups still find them.

    [HarmonyPatch(typeof(Creatures), nameof(Creatures.Values), MethodType.Getter)]
    internal static class CreaturesValuesPatch
    {
        private static void Postfix(ref IEnumerable<Creature> __result)
        {
            if (CoopSession.Active)
            {
                __result = __result.Concat(CoopSession.Inactive.Cast<Creature>());
            }
        }
    }

    [HarmonyPatch(typeof(Creatures), nameof(Creatures.GetCreature), typeof(int))]
    internal static class CreaturesGetCreatureByIdPatch
    {
        private static void Postfix(int id, ref Creature __result)
        {
            if (__result == null && CoopSession.Active)
            {
                __result = CoopSession.Inactive.FirstOrDefault(p => p.CreatureData.UniqueId == id);
            }
        }
    }

    [HarmonyPatch(typeof(Creatures), nameof(Creatures.GetCreature), typeof(int), typeof(int))]
    internal static class CreaturesGetCreatureByXYPatch
    {
        private static void Postfix(int x, int y, ref Creature __result)
        {
            if (__result == null && CoopSession.Active)
            {
                __result = CoopSession.Inactive.FirstOrDefault(p => p.CreatureData.Position.X == x && p.CreatureData.Position.Y == y);
            }
        }
    }

    [HarmonyPatch(typeof(Creatures), nameof(Creatures.GetCreature), typeof(CellPosition))]
    internal static class CreaturesGetCreatureByPositionPatch
    {
        private static void Postfix(CellPosition position, ref Creature __result)
        {
            if (__result == null && CoopSession.Active)
            {
                __result = CoopSession.Inactive.FirstOrDefault(p => p.CreatureData.Position.Equals(position));
            }
        }
    }
}
