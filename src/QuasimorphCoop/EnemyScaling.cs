using System;
using System.Globalization;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop
{
    // More enemies for a two-merc party. The game buys enemy groups with a points budget
    // (UnitGenerationSystem.GenerateUnitsByPoints): a level's random groups when it's generated, and
    // most reinforcements during a mission. Both get multiplied; hand-placed story enemies, quest
    // groups and allied groups don't.
    //
    // The multiplier is per save slot (slot_<n>_coop_enemies.dat, default x2), so it travels with the
    // save the host sends, and it can't change while online: reinforcements are rolled on both
    // machines and must come out the same.
    internal static class EnemyScaling
    {
        public static readonly float[] Choices = { 1f, 1.5f, 2f };
        private const float Default = 2f;

        private static string FileName(int slot) => $"slot_{slot}_coop_enemies.dat";

        // Test-only: replaces the saved setting.
        internal static float? Override;

        public static float Get(State global)
        {
            if (Override != null)
            {
                return Override.Value;
            }
            int? slot = global?.Get<SavedGameMetadata>()?.Slot;
            if (slot == null)
            {
                return Default;
            }
            string text = SingletonMonoBehaviour<FileManager>.Instance.LoadTextFile(FileName(slot.Value))?.Trim();
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && value > 0f ? value : Default;
        }

        public static void Set(State global, float value)
        {
            int? slot = global?.Get<SavedGameMetadata>()?.Slot;
            if (slot != null)
            {
                SingletonMonoBehaviour<FileManager>.Instance.SaveFile(FileName(slot.Value), value.ToString(CultureInfo.InvariantCulture));
                Log.Info($"Enemy multiplier for slot {slot} set to x{value.ToString(CultureInfo.InvariantCulture)}.");
            }
        }

        public static string Label(float value) => "x" + value.ToString("0.#", CultureInfo.InvariantCulture);

        // While a level is being generated: the multiplier (1 if no partner will join) and the
        // faction whose groups are allies.
        private static float? _generating;
        private static string _allyFaction;

        // Test-only: a generation scope with a known ally faction.
        internal static void BeginGenerationForTest(float multiplier, string allyFaction)
        {
            _generating = multiplier;
            _allyFaction = allyFaction;
        }

        public static void BeginGeneration(State state)
        {
            _generating = PartnerWillJoin(state) ? Get(Net.NetSession.GetGlobalState() ?? state) : 1f;
            Mission mission = state.Get<Missions>()?.Get(state.Get<RaidMetadata>());
            _allyFaction = mission?.BeneficiaryFactionId;
        }

        public static void EndGeneration()
        {
            _generating = null;
            _allyFaction = null;
        }

        public static float Multiplier(string factionId)
        {
            if (_generating != null)
            {
                return factionId != null && factionId == _allyFaction ? 1f : _generating.Value;
            }
            return CoopSession.Active ? Get(Net.NetSession.GetGlobalState()) : 1f;
        }

        // Partner.AutoAdd's checks, before the level exists.
        private static bool PartnerWillJoin(State state)
        {
            State global = Net.NetSession.GetGlobalState() ?? state;
            string id = Partner.Get(global);
            Mercenaries mercenaries = state.Get<Mercenaries>();
            Mercenary partner = id == null ? null : mercenaries?.Get(id);
            return partner != null && partner != mercenaries.MercenaryInRaid && Partner.IsAvailable(partner);
        }
    }

    [HarmonyPatch(typeof(DungeonBuilder), "SpawnDroppableContent")]
    internal static class EnemyScalingGenerationPatch
    {
        private static readonly AccessTools.FieldRef<DungeonBuilder, State> StateField = AccessTools.FieldRefAccess<DungeonBuilder, State>("_state");

        private static void Prefix(DungeonBuilder __instance)
        {
            try
            {
                EnemyScaling.BeginGeneration(StateField(__instance));
            }
            catch (Exception e)
            {
                Log.Warn("Enemy scaling: couldn't read the level's setup: " + e.Message);
                EnemyScaling.EndGeneration();
            }
        }

        private static Exception Finalizer(Exception __exception)
        {
            EnemyScaling.EndGeneration();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(UnitGenerationSystem), nameof(UnitGenerationSystem.GenerateUnitsByPoints))]
    internal static class EnemyScalingPointsPatch
    {
        private static void Prefix(string factionId, ref float totalPoints)
        {
            float multiplier = EnemyScaling.Multiplier(factionId);
            if (multiplier != 1f && totalPoints > 0f)
            {
                Log.Info($"Enemy points for {factionId}: {totalPoints:0} x{multiplier:0.#}.");
                totalPoints *= multiplier;
            }
        }
    }
}
