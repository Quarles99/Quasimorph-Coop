using System.Collections.Generic;
using System.Linq;
using System.Text;
using MGSC;

namespace QuasimorphCoop.Commands
{
    // Milestone 1 test commands. Open the dev console with the backquote key during a mission.
    // Note: the game flags a run's achievements as dirty once any console command is used.

    [ConsoleCommand(new string[] { "coop_add" })]
    public class CoopAddCommand
    {
        private const ObstacleType BlockingMask = ObstacleType.Wall | ObstacleType.Obj | ObstacleType.Deep | ObstacleType.Fence
            | ObstacleType.Monster | ObstacleType.PlayerAlly | ObstacleType.Player | ObstacleType.ClosedDoor
            | ObstacleType.BlockedDoor | ObstacleType.Unexplored;

        public static string Help(string command, bool verbose)
        {
            return "Adds a second player-controlled merc at the cell under the mouse cursor. "
                + "Syntax: coop_add (lists roster) | coop_add <index> (roster merc) | coop_add temp (throwaway copy of your merc's profile, no gear).";
        }

        public string Execute(string[] tokens)
        {
            if (!CoopSession.InDungeon)
            {
                return "Only works during a mission.";
            }
            Mercenaries mercenaries = CoopSession.State.Get<Mercenaries>();
            List<Mercenary> candidates = Candidates(mercenaries);
            if (tokens.Length == 0)
            {
                return ListRoster(candidates);
            }

            Mercenary mercenary;
            if (tokens[0] == "temp")
            {
                mercenary = CreateTemporaryMerc();
            }
            else if (int.TryParse(tokens[0], out int index) && index >= 0 && index < candidates.Count)
            {
                mercenary = candidates[index];
            }
            else
            {
                return "Unknown merc. " + ListRoster(candidates);
            }

            CellPosition position = CoopSession.State.Get<MapRenderer>().GetCellUnderCursor();
            string invalid = ValidateSpawnCell(position);
            if (invalid != null)
            {
                return invalid;
            }

            Player player = CoopSession.AddMerc(mercenary, position);
            return $"Added {mercenary.AgentName} ({mercenary.ProfileId}) at {position.X},{position.Y}. Party size {CoopSession.Party.Count}.";
        }

        // Roster mercs that are alive and not already on this mission.
        private static List<Mercenary> Candidates(Mercenaries mercenaries)
        {
            return mercenaries.Values
                .Where(m => m != null && m.CreatureData?.Health != null && m.CreatureData.Health.Alive)
                .Where(m => !CoopSession.Party.Any(p => p.Mercenary == m))
                .ToList();
        }

        private static string ListRoster(List<Mercenary> candidates)
        {
            if (candidates.Count == 0)
            {
                return "No other living mercs in your roster. Use 'coop_add temp' for a throwaway merc.";
            }
            StringBuilder sb = new StringBuilder("Available mercs (point the mouse at an empty floor cell, then coop_add <index>):");
            for (int i = 0; i < candidates.Count; i++)
            {
                Mercenary m = candidates[i];
                sb.Append($"\n  {i}: {m.AgentName} [{m.ProfileId}] class={m.MercClassId} state={m.State}");
            }
            return sb.ToString();
        }

        // Not added to the roster, so it disappears after the mission.
        private static Mercenary CreateTemporaryMerc()
        {
            Mercenary primary = CoopSession.Primary.Mercenary;
            MercenaryProfileRecord profile = Data.MercenaryProfiles.GetRecord(primary.ProfileId);
            PerkFactory perkFactory = CoopSession.State.Get<PerkFactory>();
            List<Perk> perks = new List<Perk>
            {
                perkFactory.CreatePerk(Data.Perks.GetRecord(profile.TalentPerkId)),
                perkFactory.CreatePerk(Data.Perks.GetRecord("rank_0"))
            };
            Mercenary mercenary = MercenarySystem.GenerateMercenary(profile, primary.MercClassId, perks);
            Log.Info($"Generated temporary merc from profile '{profile.Id}'.");
            return mercenary;
        }

        private static string ValidateSpawnCell(CellPosition position)
        {
            MapCell cell = CoopSession.State.Get<MapGrid>().GetCell(position);
            if (cell == null)
            {
                return $"Cell {position.X},{position.Y} is outside the map.";
            }
            if ((cell.PassConditionMask & BlockingMask) != 0 || CoopSession.Creatures.GetCreature(position) != null)
            {
                return $"Cell {position.X},{position.Y} is blocked ({cell.PassConditionMask}). Point the mouse at an empty, explored floor cell.";
            }
            return null;
        }

        public static List<string> FetchAutocompleteOptions(string command, string[] tokens)
        {
            return null;
        }

        public static bool IsAvailable()
        {
            return true;
        }

        public static bool ShowInHelpAndAutocomplete()
        {
            return true;
        }
    }

    [ConsoleCommand(new string[] { "coop_status" })]
    public class CoopStatusCommand
    {
        public static string Help(string command, bool verbose)
        {
            return "Shows the co-op party, which merc holds the player slot, and whose turn it is.";
        }

        public string Execute(string[] tokens)
        {
            if (!CoopSession.InDungeon)
            {
                return "No mission in progress.";
            }
            Player slot = CoopSession.Creatures.Player;
            ITurnable current = CoopSession.State.Get<TurnController>().CurrentProcessable;
            StringBuilder sb = new StringBuilder($"Party of {CoopSession.Party.Count}:");
            for (int i = 0; i < CoopSession.Party.Count; i++)
            {
                Player p = CoopSession.Party[i];
                CreatureData data = p.CreatureData;
                sb.Append($"\n  {i}: {p.Mercenary?.AgentName} [{p.Mercenary?.ProfileId}] id={data.UniqueId} pos={data.Position.X},{data.Position.Y} "
                    + $"hp={data.Health.Value} alive={data.Health.Alive}{(p == slot ? " SLOT" : "")}{(ReferenceEquals(p, current) ? " TURN" : "")}");
            }
            if (!(current is Player))
            {
                sb.Append($"\nCurrent turn: {current?.GetType().Name ?? "none"}");
            }
            string result = sb.ToString();
            Log.Info(result);
            return result;
        }

        public static List<string> FetchAutocompleteOptions(string command, string[] tokens)
        {
            return null;
        }

        public static bool IsAvailable()
        {
            return true;
        }

        public static bool ShowInHelpAndAutocomplete()
        {
            return true;
        }
    }
}
