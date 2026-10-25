using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MGSC;
using SimpleJSON;

namespace QuasimorphCoop.Net
{
    // Keeps item storages in sync while a player uses the inventory, health or loot screens.
    //
    // Those screens move items around through a large amount of UI code (drag and drop, tabs,
    // context menus, shift/ctrl clicks), and while an item is being dragged it is in no storage at
    // all. Instead of replaying all of that, the acting machine watches every storage the active
    // merc could reach (its own inventory, the floor, corpses and containers next to it) and sends
    // the new contents of any storage that changed, serialized with the game's own save format.
    // The other machine loads those contents into its existing storage objects, so everything that
    // holds a reference to a storage stays valid.
    //
    // Actions with effects beyond the storages (eating, medkits, repairs, reloads) are replayed as
    // calls instead (see InventoryOps); items they take as arguments are sent as an ItemRef.
    internal static class ItemSync
    {
        private enum Kind : byte { Creature = 1, Corpse, Store, Quest, Floor, InventoryFields, Cargo }

        // Cargo addresses: the ship's cargo tabs by index, and the shuttle cargo department.
        private const int ShuttleCargoIndex = 100;

        // Where a storage lives, in a form that names the same storage on both machines.
        private struct Address
        {
            public Kind Kind;
            public int A, B, C;

            public string Key => $"{(int)Kind}:{A}:{B}:{C}";

            public void Write(BinaryWriter w)
            {
                w.Write((byte)Kind);
                w.Write(A);
                w.Write(B);
                w.Write(C);
            }

            public static Address Read(BinaryReader r) => new Address { Kind = (Kind)r.ReadByte(), A = r.ReadInt32(), B = r.ReadInt32(), C = r.ReadInt32() };
        }

        private sealed class Watched
        {
            public Address Address;
            public ItemStorage Storage;      // null for InventoryFields
            public Inventory Inventory;      // for InventoryFields
        }

        private const int Reach = 2;

        // Serializing or loading items can draw from UnityEngine.Random (lazy rolls in getters and
        // after-load hooks), and only one machine does each of these. Keep them from moving the
        // random state, which is shared with the simulation when it runs inside its scope.
        internal struct RngGuard : IDisposable
        {
            private readonly UnityEngine.Random.State _saved;

            public RngGuard(bool _) => _saved = UnityEngine.Random.state;

            public void Dispose() => UnityEngine.Random.state = _saved;
        }

        internal static RngGuard KeepRng() => new RngGuard(true);

        private static readonly Dictionary<string, string> _baseline = new Dictionary<string, string>();

        // Storages sent since the acting machine last reloaded its own copies (see Canonicalize).
        private static readonly Dictionary<string, KeyValuePair<Address, string>> _sent = new Dictionary<string, KeyValuePair<Address, string>>();
        private static List<Watched> _watched = new List<Watched>();

        private static readonly MethodInfo CreateObjectFromJson = AccessTools.Method(typeof(LoadFromJSON), "CreateObjectFromJSON");
        private static readonly MethodInfo FloorRefresh = AccessTools.Method(typeof(ItemOnFloor), "RefreshAfterStorageChanged");

        // Raised after loading, in place of the storage events the load bypasses; the creature, its
        // weapon-dependent effects (heavy weapon, overheat, overclock) and open screens listen to them.
        private static readonly string[] InventoryEvents = { "OnBodyEquipmentSlotChanged", "OnWeaponChanged", "OnBackpackChanged" };

        private static void Raise(Inventory inventory, string name)
        {
            (AccessTools.Field(typeof(Inventory), name)?.GetValue(inventory) as Action)?.Invoke();
        }

        // Inventory's own saved fields other than its storages (current weapon slot, bonus sizes...).
        private static readonly MemberInfo[] InventoryFields = typeof(Inventory)
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<Save>() != null)
            .Where(m => (m is FieldInfo f && f.FieldType != typeof(ItemStorage)) || (m is PropertyInfo p && p.PropertyType != typeof(ItemStorage)))
            .ToArray();

        // ---------------------------------------------------------------- watching (acting machine)

        // Re-reads every storage the active merc can reach, without reporting changes.
        public static void ResetBaseline(State state, Player merc)
        {
            using RngGuard _ = KeepRng();
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            _watched = CollectWatched(state, merc);
            _baseline.Clear();
            foreach (Watched w in _watched)
            {
                _baseline[w.Address.Key] = Serialize(w);
            }
            WarnIfSlow(timer, "reading");
        }

        private static void WarnIfSlow(System.Diagnostics.Stopwatch timer, string what)
        {
            if (timer.ElapsedMilliseconds > 15)
            {
                Log.Warn($"Item sync: {what} {_watched.Count} storages took {timer.ElapsedMilliseconds} ms.");
            }
        }

        public static void Clear()
        {
            _watched.Clear();
            _baseline.Clear();
            _sent.Clear();
        }

        // The other machine holds items rebuilt from the sent JSON, and rebuilding isn't perfectly
        // faithful (an unloaded gun's placeholder ammo becomes its default ammo, for one). So the
        // acting machine reloads the storages it sent from the same JSON, at moments when no screen
        // or action holds on to an item object; from then on both machines hold the same objects.
        public static void Canonicalize(State state)
        {
            if (_sent.Count == 0)
            {
                return;
            }
            List<KeyValuePair<Address, string>> reload = new List<KeyValuePair<Address, string>>();
            using (KeepRng())
            {
                foreach (KeyValuePair<Address, string> sent in _sent.Values)
                {
                    // Changed again since it was sent: it goes out again (and gets reloaded) later.
                    Watched w = Find(state, sent.Key);
                    if (w != null && Serialize(w) == sent.Value)
                    {
                        reload.Add(sent);
                    }
                }
            }
            _sent.Clear();
            if (reload.Count > 0)
            {
                Apply(state, Encode(reload));
            }
        }

        // Storage contents that changed since the baseline, as one payload (null if none).
        // The baseline moves to the current state.
        public static byte[] TakeChanges(State state, Player merc)
        {
            // The merc may have opened a container or dropped something on a new cell: rescan.
            using RngGuard _ = KeepRng();
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            List<Watched> watched = CollectWatched(state, merc);
            List<KeyValuePair<Address, string>> changed = new List<KeyValuePair<Address, string>>();
            foreach (Watched w in watched)
            {
                string json = Serialize(w);
                if (!_baseline.TryGetValue(w.Address.Key, out string before) || before != json)
                {
                    // A storage first seen now (e.g. a new pile on the floor) only matters if it isn't empty.
                    if (before != null || !IsEmptyStorage(w))
                    {
                        changed.Add(new KeyValuePair<Address, string>(w.Address, json));
                        _sent[w.Address.Key] = new KeyValuePair<Address, string>(w.Address, json);
                    }
                    _baseline[w.Address.Key] = json;
                }
            }
            _watched = watched;
            WarnIfSlow(timer, "comparing");
            if (changed.Count == 0)
            {
                return null;
            }
            return Encode(changed);
        }

        private static byte[] Encode(List<KeyValuePair<Address, string>> changed)
        {
            // Slots before grids: equipping a backpack or vest resizes the matching storage.
            changed.Sort((x, y) => Order(x.Key).CompareTo(Order(y.Key)));
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(changed.Count);
                foreach (KeyValuePair<Address, string> c in changed)
                {
                    c.Key.Write(w);
                    w.Write(c.Value);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        private static int Order(Address a) => a.Kind == Kind.InventoryFields ? 0 : a.Kind == Kind.Creature ? (IsGrid(a) ? 2 : 1) : 3;

        // Inventory.AllContainers is storages (backpack, vest) followed by slots.
        private static bool IsGrid(Address a) => a.B < 2;

        public static string Describe(byte[] payload)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(payload)))
            {
                int count = r.ReadInt32();
                List<string> keys = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    keys.Add(Address.Read(r).Key);
                    r.ReadString();
                }
                return string.Join(" ", keys);
            }
        }

        private static bool IsEmptyStorage(Watched w) => w.Storage != null && w.Storage.Empty;

        private static string Serialize(Watched w)
        {
            if (w.Storage != null)
            {
                return SaveToJSON.CreateNode(w.Storage).ToString();
            }
            JSONObject node = new JSONObject();
            foreach (MemberInfo m in InventoryFields)
            {
                object value = m is FieldInfo f ? f.GetValue(w.Inventory) : ((PropertyInfo)m).GetValue(w.Inventory);
                node[m.Name] = SaveToJSON.CreateNode(value);
            }
            return node.ToString();
        }

        private static List<Watched> CollectWatched(State state, Player merc)
        {
            List<Watched> result = new List<Watched>();
            CellPosition center = merc.CreatureData.Position;
            bool Near(CellPosition p) => Math.Max(Math.Abs(p.X - center.X), Math.Abs(p.Y - center.Y)) <= Reach;

            foreach (Creature creature in state.Get<Creatures>().Values)
            {
                if (creature?.CreatureData?.Inventory != null && (creature == merc || Near(creature.CreatureData.Position)))
                {
                    AddInventory(result, Kind.Creature, creature.CreatureData.UniqueId, creature.CreatureData.Inventory, withFields: true);
                }
            }

            ItemsOnFloor floor = state.Get<ItemsOnFloor>();
            MapObstacles obstacles = state.Get<MapObstacles>();
            HashSet<int> seenObstacles = new HashSet<int>();
            List<MapObstacle> found = new List<MapObstacle>();
            for (int dx = -Reach; dx <= Reach; dx++)
            {
                for (int dy = -Reach; dy <= Reach; dy++)
                {
                    int x = center.X + dx, y = center.Y + dy;
                    ItemOnFloor pile = floor.Get(x, y);
                    if (pile != null)
                    {
                        result.Add(new Watched { Address = new Address { Kind = Kind.Floor, A = x, B = y }, Storage = pile.Storage });
                    }
                    found.Clear();
                    if (!obstacles.TryGetObstaclesAt(new CellPosition(x, y), found))
                    {
                        continue;
                    }
                    foreach (MapObstacle obstacle in found)
                    {
                        if (obstacle == null || !seenObstacles.Add(obstacle.UniqueId))
                        {
                            continue;
                        }
                        CorpseStorage corpse = obstacle.GetComponent<CorpseStorage>();
                        if (corpse?.CreatureData?.Inventory != null)
                        {
                            AddInventory(result, Kind.Corpse, obstacle.UniqueId, corpse.CreatureData.Inventory, withFields: false);
                        }
                        Store store = obstacle.GetComponent<Store>();
                        if (store?.storage != null)
                        {
                            result.Add(new Watched { Address = new Address { Kind = Kind.Store, A = obstacle.UniqueId }, Storage = store.storage });
                        }
                        QuestInteractiveStorage quest = obstacle.GetComponent<QuestInteractiveStorage>();
                        if (quest?.Storage != null)
                        {
                            result.Add(new Watched { Address = new Address { Kind = Kind.Quest, A = obstacle.UniqueId }, Storage = quest.Storage });
                        }
                        // The shuttle's menu opens the ship cargo and the shuttle cargo.
                        if (obstacle.GetComponent<Shuttle>() != null)
                        {
                            AddCargo(result, state);
                        }
                    }
                }
            }
            return result;
        }

        private static void AddCargo(List<Watched> result, State state)
        {
            List<ItemStorage> ship = StationOps.Global<MagnumCargo>(state)?.ShipCargo;
            for (int i = 0; ship != null && i < ship.Count; i++)
            {
                result.Add(new Watched { Address = new Address { Kind = Kind.Cargo, A = i }, Storage = ship[i] });
            }
            ItemStorage shuttle = ShuttleCargo(state);
            if (shuttle != null)
            {
                result.Add(new Watched { Address = new Address { Kind = Kind.Cargo, A = ShuttleCargoIndex }, Storage = shuttle });
            }
        }

        private static ItemStorage ShuttleCargo(State state) => StationOps.Global<MagnumProgression>(state)?.GetDepartment<ShuttleCargoDepartment>()?.ShuttleCargo;

        private static void AddInventory(List<Watched> result, Kind kind, int owner, Inventory inventory, bool withFields)
        {
            if (withFields)
            {
                result.Add(new Watched { Address = new Address { Kind = Kind.InventoryFields, A = owner }, Inventory = inventory });
            }
            List<ItemStorage> containers = inventory.AllContainers;
            for (int i = 0; i < containers.Count; i++)
            {
                if (containers[i] != null)
                {
                    result.Add(new Watched { Address = new Address { Kind = kind, A = owner, B = i }, Storage = containers[i] });
                }
            }
        }

        // ---------------------------------------------------------------- applying (other machine)

        public static void Apply(State state, byte[] payload)
        {
            using RngGuard _ = KeepRng();
            Creatures creatures = state.Get<Creatures>();
            Dictionary<Creature, WeaponSlotType> touchedCreatures = new Dictionary<Creature, WeaponSlotType>();
            List<ItemOnFloor> touchedFloor = new List<ItemOnFloor>();
            List<MapObstacle> touchedObstacles = new List<MapObstacle>();

            using (BinaryReader r = new BinaryReader(new MemoryStream(payload)))
            {
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    Address a = Address.Read(r);
                    JSONNode node = JSON.Parse(r.ReadString());
                    switch (a.Kind)
                    {
                        case Kind.InventoryFields:
                        {
                            Creature creature = creatures.GetCreature(a.A);
                            if (creature == null)
                            {
                                throw new InvalidDataException($"No creature {a.A} for inventory fields.");
                            }
                            Touch(touchedCreatures, creature);
                            LoadFromJSON.LoadFieldsAndProperties(creature.CreatureData.Inventory, node, false);
                            break;
                        }
                        case Kind.Creature:
                        {
                            Creature creature = creatures.GetCreature(a.A);
                            if (creature == null)
                            {
                                throw new InvalidDataException($"No creature {a.A} for storage {a.Key}.");
                            }
                            Touch(touchedCreatures, creature);
                            Load(creature.CreatureData.Inventory.AllContainers, a, node);
                            break;
                        }
                        case Kind.Corpse:
                        {
                            MapObstacle obstacle = Obstacle(state, a);
                            Load(obstacle.GetComponent<CorpseStorage>().CreatureData.Inventory.AllContainers, a, node);
                            touchedObstacles.Add(obstacle);
                            break;
                        }
                        case Kind.Store:
                        {
                            MapObstacle obstacle = Obstacle(state, a);
                            obstacle.GetComponent<Store>().storage.LoadJSON(node);
                            touchedObstacles.Add(obstacle);
                            break;
                        }
                        case Kind.Quest:
                        {
                            MapObstacle obstacle = Obstacle(state, a);
                            obstacle.GetComponent<QuestInteractiveStorage>().Storage.LoadJSON(node);
                            touchedObstacles.Add(obstacle);
                            break;
                        }
                        case Kind.Cargo:
                            (Resolve(state, a) ?? throw new InvalidDataException($"No cargo storage {a.Key}.")).LoadJSON(node);
                            break;
                        case Kind.Floor:
                        {
                            ItemOnFloor pile = state.Get<ItemsOnFloor>().GetOrCreate(new CellPosition(a.A, a.B));
                            pile.Storage.LoadJSON(node);
                            touchedFloor.Add(pile);
                            break;
                        }
                    }
                }
            }

            // Loading bypasses the storage events, so redo what their listeners would have done.
            foreach (KeyValuePair<Creature, WeaponSlotType> t in touchedCreatures)
            {
                Inventory inventory = t.Key.CreatureData.Inventory;
                inventory.RecalculateCurrentWeight();
                foreach (string name in InventoryEvents)
                {
                    Raise(inventory, name);
                }
                if (inventory.CurrentSlotType != t.Value)
                {
                    Raise(inventory, "OnWeaponSwaped");
                }
            }
            foreach (ItemOnFloor pile in touchedFloor)
            {
                FloorRefresh?.Invoke(pile, Array.Empty<object>());
            }
            foreach (MapObstacle obstacle in touchedObstacles)
            {
                obstacle.RefreshVisual();
            }
            RefreshScreens();
        }

        // For ShipSync, which keeps roster mercs' gear (not creatures) in sync the same way.
        public static string SerializeInventoryFields(Inventory inventory) => Serialize(new Watched { Inventory = inventory });

        public static void LoadInventoryFields(Inventory inventory, JSONNode node) => LoadFromJSON.LoadFieldsAndProperties(inventory, node, false);

        public static void AfterInventoryLoad(Inventory inventory)
        {
            inventory.RecalculateCurrentWeight();
            foreach (string name in InventoryEvents)
            {
                Raise(inventory, name);
            }
        }

        private static void Touch(Dictionary<Creature, WeaponSlotType> touched, Creature creature)
        {
            if (!touched.ContainsKey(creature))
            {
                touched[creature] = creature.CreatureData.Inventory.CurrentSlotType;
            }
        }

        private static void Load(List<ItemStorage> containers, Address a, JSONNode node)
        {
            if (a.B < 0 || a.B >= containers.Count || containers[a.B] == null)
            {
                throw new InvalidDataException($"No storage {a.Key}.");
            }
            containers[a.B].LoadJSON(node);
        }

        private static MapObstacle Obstacle(State state, Address a)
        {
            return state.Get<MapObstacles>().Get(a.A) ?? throw new InvalidDataException($"No obstacle {a.A} for storage {a.Key}.");
        }

        public static void RefreshScreens()
        {
            try
            {
                DungeonHudScreen hud = UI.Get<DungeonHudScreen>();
                hud.RefreshVest();
                hud.RefreshWeapon();
                if (UI.IsShowing<InventoryScreen>())
                {
                    UI.Get<InventoryScreen>().RefreshItemsList();
                }
            }
            catch (Exception e)
            {
                Log.Warn("Screen refresh after item sync failed: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- item references

        // An item argument of a replayed action: its place in a storage both machines agree on, or,
        // for an item that is in no storage (being dragged), a serialized copy.
        public static void WriteItem(BinaryWriter w, State state, BasePickupItem item)
        {
            using RngGuard _ = KeepRng();
            if (item == null)
            {
                w.Write((byte)0);
                return;
            }
            if (item.Storage != null && TryAddress(state, item.Storage, out Address a))
            {
                int index = item.Storage.Items.IndexOf(item);
                if (index >= 0)
                {
                    w.Write((byte)1);
                    a.Write(w);
                    w.Write(index);
                    return;
                }
            }
            w.Write((byte)2);
            w.Write(SaveToJSON.CreateNode(item).ToString());
        }

        public static BasePickupItem ReadItem(BinaryReader r, State state)
        {
            switch (r.ReadByte())
            {
                case 0:
                    return null;
                case 1:
                {
                    Address a = Address.Read(r);
                    int index = r.ReadInt32();
                    ItemStorage storage = Resolve(state, a);
                    if (storage == null || index >= storage.Items.Count)
                    {
                        throw new InvalidDataException($"No item {index} in storage {a.Key}.");
                    }
                    return storage.Items[index];
                }
                default:
                    using (KeepRng())
                    {
                        return (BasePickupItem)CreateObjectFromJson.Invoke(null, new object[] { JSON.Parse(r.ReadString()), typeof(BasePickupItem) });
                    }
            }
        }

        private static bool TryAddress(State state, ItemStorage storage, out Address address)
        {
            foreach (Watched w in _watched)
            {
                if (w.Storage == storage)
                {
                    address = w.Address;
                    return true;
                }
            }
            // Not next to the active merc: look everywhere.
            foreach (Creature creature in state.Get<Creatures>().Values)
            {
                int index = creature?.CreatureData?.Inventory?.AllContainers.IndexOf(storage) ?? -1;
                if (index >= 0)
                {
                    address = new Address { Kind = Kind.Creature, A = creature.CreatureData.UniqueId, B = index };
                    return true;
                }
            }
            foreach (ItemOnFloor pile in state.Get<ItemsOnFloor>().Values)
            {
                if (pile != null && pile.Storage == storage)
                {
                    address = new Address { Kind = Kind.Floor, A = pile.pos.X, B = pile.pos.Y };
                    return true;
                }
            }
            foreach (MapObstacle obstacle in state.Get<MapObstacles>().Obstacles)
            {
                if (obstacle == null)
                {
                    continue;
                }
                int index = obstacle.GetComponent<CorpseStorage>()?.CreatureData?.Inventory?.AllContainers.IndexOf(storage) ?? -1;
                if (index >= 0)
                {
                    address = new Address { Kind = Kind.Corpse, A = obstacle.UniqueId, B = index };
                    return true;
                }
                if (obstacle.GetComponent<Store>()?.storage == storage)
                {
                    address = new Address { Kind = Kind.Store, A = obstacle.UniqueId };
                    return true;
                }
                if (obstacle.GetComponent<QuestInteractiveStorage>()?.Storage == storage)
                {
                    address = new Address { Kind = Kind.Quest, A = obstacle.UniqueId };
                    return true;
                }
            }
            address = default;
            return false;
        }

        private static Watched Find(State state, Address a)
        {
            if (a.Kind == Kind.InventoryFields)
            {
                Inventory inventory = state.Get<Creatures>().GetCreature(a.A)?.CreatureData.Inventory;
                return inventory == null ? null : new Watched { Address = a, Inventory = inventory };
            }
            ItemStorage storage = Resolve(state, a);
            return storage == null ? null : new Watched { Address = a, Storage = storage };
        }

        private static ItemStorage Resolve(State state, Address a)
        {
            switch (a.Kind)
            {
                case Kind.Creature:
                {
                    List<ItemStorage> containers = state.Get<Creatures>().GetCreature(a.A)?.CreatureData.Inventory.AllContainers;
                    return containers != null && a.B < containers.Count ? containers[a.B] : null;
                }
                case Kind.Corpse:
                {
                    List<ItemStorage> containers = state.Get<MapObstacles>().Get(a.A)?.GetComponent<CorpseStorage>()?.CreatureData.Inventory.AllContainers;
                    return containers != null && a.B < containers.Count ? containers[a.B] : null;
                }
                case Kind.Store: return state.Get<MapObstacles>().Get(a.A)?.GetComponent<Store>()?.storage;
                case Kind.Quest: return state.Get<MapObstacles>().Get(a.A)?.GetComponent<QuestInteractiveStorage>()?.Storage;
                case Kind.Floor: return state.Get<ItemsOnFloor>().Get(a.A, a.B)?.Storage;
                case Kind.Cargo:
                {
                    if (a.A == ShuttleCargoIndex)
                    {
                        return ShuttleCargo(state);
                    }
                    List<ItemStorage> ship = StationOps.Global<MagnumCargo>(state)?.ShipCargo;
                    return ship != null && a.A >= 0 && a.A < ship.Count ? ship[a.A] : null;
                }
                default: return null;
            }
        }
    }
}
