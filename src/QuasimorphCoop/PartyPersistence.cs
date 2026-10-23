using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MGSC;

namespace QuasimorphCoop
{
    // The dungeon save has a single Player slot and a hardcoded component list, so the extra
    // party mercs are stored in a small sidecar written next to each dungeon file with the
    // same naming ({prefix}slot_{n}_coop_{location}.dat). Every SaveDungeon writes one, so a
    // sidecar always matches the dungeon file beside it (manual, autosave, per floor).
    //
    // Only roster mercs are stored (by ProfileId); their gear, wounds and perks already live
    // in the roster, which the main session save covers.
    internal static class PartyPersistence
    {
        internal struct Entry
        {
            public string ProfileId;
            public bool HasPosition;
            public int X;
            public int Y;
        }

        // Party read from a loaded save, or carried over to the next floor; spawned on DungeonStarted.
        public static List<Entry> Pending;

        // Party as it was when FinishGame started; FinishGame collapses the party before its own saves run.
        public static List<Entry> FinishSnapshot;

        public static string FileName(SavedGameMetadata metadata, string locationId)
        {
            string prefix = metadata.IsReportSave ? "report_" : (metadata.IsAutoSave ? "autosave_" : "");
            return $"{prefix}slot_{metadata.Slot}_coop_{locationId}.dat";
        }

        public static List<Entry> SnapshotLiveParty(bool withPositions)
        {
            return CoopSession.Party.Skip(1)
                .Where(p => p != null && p.CreatureData.Health.Alive && p.Mercenary != null)
                .Select(p => new Entry
                {
                    ProfileId = p.Mercenary.ProfileId,
                    HasPosition = withPositions,
                    X = p.CreatureData.Position.X,
                    Y = p.CreatureData.Position.Y
                })
                .ToList();
        }

        // What the party "is" right now from a save's point of view.
        public static List<Entry> CurrentForSave()
        {
            if (CoopSession.Active)
            {
                return SnapshotLiveParty(withPositions: true);
            }
            // The dungeon-entry autosave runs before DungeonStarted, i.e. before Pending is spawned.
            return FinishSnapshot ?? Pending ?? new List<Entry>();
        }

        public static void Write(SavedGameMetadata metadata, string locationId)
        {
            List<Entry> entries = CurrentForSave();
            StringBuilder sb = new StringBuilder();
            foreach (Entry e in entries)
            {
                sb.Append(e.ProfileId).Append(' ')
                    .Append(e.HasPosition ? e.X.ToString(CultureInfo.InvariantCulture) : "-").Append(' ')
                    .Append(e.HasPosition ? e.Y.ToString(CultureInfo.InvariantCulture) : "-").Append('\n');
            }
            string fileName = FileName(metadata, locationId);
            SingletonMonoBehaviour<FileManager>.Instance.SaveFile(fileName, sb.ToString());
            if (entries.Count > 0)
            {
                Log.Info($"Wrote {entries.Count} party merc(s) to {fileName}: {string.Join(", ", entries.Select(Describe))}.");
            }
        }

        public static List<Entry> Read(SavedGameMetadata metadata, string locationId)
        {
            string fileName = FileName(metadata, locationId);
            string text = SingletonMonoBehaviour<FileManager>.Instance.LoadTextFile(fileName);
            List<Entry> entries = new List<Entry>();
            if (string.IsNullOrEmpty(text))
            {
                return entries;
            }
            foreach (string line in text.Split('\n'))
            {
                string[] parts = line.Trim().Split(' ');
                if (parts.Length != 3 || parts[0].Length == 0)
                {
                    continue;
                }
                Entry e = new Entry { ProfileId = parts[0] };
                if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out e.X)
                    && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out e.Y))
                {
                    e.HasPosition = true;
                }
                entries.Add(e);
            }
            Log.Info($"Read {entries.Count} party merc(s) from {fileName}: {string.Join(", ", entries.Select(Describe))}.");
            return entries;
        }

        // Called from DungeonStarted, after the primary merc exists.
        public static void SpawnPending()
        {
            List<Entry> pending = Pending;
            Pending = null;
            if (pending == null || pending.Count == 0)
            {
                return;
            }
            Mercenaries mercenaries = CoopSession.State.Get<Mercenaries>();
            foreach (Entry e in pending)
            {
                Mercenary mercenary = mercenaries.Get(e.ProfileId);
                if (mercenary == null || mercenary.CreatureData?.Health == null || !mercenary.CreatureData.Health.Alive)
                {
                    Log.Warn($"Can't restore party merc '{e.ProfileId}': not in roster or not alive.");
                    continue;
                }
                if (CoopSession.Party.Any(p => p.Mercenary == mercenary))
                {
                    continue;
                }
                CellPosition target = e.HasPosition
                    ? new CellPosition(e.X, e.Y)
                    : CoopSession.Primary.CreatureData.Position;
                if (!CoopSession.FindFreeCellNear(target, out CellPosition position))
                {
                    Log.Warn($"Can't restore party merc '{e.ProfileId}': no free cell near {target.X},{target.Y}.");
                    continue;
                }
                CoopSession.AddMerc(mercenary, position);
            }
        }

        private static string Describe(Entry e)
        {
            return e.HasPosition ? $"{e.ProfileId}@{e.X},{e.Y}" : $"{e.ProfileId}@near-primary";
        }
    }
}
