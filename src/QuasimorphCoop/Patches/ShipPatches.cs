using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Patches
{
    // Between missions the friend equips their merc on a copy of the host's ship (Net/ShipSync).

    [HarmonyPatch(typeof(ArsenalScreen), nameof(ArsenalScreen.Configure))]
    internal static class ArsenalOpenedPatch
    {
        private static void Postfix(Mercenary mercenary) => Net.NetSession.OnArsenalOpened(mercenary);
    }

    [HarmonyPatch(typeof(ArsenalScreen), "OnDisable")]
    internal static class ArsenalClosedPatch
    {
        private static void Postfix() => Net.NetSession.OnArsenalClosed();
    }

    [HarmonyPatch(typeof(PrepareRaidScreen), "StartOperationButtonOnClick")]
    internal static class MissionStartPatch
    {
        private static bool Prefix() => Net.NetSession.AllowMissionStart();
    }

    [HarmonyPatch(typeof(ScreenWithShipCargo), "RecycleButtonOnClick")]
    internal static class RecyclePatch
    {
        private static bool Prefix() => Net.NetSession.AllowRecycle();
    }
}
