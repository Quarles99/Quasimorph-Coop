using HarmonyLib;
using MGSC;
using QuasimorphCoop.Net;

namespace QuasimorphCoop.Patches
{
    // The friend's copy of the ship mirrors the host's star map (Net/NetSession.World).

    // No ship simulation of its own: missions, news, stations, factions, prices, cargo timers and
    // merc recovery all roll their own random numbers, so they come from the host instead. The
    // clock (with the host's time patched in), orbit rings and the travel animation still run.
    [HarmonyPatch(typeof(GameLoop), nameof(GameLoop.Tick_Space))]
    internal static class MirrorTickSpacePatch
    {
        private static readonly AccessTools.FieldRef<GameLoop, State> StateRef = AccessTools.FieldRefAccess<GameLoop, State>("_state");

        private static bool Prefix(GameLoop __instance)
        {
            if (!NetSession.MirroringShip)
            {
                return true;
            }
            State state = StateRef(__instance);
            SpaceTime spaceTime = state.Get<SpaceTime>();
            TravelMetadata travel = state.Get<TravelMetadata>();
            Spaceship spaceship = state.Get<Spaceship>();
            SpaceObjects spaceObjects = state.Get<SpaceObjects>();
            OrbitRingsSystem.Update(spaceTime, travel);
            SpaceTimeSystem.Update(spaceTime, travel, spaceship, spaceObjects);
            TravelSystem.ProcessSpaceshipTravel(state.Get<Stations>(), state.Get<Factions>(), state.Get<StoryTriggers>(), spaceTime, spaceship, spaceObjects, travel, state.Get<Difficulty>());
            return false;
        }
    }

    // The host's departures are sent so the friend's ship flies along.
    [HarmonyPatch(typeof(TravelSystem), nameof(TravelSystem.StartSpaceshipTravel))]
    internal static class TravelStartedPatch
    {
        private static void Prefix(TravelMetadata travelData, out string __state) => __state = travelData.CurrentSpaceObject;

        private static void Postfix(TravelMetadata travelData, string objectId, string __state) => NetSession.OnTravelStarted(travelData, __state, objectId);
    }

    // Only the host flies the ship; the friend's copy follows (and can still browse the star map).
    [HarmonyPatch(typeof(StarmapScreen), "DepartureButtonOnClick")]
    internal static class MirrorDeparturePatch
    {
        private static bool Prefix()
        {
            if (!NetSession.MirroringShip)
            {
                return true;
            }
            NetSession.Notify("Co-op: only the host flies the ship. Yours follows when they leave orbit.");
            return false;
        }
    }

    // Story and orbit-event scenarios on the ship run on the host; the friend sees their results.
    [HarmonyPatch(typeof(SpaceGameMode), nameof(SpaceGameMode.InitScenario))]
    internal static class MirrorInitScenarioPatch
    {
        private static bool Prefix() => !NetSession.MirroringShip;
    }

    [HarmonyPatch(typeof(SpaceGameMode), nameof(SpaceGameMode.RunScenarioByStoryId))]
    internal static class MirrorRunScenarioPatch
    {
        private static bool Prefix(ref BaseScenario __result)
        {
            if (!NetSession.MirroringShip)
            {
                return true;
            }
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(IngameEventSystem), nameof(IngameEventSystem.RandomizeOrbitEvent))]
    internal static class MirrorOrbitEventPatch
    {
        private static bool Prefix(ref string __result)
        {
            if (!NetSession.MirroringShip)
            {
                return true;
            }
            __result = null;
            return false;
        }
    }
}
