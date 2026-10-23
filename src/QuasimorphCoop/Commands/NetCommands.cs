using System.Collections.Generic;
using MGSC;
using QuasimorphCoop.Net;

namespace QuasimorphCoop.Commands
{
    [ConsoleCommand(new string[] { "coop_host" })]
    public class CoopHostCommand
    {
        public static string Help(string command, bool verbose)
        {
            return "Hosts online co-op for the current mission. Add your friend's merc first with coop_add; they get party merc #1.";
        }

        public string Execute(string[] tokens) => NetSession.StartHosting();

        public static List<string> FetchAutocompleteOptions(string command, string[] tokens) => null;

        public static bool IsAvailable() => true;

        public static bool ShowInHelpAndAutocomplete() => true;
    }

    [ConsoleCommand(new string[] { "coop_leave" })]
    public class CoopLeaveCommand
    {
        public static string Help(string command, bool verbose)
        {
            return "Ends the online co-op session. The mission continues as local hotseat.";
        }

        public string Execute(string[] tokens)
        {
            NetSession.Leave("coop_leave");
            return "Left. " + NetSession.Status;
        }

        public static List<string> FetchAutocompleteOptions(string command, string[] tokens) => null;

        public static bool IsAvailable() => true;

        public static bool ShowInHelpAndAutocomplete() => true;
    }

    [ConsoleCommand(new string[] { "coop_net" })]
    public class CoopNetCommand
    {
        public static string Help(string command, bool verbose)
        {
            return "Shows the online co-op session state.";
        }

        public string Execute(string[] tokens) => NetSession.Status;

        public static List<string> FetchAutocompleteOptions(string command, string[] tokens) => null;

        public static bool IsAvailable() => true;

        public static bool ShowInHelpAndAutocomplete() => true;
    }
}
