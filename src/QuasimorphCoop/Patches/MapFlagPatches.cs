using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Patches
{
    // Cell occupancy flags are rebuilt from Creatures.Player and Creatures.Monsters directly,
    // so mercs out of the slot would leave their cell walk-through. Flag them the way an
    // allied creature is flagged (Monster | PlayerAlly).

    [HarmonyPatch(typeof(CreatureSystem), nameof(CreatureSystem.RefreshMonstersAndEntitiesFlags))]
    internal static class RefreshMonstersAndEntitiesFlagsPatch
    {
        private static void Postfix(MapGrid mapGrid)
        {
            MarkInactive(mapGrid, ObstacleType.Monster | ObstacleType.PlayerAlly);
        }

        internal static void MarkInactive(MapGrid mapGrid, ObstacleType flags)
        {
            if (!CoopSession.Active)
            {
                return;
            }
            foreach (Player player in CoopSession.Inactive)
            {
                if (!player.CreatureData.Health.Alive)
                {
                    continue;
                }
                MapCell cell = mapGrid.GetCell(player.CreatureData.Position);
                if (cell != null)
                {
                    cell.PassConditionMask |= flags;
                }
            }
        }
    }

    [HarmonyPatch(typeof(CreatureSystem), nameof(CreatureSystem.RefreshPlayerAllyFlags))]
    internal static class RefreshPlayerAllyFlagsPatch
    {
        private static void Postfix(MapGrid mapGrid)
        {
            RefreshMonstersAndEntitiesFlagsPatch.MarkInactive(mapGrid, ObstacleType.PlayerAlly);
        }
    }
}
