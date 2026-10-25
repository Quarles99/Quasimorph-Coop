using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MGSC;
using SimpleJSON;

namespace QuasimorphCoop.Net
{
    // The friend's merc's loadout between missions. The friend's game holds a copy of the host's
    // ship, so it can't change the real one; instead it equips its merc in the arsenal on its copy
    // and sends the storages it changed. The host is the authority: it applies them only if each of
    // those storages is still exactly what it last sent the friend, and otherwise refuses (and the
    // friend gets the host's current state again). Everything here is the storages the arsenal
    // screen can touch: the merc's own gear, the ship cargo tabs, the fridge, the recycling bin and
    // the shuttle cargo. Storages are written with the game's save format, like ItemSync.
    internal static class ShipSync
    {
        // Storage keys. "f" is the merc's inventory fields; "m<i>" its containers (grids, then slots);
        // "c<i>" a ship cargo tab.
        private const string Fields = "f", Fridge = "fridge", Recycle = "recycle", Shuttle = "shuttle";

        private static IEnumerable<KeyValuePair<string, ItemStorage>> Storages(State global, Mercenary merc)
        {
            List<ItemStorage> containers = merc.CreatureData.Inventory.AllContainers;
            for (int i = 0; i < containers.Count; i++)
            {
                if (containers[i] != null)
                {
                    yield return new KeyValuePair<string, ItemStorage>("m" + i, containers[i]);
                }
            }
            MagnumCargo cargo = global.Get<MagnumCargo>();
            for (int i = 0; i < cargo.ShipCargo.Count; i++)
            {
                yield return new KeyValuePair<string, ItemStorage>("c" + i, cargo.ShipCargo[i]);
            }
            yield return new KeyValuePair<string, ItemStorage>(Fridge, cargo.FridgeStorage);
            yield return new KeyValuePair<string, ItemStorage>(Recycle, cargo.RecyclingStorage);
            ItemStorage shuttle = global.Get<MagnumProgression>()?.GetDepartment<ShuttleCargoDepartment>()?.ShuttleCargo;
            if (shuttle != null)
            {
                yield return new KeyValuePair<string, ItemStorage>(Shuttle, shuttle);
            }
        }

        public static Dictionary<string, string> Snapshot(State global, Mercenary merc)
        {
            using ItemSync.RngGuard _ = ItemSync.KeepRng();
            Dictionary<string, string> result = new Dictionary<string, string>
            {
                [Fields] = ItemSync.SerializeInventoryFields(merc.CreatureData.Inventory)
            };
            foreach (KeyValuePair<string, ItemStorage> s in Storages(global, merc))
            {
                result[s.Key] = SaveToJSON.CreateNode(s.Value).ToString();
            }
            return result;
        }

        // Storages whose contents differ from the baseline.
        public static Dictionary<string, string> Changes(Dictionary<string, string> baseline, Dictionary<string, string> now) =>
            now.Where(kv => !baseline.TryGetValue(kv.Key, out string before) || before != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);

        // Keys in the change whose host storage no longer matches what the friend started from.
        public static List<string> Conflicts(Dictionary<string, string> sent, Dictionary<string, string> current, Dictionary<string, string> change) =>
            change.Keys.Where(k => !sent.TryGetValue(k, out string s) || !current.TryGetValue(k, out string c) || s != c).ToList();

        public static void Load(State global, Mercenary merc, Dictionary<string, string> entries)
        {
            using ItemSync.RngGuard _ = ItemSync.KeepRng();
            Inventory inventory = merc.CreatureData.Inventory;
            if (entries.TryGetValue(Fields, out string fields))
            {
                ItemSync.LoadInventoryFields(inventory, JSON.Parse(fields));
            }
            Dictionary<string, ItemStorage> storages = Storages(global, merc).ToDictionary(s => s.Key, s => s.Value);
            // Slots before grids (equipping a backpack or vest resizes its grid), then the cargo.
            foreach (string key in entries.Keys.Where(k => k != Fields).OrderBy(Order))
            {
                if (!storages.TryGetValue(key, out ItemStorage storage))
                {
                    throw new InvalidDataException("No ship storage " + key);
                }
                storage.LoadJSON(JSON.Parse(entries[key]));
            }
            ItemSync.AfterInventoryLoad(inventory);
        }

        private static int Order(string key)
        {
            if (key.StartsWith("m"))
            {
                return int.Parse(key.Substring(1)) < 2 ? 1 : 0;
            }
            return 2;
        }

        // ------------------------------------------------------------------ wire format

        public static void Write(BinaryWriter w, Dictionary<string, string> entries)
        {
            w.Write(entries.Count);
            foreach (KeyValuePair<string, string> e in entries.OrderBy(e => e.Key))
            {
                w.Write(e.Key);
                w.Write(e.Value);
            }
        }

        public static Dictionary<string, string> Read(BinaryReader r)
        {
            int count = r.ReadInt32();
            Dictionary<string, string> entries = new Dictionary<string, string>(count);
            for (int i = 0; i < count; i++)
            {
                entries[r.ReadString()] = r.ReadString();
            }
            return entries;
        }

        public static byte[] Compress(byte[] data)
        {
            using (MemoryStream output = new MemoryStream())
            {
                using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress))
                {
                    gzip.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
        }

        public static byte[] Decompress(byte[] data)
        {
            using (GZipStream gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return output.ToArray();
            }
        }
    }
}
