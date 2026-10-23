using System.Collections.Generic;
using MGSC;
using QuasimorphCoop.Net;
using Steamworks;

namespace QuasimorphCoop.Commands
{
    // Fallback for when the Steam overlay invite doesn't work (e.g. under Proton).
    [ConsoleCommand(new string[] { "coop_join" })]
    public class CoopJoinCommand
    {
        public static string Help(string command, bool verbose)
        {
            return "Joins a co-op lobby by id (shown to the host after coop_host). Run it from the main menu. Syntax: coop_join <lobbyid>";
        }

        public string Execute(string[] tokens)
        {
            if (tokens.Length != 1 || !ulong.TryParse(tokens[0], out ulong lobbyId))
            {
                return "Syntax: coop_join <lobbyid>";
            }
            SteamNet.Ensure();
            SteamNet.Instance.JoinLobby(new CSteamID(lobbyId));
            return $"Joining lobby {lobbyId}...";
        }

        public static List<string> FetchAutocompleteOptions(string command, string[] tokens) => null;

        public static bool IsAvailable() => true;

        public static bool ShowInHelpAndAutocomplete() => true;
    }
}
