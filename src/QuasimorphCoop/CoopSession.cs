using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MGSC;
using UnityEngine;

namespace QuasimorphCoop
{
    // Owns the party for the current mission.
    //
    // The game assumes exactly one Player and reads Creatures.Player ~960 times.
    // Instead of patching all of those, the slot always holds the *active* merc:
    // when a party member's action point comes up we swap it into the slot, so
    // input, HUD, camera and fog of war all follow it unmodified. Party members
    // that are not in the slot are fed back into creature lookups by
    // CreaturesPatches so the rest of the world (AI, targeting) still sees them.
    internal static class CoopSession
    {
        private static readonly List<Player> _party = new List<Player>();

        public static State State { get; private set; }

        public static bool InDungeon => State != null;

        // More than one merc on the map; otherwise every patch is a no-op.
        public static bool Active => _party.Count > 1;

        public static IReadOnlyList<Player> Party => _party;

        // The merc the mission was started with. Saves and mission results always use it.
        public static Player Primary => _party.Count > 0 ? _party[0] : null;

        public static Creatures Creatures => State?.Get<Creatures>();

        public static void Begin(State state)
        {
            State = state;
            _party.Clear();
            Player primary = state.Get<Creatures>().Player;
            if (primary != null)
            {
                _party.Add(primary);
            }
            Log.Info($"Dungeon started; primary merc '{primary?.Mercenary?.ProfileId}'.");
            if (primary != null)
            {
                PartyPersistence.SpawnPending();
                Partner.AutoAdd();
            }
            PartyPersistence.Pending = null;
        }

        public static string DisplayName(Mercenary mercenary)
        {
            if (mercenary == null)
            {
                return "?";
            }
            string name = Localization.Get("monster." + mercenary.ProfileId + ".name");
            return name.StartsWith("monster.") ? mercenary.ProfileId : name;
        }

        public static void End()
        {
            if (State != null)
            {
                Collapse();
            }
            _party.Clear();
            State = null;
            Log.Info("Dungeon finished; session cleared.");
        }

        public static bool IsMember(Creature creature)
        {
            return creature is Player player && _party.Contains(player);
        }

        // Party members not currently in the Creatures.Player slot.
        public static IEnumerable<Player> Inactive
        {
            get
            {
                Player slot = Creatures?.Player;
                foreach (Player player in _party)
                {
                    if (player != null && player != slot)
                    {
                        yield return player;
                    }
                }
            }
        }

        public static Player AddMerc(Mercenary mercenary, CellPosition position)
        {
            Creatures creatures = Creatures;
            TurnController turnController = State.Get<TurnController>();
            MercenaryProfileRecord record = Data.MercenaryProfiles.GetRecord(mercenary.ProfileId);

            // Mirrors CreatureSystem.SpawnPlayer, minus taking over the slot and camera.
            Player player = SingletonMonoBehaviour<CreatureFactory>.Instance.CreatePlayer(mercenary, record, position);
            player.CreatureData.UniqueId = State.Get<RaidMetadata>().NextIdCounter;
            int actionPoints = MoveSystem.GetMaxPossibleActionPoints(player.CreatureData);
            for (int i = 0; i < actionPoints; i++)
            {
                turnController.AddContender(player);
            }
            player.Creature3dView.RefreshItemsRender();
            CreatureSystem.SetBareHandSlot(player.CreatureData);
            CreatureSystem.SetArmStumpSlot(player.CreatureData);

            // The game only listens for the primary's death; route everyone else's through the same handler.
            player.PlayerDied += () => OnMemberDied(player);

            _party.Add(player);
            Log.Info($"Added merc '{mercenary.ProfileId}' ({mercenary.AgentName}) id={player.CreatureData.UniqueId} at {position.X},{position.Y} with {actionPoints} AP; party size {_party.Count}.");
            return player;
        }

        // Puts a party member into the Creatures.Player slot and points the camera, HUD and vision at it.
        public static void MakeActive(Player player, string reason)
        {
            Creatures creatures = Creatures;
            if (player == null || creatures == null || creatures.Player == player)
            {
                return;
            }
            Player previous = creatures.Player;
            creatures.Player = player;

            // A queued path or action belongs to whoever queued it.
            State.Get<PlayerCommandQueue>().Clear();

            State.Get<GameCamera>().Configure(player.transform);
            player.RefreshVision();
            VisibilitySystem.UpdateVisibility(State.Get<ItemsOnFloor>(), creatures, State.Get<MapObstacles>(), State.Get<MapRenderer>(), State.Get<MapGrid>(), State.Get<MapEntities>(), State.Get<FireController>(), State.Get<Visibilities>());
            UI.Get<DungeonHudScreen>().RefreshAllHud(refreshOnEnable: false);

            Log.Info($"Active merc: '{previous?.Mercenary?.ProfileId}' -> '{player.Mercenary?.ProfileId}' ({reason}).");
        }

        // The deployed merc died but another party merc is alive: that one becomes the deployed merc,
        // so saves, extraction and the mission result go with them. The dead one is resolved on the
        // ship like any fallen party merc (see Fallen).
        public static void Promote(Player survivor)
        {
            int index = _party.IndexOf(survivor);
            if (index <= 0)
            {
                return;
            }
            Player fallen = _party[0];
            _party[index] = fallen;
            _party[0] = survivor;
            if (fallen.Mercenary != null)
            {
                fallen.Mercenary.State = MercenaryState.None;
            }
            survivor.Mercenary.State = MercenaryState.InRaid;
            Log.Info($"'{survivor.Mercenary.ProfileId}' is now the deployed merc (was '{fallen.Mercenary?.ProfileId}').");
        }

        // Quietly swaps the slot without touching camera/HUD; used around saves.
        public static Player SwapSlotSilently(Player player)
        {
            Creatures creatures = Creatures;
            Player previous = creatures.Player;
            creatures.Player = player;
            return previous;
        }

        // Back to a normal single-merc mission: primary in the slot, extras out of the turn order.
        // Their Mercenary data is shared with the roster, so gear and wounds they picked up stay with them.
        public static void Collapse()
        {
            if (!Active)
            {
                return;
            }
            Creatures creatures = Creatures;
            TurnController turnController = State.Get<TurnController>();
            if (creatures != null)
            {
                creatures.Player = Primary;
            }
            foreach (Player player in _party.Skip(1))
            {
                turnController?.RemoveContender(player);
            }
            Log.Info($"Collapsed party of {_party.Count} back to primary.");
            _party.RemoveRange(1, _party.Count - 1);
        }

        private const ObstacleType BlockingMask = ObstacleType.Wall | ObstacleType.Obj | ObstacleType.Deep | ObstacleType.Fence
            | ObstacleType.Monster | ObstacleType.PlayerAlly | ObstacleType.Player | ObstacleType.ClosedDoor
            | ObstacleType.BlockedDoor;

        // Null if a merc can stand on the cell, otherwise why not.
        public static string WhyCellBlocked(CellPosition position, bool allowUnexplored = false)
        {
            MapCell cell = State.Get<MapGrid>().GetCell(position);
            if (cell == null)
            {
                return $"Cell {position.X},{position.Y} is outside the map.";
            }
            ObstacleType mask = allowUnexplored ? BlockingMask : BlockingMask | ObstacleType.Unexplored;
            if ((cell.PassConditionMask & mask) != 0 || Creatures.GetCreature(position) != null)
            {
                return $"Cell {position.X},{position.Y} is blocked ({cell.PassConditionMask}).";
            }
            return null;
        }

        // The cell itself if free, else the nearest free cell in growing square rings.
        public static bool FindFreeCellNear(CellPosition center, out CellPosition result)
        {
            foreach (bool allowUnexplored in new[] { false, true })
            {
                for (int radius = 0; radius <= 6; radius++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        for (int dy = -radius; dy <= radius; dy++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                            {
                                continue;
                            }
                            CellPosition candidate = new CellPosition(center.X + dx, center.Y + dy);
                            if (WhyCellBlocked(candidate, allowUnexplored) == null)
                            {
                                result = candidate;
                                return true;
                            }
                        }
                    }
                }
            }
            result = center;
            return false;
        }

        public static Player FirstAliveOtherThan(Player player)
        {
            return _party.FirstOrDefault(p => p != null && p != player && p.CreatureData.Health.Alive);
        }

        public static bool AnyAlive()
        {
            return _party.Any(p => p != null && p.CreatureData.Health.Alive);
        }

        private static void OnMemberDied(Player player)
        {
            Log.Info($"Merc '{player.Mercenary?.ProfileId}' died (PlayerDied event).");
            if (AnyAlive())
            {
                return;
            }
            // Last one down: hand over to the game's normal death flow.
            DungeonGameMode gameMode = SingletonMonoBehaviour<DungeonGameMode>.Instance;
            if (gameMode != null)
            {
                AccessTools.Method(typeof(DungeonGameMode), "OnPlayerDied").Invoke(gameMode, Array.Empty<object>());
            }
        }
    }
}
