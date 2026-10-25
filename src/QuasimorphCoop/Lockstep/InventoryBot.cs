using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Lockstep
{
    // Test-only: uses the inventory and health screens the way a player would, through the same
    // screen methods the UI calls, so record/playback covers item sync. Each call opens the
    // inventory, does one scripted action, and closes it again (closing ends the action point if
    // the action took one, like in the real game).
    internal sealed class InventoryBot
    {
        private readonly Action<string> _log;
        private readonly System.Random _rng;
        private readonly Dictionary<int, int> _steps = new Dictionary<int, int>();

        public InventoryBot(Action<string> log, System.Random rng)
        {
            _log = log;
            _rng = rng;
        }

        private static readonly string[] Actions = { "drop", "take", "consume", "unload", "reload", "unequip", "equip", "loot", "move", "wound" };

        public void Act(State state, Player player)
        {
            int id = player.CreatureData.UniqueId;
            _steps.TryGetValue(id, out int step);
            _steps[id] = step + 1;
            string action = Actions[step % Actions.Length];
            InventoryScreen screen = UI.Get<InventoryScreen>();
            bool health = action == "wound";
            string result;
            try
            {
                if (health)
                {
                    UI.Chain<HealthScreen>().Show();
                }
                else
                {
                    UI.Chain<InventoryScreen>().Show();
                }
                result = Do(action, state, player, screen);
            }
            catch (Exception e)
            {
                result = "error " + e.GetType().Name + ": " + e.Message;
                Log.Warn("Inventory bot: " + e);
            }
            finally
            {
                if (health)
                {
                    UI.Hide<HealthScreen>();
                }
                else if (UI.IsShowing<InventoryScreen>())
                {
                    AccessTools.Method(typeof(InventoryScreen), "Hide").Invoke(screen, new object[] { true });
                }
            }
            _log($"bot P{player.CreatureData.UniqueId} inv {action}: {result}");
        }

        private string Do(string action, State state, Player player, InventoryScreen screen)
        {
            Inventory inv = player.CreatureData.Inventory;
            ItemStorage floor = state.Get<ItemsOnFloor>().Get(player.CreatureData.Position)?.Storage;
            switch (action)
            {
                case "drop":
                {
                    BasePickupItem item = Pick(inv.BackpackStore.Items.Where(i => !i.Locked));
                    if (item == null)
                    {
                        return "nothing to drop";
                    }
                    // DragController: BeginDrag takes the item out, dropping outside the window puts it on the floor.
                    item.Storage.Remove(item);
                    screen.DragControllerDropOutsideCallback(item);
                    screen.DragControllerInteractionCallback(InventoryInteractionType.DropOutside);
                    return item.Id;
                }
                case "take":
                {
                    BasePickupItem item = floor == null ? null : Pick(floor.Items);
                    if (item == null)
                    {
                        return "floor empty";
                    }
                    AccessTools.Method(typeof(InventoryScreen), "Take").Invoke(screen, new object[] { item, true });
                    return item.Id;
                }
                case "consume":
                {
                    BasePickupItem item = inv.Storages.SelectMany(s => s.Items).FirstOrDefault(i => i.Is<ConsumableRecord>());
                    if (item == null)
                    {
                        return "no consumable";
                    }
                    screen.PutConsumableInCharacter(item, spendTurn: true);
                    return item.Id;
                }
                case "unload":
                {
                    BasePickupItem weapon = inv.CurrentWeapon;
                    if (weapon == null || weapon.Comp<WeaponComponent>() == null || weapon.Comp<WeaponComponent>().Melee)
                    {
                        return "no gun";
                    }
                    return screen.TryUnloadWeapon(weapon, playSound: false) ? weapon.Id : "couldn't unload";
                }
                case "reload":
                {
                    BasePickupItem weapon = inv.CurrentWeapon;
                    if (weapon == null || weapon.Comp<WeaponComponent>() == null || weapon.Comp<WeaponComponent>().Melee)
                    {
                        return "no gun";
                    }
                    // Pick the ammo back up first if the unload left it on the floor.
                    BasePickupItem ammo = floor?.Items.FirstOrDefault(i => i.Is<AmmoRecord>());
                    if (ammo != null)
                    {
                        AccessTools.Method(typeof(InventoryScreen), "Take").Invoke(screen, new object[] { ammo, true });
                    }
                    screen.Reload(weapon);
                    return weapon.Id + $" ammo now {weapon.Comp<WeaponComponent>().CurrentAmmo}";
                }
                case "unequip":
                {
                    BasePickupItem weapon = inv.SecondarySlot.First ?? inv.PrimarySlot.First;
                    if (weapon == null || weapon.Locked)
                    {
                        return "no weapon";
                    }
                    screen.Unequip(weapon);
                    return weapon.Id;
                }
                case "equip":
                {
                    BasePickupItem weapon = floor?.Items.FirstOrDefault(i => i.Is<WeaponRecord>())
                        ?? inv.BackpackStore.Items.FirstOrDefault(i => i.Is<WeaponRecord>());
                    if (weapon == null)
                    {
                        return "no weapon around";
                    }
                    AccessTools.Method(typeof(InventoryScreen), "Equip").Invoke(screen, new object[] { weapon });
                    return weapon.Id;
                }
                case "loot":
                {
                    CorpseStorage corpse = NearbyCorpses(state, player).FirstOrDefault(c => c.CreatureData.Inventory.AllContainers.Any(s => s != null && !s.Empty));
                    if (corpse == null)
                    {
                        return "no corpse with items";
                    }
                    BasePickupItem item = corpse.CreatureData.Inventory.AllContainers.Where(s => s != null).SelectMany(s => s.Items).First();
                    AccessTools.Method(typeof(InventoryScreen), "Take").Invoke(screen, new object[] { item, true });
                    return item.Id;
                }
                case "move":
                {
                    ItemStorage backpack = inv.BackpackStore;
                    BasePickupItem item = Pick(backpack.Items.Where(i => !i.Locked));
                    if (item == null)
                    {
                        return "backpack empty";
                    }
                    CellPosition from = item.InventoryPos;
                    backpack.Remove(item);
                    for (int y = backpack.Height - 1; y >= 0; y--)
                    {
                        for (int x = backpack.Width - 1; x >= 0; x--)
                        {
                            CellPosition to = new CellPosition(x, y);
                            if ((to.X != from.X || to.Y != from.Y) && ItemInteractionSystem.Move(item, backpack, to, sendEvent: true, hasInventoryPos: true))
                            {
                                screen.DragControllerInteractionCallback(InventoryInteractionType.MoveItem);
                                return $"{item.Id} {from.X},{from.Y} -> {x},{y}";
                            }
                        }
                    }
                    backpack.TryPutItem(item, from, hasSpecialPos: true);
                    return item.Id + " no free cell";
                }
                case "wound":
                {
                    BasePickupItem medkit = inv.Storages.SelectMany(s => s.Items).FirstOrDefault(i => i.Is<FixationMedicineRecord>());
                    if (medkit == null)
                    {
                        return "no fixation medkit";
                    }
                    HealthScreen health = UI.Get<HealthScreen>();
                    WoundSlot slot = Traverse.Create(health).Field("_woundSlots").GetValue<List<WoundSlot>>().FirstOrDefault(s => s.HasWoundsToFixate);
                    if (slot == null)
                    {
                        return "no wound to fixate";
                    }
                    health.StartApplyMedkitMode(medkit, HealthScreen.ApplyMedkitMode.Fixate, spendTurn: true);
                    AccessTools.Method(typeof(HealthScreen), "WoundSlotOnClicked").Invoke(health, new object[] { slot });
                    return medkit.Id + " on " + slot.WoundSlotId;
                }
                default:
                    return "?";
            }
        }

        private BasePickupItem Pick(IEnumerable<BasePickupItem> items)
        {
            List<BasePickupItem> list = items.ToList();
            return list.Count == 0 ? null : list[_rng.Next(list.Count)];
        }

        private static IEnumerable<CorpseStorage> NearbyCorpses(State state, Player player)
        {
            CellPosition me = player.CreatureData.Position;
            List<MapObstacle> found = new List<MapObstacle>();
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    found.Clear();
                    if (state.Get<MapObstacles>().TryGetObstaclesAt(new CellPosition(me.X + dx, me.Y + dy), found))
                    {
                        foreach (MapObstacle o in found)
                        {
                            CorpseStorage corpse = o?.GetComponent<CorpseStorage>();
                            if (corpse?.CreatureData?.Inventory != null)
                            {
                                yield return corpse;
                            }
                        }
                    }
                }
            }
        }
    }
}
