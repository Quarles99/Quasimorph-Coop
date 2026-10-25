using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Lockstep
{
    // A cheap fingerprint of the simulation: every creature's id, position, HP and alive flag,
    // the charge left in powered map objects (stations), plus the gameplay RNG state. Two machines in lockstep must produce the same string.
    internal static class StateHash
    {
        private static readonly AccessTools.StructFieldRef<UnityEngine.Random.State, int>[] RngFields =
        {
            AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s0"),
            AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s1"),
            AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s2"),
            AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s3"),
        };

        public static string Describe(Creatures creatures, int turnNumber, MapObstacles obstacles = null)
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
                if (c is Player && d.Inventory != null)
                {
                    sb.Append('i').Append(InventoryDigest(d.Inventory)).Append('w').Append((int)d.Inventory.CurrentSlotType);
                }
            }
            if (obstacles != null)
            {
                sb.Append(" e").Append(EnergyDigest(obstacles));
            }
            return sb.ToString();
        }

        private static string EnergyDigest(MapObstacles obstacles)
        {
            StringBuilder sb = new StringBuilder();
            foreach (MapObstacle o in obstacles.Obstacles)
            {
                UsesEnergy energy = o == null ? null : o.GetComponent<UsesEnergy>();
                if (energy != null)
                {
                    sb.Append(o.UniqueId.ToString(CultureInfo.InvariantCulture)).Append(':').Append(energy.Energy.ToString(CultureInfo.InvariantCulture)).Append(';');
                }
            }
            return Hash(sb.ToString()).Substring(0, 8);
        }

        // Every item a merc carries: id, stack, position, and loaded ammo for weapons.
        private static string InventoryDigest(Inventory inventory)
        {
            StringBuilder sb = new StringBuilder();
            foreach (ItemStorage storage in inventory.AllContainers)
            {
                sb.Append('|');
                if (storage == null)
                {
                    continue;
                }
                foreach (BasePickupItem item in storage.Items)
                {
                    sb.Append(item.Id).Append('x').Append(item.StackCount.ToString(CultureInfo.InvariantCulture))
                        .Append('@').Append(item.InventoryPos.X.ToString(CultureInfo.InvariantCulture)).Append(',').Append(item.InventoryPos.Y.ToString(CultureInfo.InvariantCulture));
                    WeaponComponent weapon = item.Comp<WeaponComponent>();
                    if (weapon != null)
                    {
                        sb.Append('a').Append(weapon.CurrentAmmo.ToString(CultureInfo.InvariantCulture));
                    }
                    sb.Append(';');
                }
            }
            return Hash(sb.ToString()).Substring(0, 8);
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
