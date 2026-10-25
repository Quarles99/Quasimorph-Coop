using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MGSC;
using UnityEngine;

namespace QuasimorphCoop.Patches
{
    // Shared vision: the map shows what any living party merc can see, not only the merc in the
    // Creatures.Player slot. MapController.UpdateLos darkens the map and runs one line-of-sight pass
    // for the slot merc; the other party mercs get their own pass right after it, into the same
    // listener, so their cells count as seen for everything downstream (fog, which enemies are
    // visible, AI checks of what the player sees). Both co-op machines run the same passes.
    internal static class SharedVision
    {
        private static bool _adding;

        // LosCalculator is internal to the game assembly.
        public static readonly MethodInfo Calculate = AccessTools.Method(AccessTools.TypeByName("MGSC.LosCalculator"), "Calculate");

        public static void AddPartyLos(MapGrid mapGrid, ILosCalcListener listener)
        {
            if (_adding || !CoopSession.Active || !(listener is MapController))
            {
                return;
            }
            _adding = true;
            try
            {
                foreach (Player p in CoopSession.Inactive.Where(p => p.CreatureData.Health.Alive))
                {
                    int los = CreatureSystem.GetLos(p);
                    if (los > 0)
                    {
                        CreatureData d = p.CreatureData;
                        Calculate.Invoke(null, new object[] { mapGrid, d.Position, CellPosition.DirectionToVec2(d.LookDirection), d.LookAngle, false, los + 1, listener, null });
                    }
                }
            }
            finally
            {
                _adding = false;
            }
        }

        // The rectangle around a merc that the game refreshes after a line-of-sight pass.
        public static void ForEachPartyRect(Action<int, int, int, int> action)
        {
            if (!CoopSession.Active)
            {
                return;
            }
            foreach (Player p in CoopSession.Inactive.Where(p => p.CreatureData.Health.Alive))
            {
                int r = CreatureSystem.GetLos(p) + 2;
                CellPosition c = p.CreatureData.Position;
                action(c.X - r, c.Y - r, c.X + r, c.Y + r);
            }
        }
    }

    [HarmonyPatch]
    internal static class LosCalculatorPatch
    {
        private static MethodBase TargetMethod() => SharedVision.Calculate;

        private static void Postfix(MapGrid mapGrid, ILosCalcListener listener) => SharedVision.AddPartyLos(mapGrid, listener);
    }

    // Fog tiles are only re-lit around the slot merc; do the same around the others.
    [HarmonyPatch(typeof(MapController), nameof(MapController.UpdateLos))]
    internal static class UpdateLosFogPatch
    {
        private static readonly AccessTools.FieldRef<MapController, FogOfWar> Fog = AccessTools.FieldRefAccess<MapController, FogOfWar>("_fogOfWar");
        private static readonly AccessTools.FieldRef<MapController, MapGrid> Grid = AccessTools.FieldRefAccess<MapController, MapGrid>("_mapGrid");

        private static void Postfix(MapController __instance)
        {
            FogOfWar fog = Fog(__instance);
            MapGrid grid = Grid(__instance);
            SharedVision.ForEachPartyRect((x0, y0, x1, y1) =>
            {
                for (int x = Mathf.Max(0, x0); x <= Mathf.Min(grid.MaxWidth - 1, x1); x++)
                {
                    for (int y = Mathf.Max(0, y0); y <= Mathf.Min(grid.MaxHeight - 1, y1); y++)
                    {
                        MapCell cell = grid.GetCell(x, y);
                        fog.EnlightTile(x, y, cell.isSeen, cell.IsExplored);
                    }
                }
            });
            if (fog.UnapplyedChangesExist)
            {
                fog.ApplyChanges();
            }
        }
    }

    [HarmonyPatch(typeof(VisibilitySystem), nameof(VisibilitySystem.UpdateVisibility))]
    internal static class UpdateVisibilityRectsPatch
    {
        private static void Postfix(MapRenderer mapRenderer, MapGrid mapGrid, Visibilities visibilities)
        {
            SharedVision.ForEachPartyRect((x0, y0, x1, y1) =>
            {
                VisibilitySystem.UpdateHazardVisibilityInRect(x0, y0, x1, y1, mapGrid, visibilities);
                mapRenderer.UpdateLiquidRenderInRect(x0, y0, x1, y1);
                mapRenderer.RefreshMapTilesInRect(x0, y0, x1, y1);
            });
        }
    }
}
