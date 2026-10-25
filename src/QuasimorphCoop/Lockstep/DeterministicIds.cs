using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Lockstep
{
    // A new weapon gets a random GUID, and overclock/overheat effects find their weapon by it.
    // In lockstep both machines create the same weapons in the same order, so number them instead:
    // the ids then match, including on items copied over by ItemSync.
    [HarmonyPatch(typeof(WeaponComponent), MethodType.Constructor, typeof(BasePickupItemRecord))]
    internal static class WeaponInstanceIdPatch
    {
        private static readonly AccessTools.FieldRef<WeaponComponent, string> Id = AccessTools.FieldRefAccess<WeaponComponent, string>("<InstanceId>k__BackingField");

        private static int _generation;
        private static int _counter;
        private static int _seed;

        // Called whenever the gameplay RNG is reseeded.
        public static void Reset(int seed)
        {
            _seed = seed;
            _generation++;
            _counter = 0;
        }

        private static void Postfix(WeaponComponent __instance)
        {
            if (GameplayRng.Enabled)
            {
                Id(__instance) = $"coop-{_seed:x}-{_generation}-{++_counter}";
            }
        }
    }
}
