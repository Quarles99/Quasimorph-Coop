using System.Collections.Generic;
using System.Linq;
using MGSC;

namespace QuasimorphCoop
{
    // The second merc (played by the friend online, or by you in hotseat), remembered per save slot
    // in slot_<n>_coop_partner.dat next to the save, and spawned next to your merc at every level start.
    internal static class Partner
    {
        private static string FileName(int slot) => $"slot_{slot}_coop_partner.dat";

        private static int? Slot(State state) => state?.Get<SavedGameMetadata>()?.Slot;

        public static string Get(State state)
        {
            int? slot = Slot(state);
            if (slot == null)
            {
                return null;
            }
            string id = SingletonMonoBehaviour<FileManager>.Instance.LoadTextFile(FileName(slot.Value))?.Trim();
            return string.IsNullOrEmpty(id) ? null : id;
        }

        public static void Set(State state, string profileId)
        {
            int? slot = Slot(state);
            if (slot == null)
            {
                return;
            }
            SingletonMonoBehaviour<FileManager>.Instance.SaveFile(FileName(slot.Value), profileId ?? "");
            Log.Info($"Partner for slot {slot} set to '{profileId ?? "none"}'.");
        }

        // Living roster mercs other than the one deployed on the current mission.
        public static List<Mercenary> Candidates(State state)
        {
            Mercenary deployed = CoopSession.InDungeon ? CoopSession.Primary?.Mercenary : null;
            return state.Get<Mercenaries>().Values
                .Where(m => m != null && m != deployed && m.CreatureData?.Health != null && m.CreatureData.Health.Alive)
                .ToList();
        }

        // Called at every level start (after any party restored from a save) and when the partner
        // is picked mid-level.
        public static void AutoAdd()
        {
            if (!CoopSession.InDungeon || CoopSession.Party.Count != 1)
            {
                return;
            }
            State state = CoopSession.State;
            string id = Get(state);
            if (id == null)
            {
                return;
            }
            Mercenary mercenary = state.Get<Mercenaries>().Get(id);
            if (mercenary == null || mercenary.CreatureData?.Health == null || !mercenary.CreatureData.Health.Alive)
            {
                Net.NetSession.Notify($"Co-op: partner {CoopSession.DisplayName(mercenary)} isn't available (dead or gone). Pick another in the Co-op menu.");
                return;
            }
            if (mercenary == CoopSession.Primary.Mercenary)
            {
                Net.NetSession.Notify("Co-op: you deployed your partner merc yourself, so no partner joins this mission.");
                return;
            }
            if (!CoopSession.FindFreeCellNear(CoopSession.Primary.CreatureData.Position, out CellPosition position))
            {
                Log.Warn($"No free cell near the primary merc for partner '{id}'.");
                return;
            }
            CoopSession.AddMerc(mercenary, position);
            Net.NetSession.Notify($"Co-op: {CoopSession.DisplayName(mercenary)} joins the mission.");
        }
    }
}
