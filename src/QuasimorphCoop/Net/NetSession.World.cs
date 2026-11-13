using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MGSC;
using SimpleJSON;
using UnityEngine;

namespace QuasimorphCoop.Net
{
    // The friend's copy of the ship mirrors the host's star map (see docs/ship-sync.md).
    //
    // The friend's game no longer simulates the ship layer (GameLoop.Tick_Space keeps only the
    // clock, orbit rings and the travel animation; see WorldPatches). The host sends the campaign
    // components that changed, serialized the way the game saves them, and the friend's game loads
    // them into its live components the way the game loads a save (LoadJSON keeps each component
    // instance). Travel is started on both by a Travel message and kept together by the clock.
    internal static partial class NetSession
    {
        // Load order, as in ComponentsLayout.DeserializeGlobalComponents. SpaceTime has its own
        // message (the clock); Difficulty, AchievementRunState, DebugData and UserMods aren't sent.
        private static readonly Type[] WorldOrder =
        {
            typeof(MagnumProjects), typeof(ItemsPrices), typeof(Mercenaries), typeof(Factions), typeof(Stations),
            typeof(Missions), typeof(Shippings), typeof(TravelMetadata), typeof(MagnumProgression), typeof(MagnumCargo),
            typeof(News), typeof(RaidMetadata), typeof(Statistics), typeof(StoryTriggers), typeof(SpaceTickTimers),
            typeof(MonsterTransfer), typeof(CombatLog)
        };

        // Up to a few MB each late in a campaign: serialized one per second, in turn.
        private static readonly Type[] WorldBig = { typeof(Stations), typeof(Missions), typeof(Shippings) };

        private const float WorldInterval = 1f;

        // Host.
        private static bool _friendOnShip;
        private static readonly Dictionary<string, string> _worldSent = new Dictionary<string, string>();
        private static int _worldVersion;
        private static float _nextWorldSmall;
        private static float _nextWorldBig;
        private static int _worldBigIndex;
        private static float _nextSlowWarning;

        // Friend: newest received JSON per component, waiting for the screens to close.
        private static readonly Dictionary<string, string> _worldPending = new Dictionary<string, string>();
        private static int _worldPendingVersion;
        private static readonly Dictionary<string, int> _worldVersionOf = new Dictionary<string, int>();
        private static bool _worldNoticeShown;

        // The friend's copy runs no ship simulation of its own.
        public static bool MirroringShip => ShipClient;

        private static void ResetWorld()
        {
            _friendOnShip = false;
            _worldSent.Clear();
            _worldPending.Clear();
            _worldVersionOf.Clear();
            _worldPendingVersion = 0;
            _worldNoticeShown = false;
        }

        // A message from the friend's copy of the ship: they're there, so start streaming.
        private static void HostFriendOnShip(bool arrived)
        {
            if (_role != Role.Host)
            {
                return;
            }
            if (arrived || !_friendOnShip)
            {
                // Their copy came from a save that may be old: send everything once.
                _worldSent.Clear();
                _nextWorldSmall = _nextWorldBig = 0f;
                _worldBigIndex = 0;
            }
            _friendOnShip = true;
        }

        // ---------------------------------------------------------------- host

        private static void HostWorldTick()
        {
            if (!ShipHost || !_onShip || !_friendOnShip || !(SteamNet.Instance?.HasPeer ?? false))
            {
                return;
            }
            State global = GetGlobalState();
            if (global == null)
            {
                return;
            }
            float now = Time.unscaledTime;
            List<Type> due = new List<Type>();
            bool full = _worldSent.Count == 0;
            if (full || now >= _nextWorldSmall)
            {
                _nextWorldSmall = now + WorldInterval;
                due.AddRange(WorldOrder.Where(t => full || !WorldBig.Contains(t)));
            }
            if (!full && now >= _nextWorldBig)
            {
                _nextWorldBig = now + WorldInterval;
                due.Add(WorldBig[_worldBigIndex++ % WorldBig.Length]);
            }
            if (due.Count == 0)
            {
                return;
            }
            TravelMetadata travel = global.Get<TravelMetadata>();
            Stopwatch watch = Stopwatch.StartNew();
            List<KeyValuePair<string, string>> changed = new List<KeyValuePair<string, string>>();
            foreach (Type type in due)
            {
                // Mid-flight the saved travel fields say "nowhere"; the Travel message covers the flight.
                if (type == typeof(TravelMetadata) && travel != null && travel.IsInTravel)
                {
                    continue;
                }
                object component = global.Get(type);
                if (component == null)
                {
                    continue;
                }
                string json;
                try
                {
                    json = SaveToJSON.CreateNode(component).ToString();
                }
                catch (Exception e)
                {
                    Log.Error($"Couldn't serialize {type.Name} for the friend's ship: {e}");
                    continue;
                }
                if (!_worldSent.TryGetValue(type.FullName, out string sent) || sent != json)
                {
                    _worldSent[type.FullName] = json;
                    changed.Add(new KeyValuePair<string, string>(type.FullName, json));
                }
            }
            watch.Stop();
            if (watch.ElapsedMilliseconds > 40 && now >= _nextSlowWarning)
            {
                _nextSlowWarning = now + 30f;
                Log.Warn($"Serializing the star map for the friend took {watch.ElapsedMilliseconds} ms ({string.Join(" ", due.Select(t => t.Name))}).");
            }
            if (changed.Count == 0)
            {
                return;
            }
            int version = ++_worldVersion;
            SendShip(ShipMsg.World, w =>
            {
                w.Write(version);
                w.Write(changed.Count);
                foreach (KeyValuePair<string, string> entry in changed)
                {
                    w.Write(entry.Key);
                    w.Write(entry.Value);
                }
            });
            Log.Info($"Sent star map v{version}: {string.Join(" ", changed.Select(e => e.Key.Substring(e.Key.LastIndexOf('.') + 1) + "(" + e.Value.Length + ")"))}.");
        }

        // TravelSystem.StartSpaceshipTravel postfix.
        public static void OnTravelStarted(TravelMetadata travel, string from, string to)
        {
            if (!ShipHost || !_onShip || !_friendOnShip)
            {
                return;
            }
            int bramfaturaAfter = travel.BramfaturaCounterAfterTravel;
            SendShip(ShipMsg.Travel, w => { w.Write(from ?? ""); w.Write(to ?? ""); w.Write(bramfaturaAfter); });
            Log.Info($"Sent travel {from} -> {to}.");
        }

        // ---------------------------------------------------------------- friend

        private static void OnWorld(BinaryReader r)
        {
            int version = r.ReadInt32();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = r.ReadString();
                string json = r.ReadString();
                // Messages can complete out of order (a big one still arriving in chunks); never let
                // an older copy of a component replace a newer one.
                if (_worldVersionOf.TryGetValue(name, out int newest) && newest > version)
                {
                    continue;
                }
                _worldVersionOf[name] = version;
                _worldPending[name] = json;
            }
            _worldPendingVersion = Math.Max(_worldPendingVersion, version);
        }

        private static void ClientWorldTick()
        {
            if (_worldPending.Count == 0 || !ShipClient || _editingOwnMerc || Dragging())
            {
                return;
            }
            // Open screens (trade, mission prep, the arsenal) hold the objects a load replaces; the
            // update waits until the friend is back on the star map.
            try
            {
                if (UI.IsAnyShowing(typeof(SpaceHudScreen), typeof(InputLockerScreen)))
                {
                    return;
                }
            }
            catch (Exception)
            {
                return;
            }
            ApplyWorld();
        }

        private static void ApplyWorld()
        {
            State global = GetGlobalState();
            SpaceGameMode mode = SingletonMonoBehaviour<SpaceGameMode>.Instance;
            if (global == null || mode == null)
            {
                return;
            }
            TravelMetadata travel = global.Get<TravelMetadata>();
            string previousObject = travel?.CurrentSpaceObject;
            List<string> loaded = new List<string>();
            foreach (Type type in WorldOrder)
            {
                if (!_worldPending.TryGetValue(type.FullName, out string json))
                {
                    continue;
                }
                // A flight in progress here owns these fields until it lands.
                if (type == typeof(TravelMetadata) && travel != null && travel.IsInTravel)
                {
                    continue;
                }
                _worldPending.Remove(type.FullName);
                object component = global.Get(type);
                if (component == null)
                {
                    continue;
                }
                try
                {
                    component.LoadJSON(JSON.Parse(json)["Content"]);
                    loaded.Add(type.Name);
                }
                catch (Exception e)
                {
                    Log.Error($"Couldn't load the host's {type.Name}: {e}");
                }
            }
            if (loaded.Count == 0)
            {
                return;
            }
            try
            {
                if (loaded.Contains(nameof(Missions)))
                {
                    PresetIdRegistry.NormalizeMissions(global.Get<Missions>());
                }
                if (travel != null && !travel.IsInTravel && !string.IsNullOrEmpty(travel.CurrentSpaceObject) && travel.CurrentSpaceObject != previousObject)
                {
                    PlaceShipAt(mode, travel.CurrentSpaceObject);
                }
                RefreshStarMap(travel);
            }
            catch (Exception e)
            {
                Log.Error("Couldn't refresh the star map after the host's update: " + e);
            }
            Log.Info($"Applied star map v{_worldPendingVersion}: {string.Join(" ", loaded)}.");
            if (!_worldNoticeShown)
            {
                _worldNoticeShown = true;
                Notify($"Co-op: your ship and star map follow {PeerName}'s. Only your merc's gear is sent back; trades and other changes here aren't kept.");
            }
        }

        private static void OnTravel(BinaryReader r)
        {
            string from = r.ReadString(), to = r.ReadString();
            int bramfaturaAfter = r.ReadInt32();
            if (!ShipClient)
            {
                return;
            }
            SpaceGameMode mode = SingletonMonoBehaviour<SpaceGameMode>.Instance;
            TravelMetadata travel = mode?.Get<TravelMetadata>();
            if (travel == null)
            {
                return;
            }
            if (travel.IsInTravel)
            {
                Log.Warn($"Host started travel {from} -> {to} while this copy is still flying to {travel.TargetSpaceObject}.");
                return;
            }
            try
            {
                if (!string.IsNullOrEmpty(from) && travel.CurrentSpaceObject != from)
                {
                    PlaceShipAt(mode, from);
                }
                TravelSystem.StartSpaceshipTravel(mode.Get<News>(), mode.Get<SpaceTime>(), mode.Get<Spaceship>(), mode.Get<SpaceObjects>(), travel, to);
                travel.BramfaturaCounterAfterTravel = bramfaturaAfter;
                Log.Info($"Following the host's travel {from} -> {to}.");
            }
            catch (Exception e)
            {
                Log.Error("Couldn't follow the host's travel: " + e);
            }
        }

        // Puts the ship in orbit around a space object without the arrival's random event.
        private static void PlaceShipAt(SpaceGameMode mode, string objectId)
        {
            TravelSystem.MoveSpaceshipOnOrbit(mode.Get<Stations>(), mode.Get<Factions>(), mode.Get<StoryTriggers>(), mode.Get<Spaceship>(),
                mode.Get<SpaceObjects>(), mode.Get<TravelMetadata>(), mode.Get<Difficulty>(), objectId, updatePos: true, triggerSpaceEvent: false);
            Log.Info($"Moved the ship to {objectId} to match the host.");
        }

        // The star map's panels hold the Station and Mission objects a load replaced.
        private static void RefreshStarMap(TravelMetadata travel)
        {
            SpaceHudScreen hud = UI.Get<SpaceHudScreen>();
            if (hud == null)
            {
                return;
            }
            if (travel != null && !travel.IsInTravel && !travel.IsInBramfatura)
            {
                hud.SpaceStationsWindow.Initialize(travel.CurrentSpaceObject, allowClicks: true);
            }
            hud.Newsline.RefreshAllEvents();
            hud.Questlines.Refresh();
        }

        // ---------------------------------------------------------------- travel timer

        // Sent with the clock: the host's flight timer, so both ships fly (and pause) together.
        private static bool _hostTravelling;
        private static bool _hostTravelRunning;
        private static float _hostTravelTime;
        private static float _hostClockAt;

        private static void WriteTravelClock(BinaryWriter w, TravelMetadata travel)
        {
            bool travelling = travel != null && travel.IsInTravel;
            w.Write(travelling);
            w.Write(travelling && travel.TravelTimer.Started);
            w.Write(!travelling ? 0f : travel.TravelTimer.Started ? travel.TravelTimer.Time : travel.FlightTimeBeforePause);
        }

        private static void ReadTravelClock(BinaryReader r)
        {
            _hostTravelling = r.ReadBoolean();
            _hostTravelRunning = r.ReadBoolean();
            _hostTravelTime = r.ReadSingle();
            _hostClockAt = Time.time;
        }

        private static void ApplyHostTravelClock(TravelMetadata travel)
        {
            if (travel == null || !travel.IsInTravel || !_hostTravelling)
            {
                return;
            }
            float target = _hostTravelTime + (_hostTravelRunning ? Time.time - _hostClockAt : 0f);
            if (_hostTravelRunning)
            {
                travel.FlightTimeBeforePause = 0f;
                if (!travel.TravelTimer.Started)
                {
                    travel.TravelTimer.Start();
                }
                travel.TravelTimer.AddTime(target - travel.TravelTimer.Time);
            }
            else
            {
                travel.TravelTimer.Pause();
                travel.FlightTimeBeforePause = Mathf.Max(target, 0.001f);
            }
        }
    }
}
