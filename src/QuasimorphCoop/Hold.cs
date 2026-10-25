using System.Collections.Generic;
using System.Linq;
using MGSC;

namespace QuasimorphCoop
{
    // A held party merc stays on the map (enemies can still go for it) but passes every action
    // point, so you can play on alone when your friend steps away or drops out without sending
    // their merc back to the ship.
    //
    // Online it is synced like an input: the host's toggle is recorded on the host's own action
    // point (NetSession.RequestHold), and both machines then skip the merc's action points by
    // themselves. The flag rides in the party sidecar, so it survives floors, reloads and resyncs.
    internal static class Hold
    {
        private static readonly HashSet<string> _held = new HashSet<string>();

        // SkipTurn is a recorded direct action; ours happens on both machines and isn't input.
        public static bool Skipping { get; private set; }

        public static bool Any => _held.Count > 0;

        public static bool IsHeld(string profileId) => profileId != null && _held.Contains(profileId);

        public static bool IsHeld(Player player) => IsHeld(player?.Mercenary?.ProfileId);

        // The merc the Co-op menu's Hold acts on: the remembered partner if it's on the map, else the
        // last one added (online, NetSession passes the friend's merc instead).
        public static Player Target(State state)
        {
            if (!CoopSession.Active)
            {
                return null;
            }
            string partner = Partner.Get(state);
            return CoopSession.Party.Skip(1).FirstOrDefault(p => p.Mercenary?.ProfileId == partner)
                ?? CoopSession.Party.Last();
        }

        public static void Set(string profileId, bool held)
        {
            if (profileId == null || (held ? !_held.Add(profileId) : !_held.Remove(profileId)))
            {
                return;
            }
            Log.Info($"Party merc '{profileId}' {(held ? "held" : "released")}.");
        }

        public static void Clear()
        {
            _held.Clear();
        }

        // Every frame, on both machines: pass the held merc's action point once it waits for input
        // (the same point a received skip is replayed at).
        public static void Update()
        {
            Player player = CoopSession.Creatures?.Player;
            if (!CoopSession.Active || !IsHeld(player) || !Net.NetSession.CanProcess(CoopSession.State)
                || !CoopSession.State.Get<PlayerCommandQueue>().Empty)
            {
                return;
            }
            // Never skip the last merc standing, or nobody would ever act again.
            if (!CoopSession.Party.Any(p => p != player && p.CreatureData.Health.Alive && !IsHeld(p)))
            {
                Set(player.Mercenary.ProfileId, false);
                Net.NetSession.Notify($"Co-op: {CoopSession.DisplayName(player.Mercenary)} is no longer held; nobody else is left to play.");
                return;
            }
            Skipping = true;
            try
            {
                player.SkipTurn();
            }
            finally
            {
                Skipping = false;
            }
        }
    }
}
