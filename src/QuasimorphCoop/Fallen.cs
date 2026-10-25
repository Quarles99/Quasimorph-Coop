using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop
{
    // Party mercs who died while someone else carried on. The game only resolves the death of the
    // deployed merc (MercenaryInRaid), when the mission result is processed back on the ship; these
    // get the same treatment there: death penalty on their gear, then cloning or permadeath per the
    // difficulty settings. Remembered per save slot in slot_<n>_coop_fallen.dat until then, so the
    // list survives floor changes and quitting.
    internal static class Fallen
    {
        private static string FileName(int slot) => $"slot_{slot}_coop_fallen.dat";

        public static List<string> Get(State state)
        {
            int? slot = state?.Get<SavedGameMetadata>()?.Slot;
            if (slot == null)
            {
                return new List<string>();
            }
            string text = SingletonMonoBehaviour<FileManager>.Instance.LoadTextFile(FileName(slot.Value));
            return string.IsNullOrEmpty(text) ? new List<string>() : text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }

        private static void Set(State state, List<string> ids)
        {
            int? slot = state?.Get<SavedGameMetadata>()?.Slot;
            if (slot != null)
            {
                SingletonMonoBehaviour<FileManager>.Instance.SaveFile(FileName(slot.Value), string.Join("\n", ids));
            }
        }

        public static void Add(State state, string profileId)
        {
            List<string> ids = Get(state);
            if (!ids.Contains(profileId))
            {
                ids.Add(profileId);
                Set(state, ids);
                Log.Info($"Fallen: '{profileId}' will be resolved as dead on return to the ship.");
            }
        }

        // MissionSystem.MercDied, for a merc other than MercenaryInRaid.
        public static void Resolve(State state, Mercenaries mercenaries, MagnumCargo cargo, MagnumProgression progression, MagnumProjects projects,
            SpaceTime time, Difficulty difficulty, PerkFactory perkFactory)
        {
            List<string> ids = Get(state);
            if (ids.Count == 0)
            {
                return;
            }
            foreach (string id in ids)
            {
                Mercenary merc = mercenaries.Get(id);
                // Alive again means a save from before the death was loaded; the deployed merc is the game's to resolve.
                if (merc == null || merc.CreatureData?.Health == null || merc.CreatureData.Health.Alive || merc.State == MercenaryState.InRaid)
                {
                    continue;
                }
                Log.Info($"Fallen: resolving the death of '{id}' ({difficulty.Preset.RevivePenalty}).");
                MercenarySystem.SetDefaultFireMode(merc);
                MercenarySystem.DropOnDeathPenalty(merc, cargo, difficulty, time);
                if (difficulty.Preset.RevivePenalty == RevivePenalty.PermanentDeath)
                {
                    MercenarySystem.DeleteMercenary(mercenaries, id);
                }
                else
                {
                    MercenarySystem.CloneMercenary(time, projects, progression, mercenaries, id, difficulty.Preset.RevivePenalty == RevivePenalty.NoPenalty, difficulty, perkFactory);
                }
                if (Partner.Get(state) == id && mercenaries.Get(id) == null)
                {
                    Partner.Set(state, null);
                }
            }
            Set(state, new List<string>());
        }
    }

    [HarmonyPatch(typeof(MissionSystem), nameof(MissionSystem.ProcessFinishedDungeonData))]
    internal static class ResolveFallenPatch
    {
        private static void Postfix(Mercenaries mercenaries, MagnumCargo magnumCargo, MagnumProgression magnumSpaceship, MagnumProjects magnumProjects,
            SpaceTime spaceTime, Difficulty difficulty, PerkFactory perkFactory)
        {
            try
            {
                Fallen.Resolve(Net.NetSession.GetGlobalState(), mercenaries, magnumCargo, magnumSpaceship, magnumProjects, spaceTime, difficulty, perkFactory);
            }
            catch (System.Exception e)
            {
                Log.Error("Failed to resolve fallen party mercs: " + e);
            }
        }
    }
}
