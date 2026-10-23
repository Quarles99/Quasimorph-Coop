using HarmonyLib;
using MGSC;

namespace QuasimorphCoop
{
    public static class Plugin
    {
        public const string HarmonyId = "quarles99.quasimorphcoop";

        private static bool _patched;

        [Hook(ModHookType.AfterConfigsLoaded)]
        public static void AfterConfigsLoaded(IModContext context)
        {
            // Set after configs load so the game's own settings can't overwrite it.
            Data.Global.Console = true;

            if (!_patched)
            {
                new Harmony(HarmonyId).PatchAll(typeof(Plugin).Assembly);
                _patched = true;
            }
            Net.SteamNet.Ensure();
            Log.Info("Loaded; Harmony patches applied, dev console enabled (backquote key).");
        }

        // Loads from the menu happen after this, so anything still pending here is stale.
        [Hook(ModHookType.MainMenuStarted)]
        public static void MainMenuStarted(IModContext context)
        {
            PartyPersistence.Pending = null;
            PartyPersistence.FinishSnapshot = null;
            Lockstep.LockstepTest.OnMainMenu(context);
            Net.NetSession.OnMainMenuStarted();
            Net.SteamNet.Instance?.JoinPendingLobby();
        }

        [Hook(ModHookType.SpaceStarted)]
        public static void SpaceStarted(IModContext context)
        {
            Net.NetSession.OnSpaceStarted();
        }

        [Hook(ModHookType.SpaceFinished)]
        public static void SpaceFinished(IModContext context)
        {
            Net.NetSession.OnSpaceFinished();
        }

        [Hook(ModHookType.DungeonStarted)]
        public static void DungeonStarted(IModContext context)
        {
            CoopSession.Begin(context.State);
            Lockstep.LockstepTest.OnDungeonStarted();
            Net.NetSession.OnDungeonStarted();
        }

        [Hook(ModHookType.DungeonFinished)]
        public static void DungeonFinished(IModContext context)
        {
            Net.NetSession.OnDungeonFinished();
            CoopSession.End();
        }
    }
}
