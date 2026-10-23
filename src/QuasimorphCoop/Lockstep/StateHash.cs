using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Lockstep
{
    // A cheap fingerprint of the simulation: every creature's id, position, HP and alive flag,
    // plus the gameplay RNG state. Two machines in lockstep must produce the same string.
    internal static class StateHash
    {
        private static readonly AccessTools.StructFieldRef<UnityEngine.Random.State, int>[] RngFields =
        {
            AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s0"),
            AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s1"),
            AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s2"),
            AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s3"),
        };

        public static string Describe(Creatures creatures, int turnNumber)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("t").Append(turnNumber.ToString(CultureInfo.InvariantCulture));
            UnityEngine.Random.State rng = GameplayRng.GameplayState;
            sb.Append(" rng");
            foreach (var field in RngFields)
            {
                sb.Append(':').Append(field(ref rng).ToString("x8", CultureInfo.InvariantCulture));
            }
            foreach (Creature c in creatures.Values.Where(c => c != null).OrderBy(c => c.CreatureData.UniqueId))
            {
                CreatureData d = c.CreatureData;
                sb.Append(' ').Append(d.UniqueId.ToString(CultureInfo.InvariantCulture))
                    .Append('@').Append(d.Position.X.ToString(CultureInfo.InvariantCulture))
                    .Append(',').Append(d.Position.Y.ToString(CultureInfo.InvariantCulture))
                    .Append('d').Append(((int)d.LookDirection).ToString(CultureInfo.InvariantCulture))
                    .Append('h').Append(d.Health.Value.ToString(CultureInfo.InvariantCulture))
                    .Append(d.Health.Alive ? "" : "x");
                // Inventory changes made in UI screens aren't synced yet; make them show up as a desync.
                if (c is Player && d.Inventory != null)
                {
                    sb.Append('i').Append(d.Inventory.BackpackStore?.Items.Count.ToString(CultureInfo.InvariantCulture) ?? "-")
                        .Append('/').Append(d.Inventory.VestStore?.Items.Count.ToString(CultureInfo.InvariantCulture) ?? "-")
                        .Append('w').Append(d.Inventory.CurrentWeapon?.Id ?? "-");
                }
            }
            return sb.ToString();
        }

        public static string Hash(string description)
        {
            // FNV-1a 64
            ulong hash = 14695981039346656037UL;
            foreach (char ch in description)
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
    }
}
