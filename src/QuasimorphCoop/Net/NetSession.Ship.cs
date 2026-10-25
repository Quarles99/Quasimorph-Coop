using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MGSC;
using UnityEngine;

namespace QuasimorphCoop.Net
{
    // Between missions (see ShipSync): the friend equips their merc on their copy of the host's
    // ship and the host applies the result to the real one.
    //
    //   friend opens the arsenal for their merc  -> Request(arsenal)  -> host sends Snapshot
    //   friend closes it                        -> Change(changed storages, snapshot version)
    //   host checks and applies (or refuses)    -> Result(ok, reason, fresh Snapshot)
    internal static partial class NetSession
    {
        // Ready: friend -> host, pressed Start (with the mission or station they had picked).
        // PickMerc: friend -> host, wants to play another roster merc. Picked: host -> friend, the merc
        // the friend now plays (answer to PickMerc, or the host changed the partner).
        private enum ShipMsg : byte { Request = 1, Snapshot, Change, Result, Ready, PickMerc, Picked }

        private static int _shipBlobId;
        private static readonly Dictionary<int, byte[][]> _shipIncoming = new Dictionary<int, byte[][]>();

        // Host: what the friend was last sent.
        private static int _shipVersion;
        private static Dictionary<string, string> _shipSent;
        private static bool _friendEditing;
        private static bool _startWarned;
        private static bool _friendReady;

        // Friend: the snapshot this copy was last brought in line with, as this copy serializes it.
        private static int _shipAppliedVersion;
        private static Dictionary<string, string> _shipBaseline;
        private static bool _editingOwnMerc;

        // A snapshot that arrived while an item was being dragged. A held item is in no storage,
        // so loading then would put it back in its storage while the drag still drops a copy.
        private static Dictionary<string, string> _pendingShip;
        private static int _pendingShipVersion;

        private static bool ShipHost => _role == Role.Host && _phase >= Phase.Connected && _loopback == Loopback.None;

        private static bool ShipClient => _role == Role.Client && _phase == Phase.Connected && _loopback == Loopback.None && _onShip;

        private static string PeerName => SteamNet.Instance?.PeerName ?? "your friend";

        private static Mercenary FriendMerc(State global) => string.IsNullOrEmpty(_friendMercId) ? null : global?.Get<Mercenaries>()?.Get(_friendMercId);

        private static string FriendMercName(State global) => CoopSession.DisplayName(FriendMerc(global));

        // ---------------------------------------------------------------- hooks

        // The friend arrived on (their copy of) the ship.
        private static void OnShipArrived()
        {
            _startWarned = false;
            _friendReady = false;
            if (ShipHost)
            {
                // The friend's copy may have got here first; bring it in line.
                if (FriendMerc(GetGlobalState()) != null)
                {
                    HostSendSnapshot();
                }
                return;
            }
            if (!ShipClient)
            {
                return;
            }
            State global = GetGlobalState();
            if (FriendMerc(global) == null)
            {
                return;
            }
            SendShip(ShipMsg.Request, w => w.Write(false));
            Notify($"Co-op: this is a copy of {PeerName}'s ship. Change {FriendMercName(global)}'s gear in the Equipment screen and it's sent to {PeerName}; other changes here aren't kept.");
        }

        private static void OnShipLeft()
        {
            _editingOwnMerc = false;
            _pendingShip = null;
            if (_role == Role.Host && _friendEditing)
            {
                Log.Info("Left the ship while the friend was still editing.");
            }
            _friendEditing = false;
        }

        public static void OnArsenalOpened(Mercenary merc)
        {
            if (!ShipClient)
            {
                return;
            }
            if (merc != null && merc.ProfileId == _friendMercId)
            {
                _editingOwnMerc = true;
                SendShip(ShipMsg.Request, w => w.Write(true));
            }
            else
            {
                Notify($"Co-op: only {FriendMercName(GetGlobalState())}'s gear is sent to {PeerName}; changes to other mercs or cargo here aren't kept.");
            }
        }

        public static void OnArsenalClosed()
        {
            if (!_editingOwnMerc)
            {
                return;
            }
            _editingOwnMerc = false;
            State global = GetGlobalState();
            Mercenary merc = FriendMerc(global);
            if (!ShipClient)
            {
                return;
            }
            Dictionary<string, string> changes = new Dictionary<string, string>();
            if (merc != null && _shipBaseline != null)
            {
                try
                {
                    changes = ShipSync.Changes(_shipBaseline, ShipSync.Snapshot(global, merc));
                }
                catch (Exception e)
                {
                    Log.Error("Couldn't read the ship's storages: " + e);
                }
            }
            // An empty change still tells the host the friend is done.
            SendShip(ShipMsg.Change, w => { w.Write(_shipAppliedVersion); ShipSync.Write(w, changes); });
            if (changes.Count > 0)
            {
                Log.Info($"Sent gear change ({string.Join(" ", changes.Keys)}) based on ship version {_shipAppliedVersion}.");
                Notify($"Co-op: sending your gear to {PeerName}...");
            }
        }

        // PrepareRaidScreen's start button. Returns false to swallow the click. For the friend it
        // means "ready", with the mission or station they had open as a suggestion.
        public static bool AllowMissionStart(PrepareRaidScreen screen)
        {
            if (ShipClient)
            {
                string suggestion = Suggestion(screen);
                SendShip(ShipMsg.Ready, w => w.Write(suggestion ?? ""));
                Notify($"Co-op: you're ready{(suggestion != null ? $" and suggested {suggestion}" : "")}. {PeerName} starts the mission; you'll join automatically.");
                return false;
            }
            if (ShipHost && _onShip && _friendEditing && !_startWarned)
            {
                _startWarned = true;
                Notify($"Co-op: {PeerName} is still changing their merc's gear. Press Start again to go anyway (their unsent changes would be lost).");
                return false;
            }
            if (ShipHost && _onShip && !_friendReady && FriendMerc(GetGlobalState()) != null && !_startWarned)
            {
                _startWarned = true;
                Notify($"Co-op: {PeerName} hasn't pressed Start yet (that marks them ready). Press Start again to go anyway.");
                return false;
            }
            return true;
        }

        private static string Suggestion(PrepareRaidScreen screen)
        {
            try
            {
                Traverse t = Traverse.Create(screen);
                Mission mission = t.Field("_mission").GetValue<Mission>();
                Station station = t.Field("_station").GetValue<Station>();
                string stationName = station == null ? null : Localization.Get("station." + station.Id + ".name");
                if (mission != null)
                {
                    string name = MissionSystem.GetMissionName(t.Field("_factions").GetValue<Factions>(), mission);
                    return stationName == null ? name : $"{name} at {stationName}";
                }
                return stationName == null ? null : "a visit to " + stationName;
            }
            catch (Exception e)
            {
                Log.Warn("Couldn't read the picked mission: " + e.Message);
                return null;
            }
        }

        public static bool IsShipClient => ShipClient;

        // The merc the friend plays, as this machine knows it.
        public static string FriendMercId => _friendMercId;

        // Friend's Co-op menu on the ship: ask the host for the next available roster merc.
        public static void RequestOwnMerc(State global)
        {
            List<Mercenary> candidates = Partner.Candidates(global);
            if (candidates.Count == 0)
            {
                Notify("Co-op: no other merc is free (alive and not busy on the ship).");
                return;
            }
            int index = candidates.FindIndex(m => m.ProfileId == _friendMercId);
            Mercenary next = candidates[(index + 1) % candidates.Count];
            SendShip(ShipMsg.PickMerc, w => w.Write(next.ProfileId));
            Notify($"Co-op: asking {PeerName} for {CoopSession.DisplayName(next)}...");
        }

        // Host: the partner changed (Co-op menu) while the friend is on the ship; they play it now.
        public static void OnPartnerChanged(State global)
        {
            if (!ShipHost || !_onShip)
            {
                return;
            }
            string partner = Partner.Get(global);
            if (string.IsNullOrEmpty(partner) || partner == _friendMercId)
            {
                return;
            }
            _friendMercId = partner;
            _friendEditing = false;
            SendShip(ShipMsg.Picked, w => { w.Write(true); w.Write(partner); w.Write(""); });
            HostSendSnapshot();
        }

        private static void HostPickMerc(string id)
        {
            State global = GetGlobalState();
            Mercenary merc = global?.Get<Mercenaries>()?.Get(id);
            string why = !_onShip ? "only between missions, on the ship"
                : merc == null ? "no such merc"
                : !Partner.IsAvailable(merc) ? CoopSession.DisplayName(merc) + " isn't free (dead, gone, or busy on the ship)"
                : null;
            if (why != null)
            {
                SendShip(ShipMsg.Picked, w => { w.Write(false); w.Write(_friendMercId ?? ""); w.Write(why); });
                return;
            }
            Partner.Set(global, id);
            _friendMercId = id;
            _friendEditing = false;
            _friendReady = false;
            Notify($"Co-op: {PeerName} now plays {CoopSession.DisplayName(merc)}.");
            SendShip(ShipMsg.Picked, w => { w.Write(true); w.Write(id); w.Write(""); });
            HostSendSnapshot();
        }

        // Recycling runs on a timer in the ship's state, which the friend's copy can't hand over.
        public static bool AllowRecycle()
        {
            if (ShipClient)
            {
                Notify($"Co-op: only {PeerName} can recycle.");
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- messages

        private static void OnShipMessage(ShipMsg kind, BinaryReader r)
        {
            switch (kind)
            {
                case ShipMsg.Request:
                    if (_role == Role.Host)
                    {
                        bool arsenal = r.ReadBoolean();
                        if (HostSendSnapshot())
                        {
                            if (arsenal)
                            {
                                _friendEditing = true;
                                _friendReady = false;
                                Notify($"Co-op: {PeerName} is changing {FriendMercName(GetGlobalState())}'s gear.");
                            }
                        }
                        else if (arsenal)
                        {
                            // (An arrival request needs no answer: the host sends a snapshot when it gets to the ship.)
                            string why = _onShip ? "their merc isn't on this ship" : "the host isn't on the ship yet";
                            SendShip(ShipMsg.Result, w => { w.Write(false); w.Write(why); w.Write(0); ShipSync.Write(w, new Dictionary<string, string>()); });
                        }
                    }
                    break;
                case ShipMsg.Snapshot:
                    if (_role == Role.Client)
                    {
                        int version = r.ReadInt32();
                        ApplyShipSnapshot(version, ShipSync.Read(r));
                    }
                    break;
                case ShipMsg.Change:
                    if (_role == Role.Host)
                    {
                        int version = r.ReadInt32();
                        HostApplyChange(version, ShipSync.Read(r));
                    }
                    break;
                case ShipMsg.Ready:
                    if (_role == Role.Host)
                    {
                        string suggestion = r.ReadString();
                        _friendReady = true;
                        Notify($"Co-op: {PeerName} is ready" + (suggestion.Length > 0 ? $" and suggests {suggestion}." : "."));
                    }
                    break;
                case ShipMsg.PickMerc:
                    if (_role == Role.Host)
                    {
                        HostPickMerc(r.ReadString());
                    }
                    break;
                case ShipMsg.Picked:
                    if (_role == Role.Client)
                    {
                        bool ok = r.ReadBoolean();
                        string id = r.ReadString();
                        string reason = r.ReadString();
                        if (ok)
                        {
                            _friendMercId = id;
                            Notify($"Co-op: you now play {CoopSession.DisplayName(GetGlobalState()?.Get<Mercenaries>()?.Get(id))}.");
                        }
                        else
                        {
                            Notify($"Co-op: {PeerName}'s game didn't switch your merc ({reason}).");
                        }
                    }
                    break;
                case ShipMsg.Result:
                    if (_role == Role.Client)
                    {
                        bool ok = r.ReadBoolean();
                        string reason = r.ReadString();
                        int version = r.ReadInt32();
                        Dictionary<string, string> entries = ShipSync.Read(r);
                        if (ok)
                        {
                            Notify($"Co-op: your gear is saved on {PeerName}'s ship.");
                        }
                        else if (entries.Count == 0)
                        {
                            Notify($"Co-op: couldn't sync your gear with {PeerName} ({reason}).");
                        }
                        else
                        {
                            Notify($"Co-op: {PeerName}'s game didn't take your gear changes ({reason}). Your copy shows their ship again; please redo them.");
                        }
                        if (entries.Count > 0)
                        {
                            ApplyShipSnapshot(version, entries);
                        }
                    }
                    break;
            }
        }

        // Returns false if there's nothing to send (not on the ship).
        private static bool HostSendSnapshot()
        {
            State global = GetGlobalState();
            Mercenary merc = FriendMerc(global);
            if (!_onShip || merc == null)
            {
                return false;
            }
            _shipSent = ShipSync.Snapshot(global, merc);
            int version = ++_shipVersion;
            Dictionary<string, string> sent = _shipSent;
            SendShip(ShipMsg.Snapshot, w => { w.Write(version); ShipSync.Write(w, sent); });
            Log.Info($"Sent ship snapshot v{version} ({sent.Count} storages).");
            return true;
        }

        // The host's side of a gear change. Public for the selftest.
        internal static string HostApplyChange(int version, Dictionary<string, string> change)
        {
            _friendEditing = false;
            State global = GetGlobalState();
            Mercenary merc = FriendMerc(global);
            string reason = null;
            if (!_onShip)
            {
                reason = "you had already left the ship";
            }
            else if (merc == null)
            {
                reason = "their merc isn't on this ship";
            }
            else if (change.Count > 0)
            {
                if (_shipSent == null || version != _shipVersion)
                {
                    reason = "it started from an older copy of the ship";
                }
                else
                {
                    List<string> conflicts = ShipSync.Conflicts(_shipSent, ShipSync.Snapshot(global, merc), change);
                    if (conflicts.Count > 0)
                    {
                        reason = $"the host moved items in the same storage meanwhile ({string.Join(", ", conflicts)})";
                    }
                }
                if (reason == null)
                {
                    try
                    {
                        ShipSync.Load(global, merc, change);
                        RefreshShipScreens();
                        Log.Info($"Applied friend's gear change ({string.Join(" ", change.Keys)}).");
                        Notify($"Co-op: {PeerName} updated {CoopSession.DisplayName(merc)}'s gear.");
                    }
                    catch (Exception e)
                    {
                        Log.Error("Couldn't apply the friend's gear change: " + e);
                        reason = "it couldn't be applied";
                    }
                }
            }
            else
            {
                return null;
            }
            if (reason != null)
            {
                Log.Warn("Refused the friend's gear change: " + reason);
            }
            // Either way the friend's copy goes back in line with the real ship.
            Dictionary<string, string> fresh = new Dictionary<string, string>();
            int freshVersion = 0;
            if (_onShip && merc != null)
            {
                _shipSent = fresh = ShipSync.Snapshot(global, merc);
                freshVersion = ++_shipVersion;
            }
            bool ok = reason == null;
            string why = reason ?? "";
            SendShip(ShipMsg.Result, w => { w.Write(ok); w.Write(why); w.Write(freshVersion); ShipSync.Write(w, fresh); });
            return reason;
        }

        // Per frame.
        public static void ShipTick()
        {
            if (_pendingShip != null && !Dragging())
            {
                Dictionary<string, string> entries = _pendingShip;
                _pendingShip = null;
                ApplyShipSnapshot(_pendingShipVersion, entries);
            }
        }

        private static bool Dragging()
        {
            try
            {
                return UI.Drag != null && UI.Drag.IsDragging;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void ApplyShipSnapshot(int version, Dictionary<string, string> entries)
        {
            State global = GetGlobalState();
            Mercenary merc = FriendMerc(global);
            if (!_onShip || merc == null)
            {
                return;
            }
            if (Dragging())
            {
                _pendingShip = entries;
                _pendingShipVersion = version;
                Log.Info($"Ship snapshot v{version} waits for the drag to end.");
                return;
            }
            try
            {
                ShipSync.Load(global, merc, entries);
                _shipAppliedVersion = version;
                _shipBaseline = ShipSync.Snapshot(global, merc);
                RefreshShipScreens();
                Log.Info($"Applied ship snapshot v{version} ({entries.Count} storages).");
            }
            catch (Exception e)
            {
                Log.Error("Couldn't apply the host's ship snapshot: " + e);
            }
        }

        private static void RefreshShipScreens()
        {
            try
            {
                if (UI.IsShowing<ArsenalScreen>())
                {
                    UI.Get<ArsenalScreen>().RefreshView();
                }
            }
            catch (Exception e)
            {
                Log.Warn("Ship screen refresh failed: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- selftest

        private static bool _shipTesting;
        private static string _equipTestItem;


        // Test-only (selftest=ship): the host's side of a gear change on this machine, with the
        // friend's edits simulated on the same objects. Returns a report starting PASS or FAIL.
        internal static string ShipSelftest(Mercenary merc)
        {
            State global = GetGlobalState();
            bool onShip = _onShip;
            string friend = _friendMercId;
            _shipTesting = true;
            _onShip = true;
            _friendMercId = merc.ProfileId;
            List<string> report = new List<string>();
            bool ok = true;
            void Check(bool condition, string what)
            {
                report.Add((condition ? "ok " : "FAILED ") + what);
                ok &= condition;
            }
            Dictionary<string, string> original = ShipSync.Snapshot(global, merc);
            try
            {
                MagnumCargo cargo = global.Get<MagnumCargo>();
                Check(cargo != null, "ship cargo reachable");
                // Wire format: written, compressed, decompressed and read back unchanged.
                byte[] packed;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    ShipSync.Write(w, original);
                    w.Flush();
                    packed = ShipSync.Compress(ms.ToArray());
                }
                Dictionary<string, string> unpacked = ShipSync.Read(new BinaryReader(new MemoryStream(ShipSync.Decompress(packed))));
                Check(unpacked.Count == original.Count && original.All(kv => unpacked[kv.Key] == kv.Value), $"wire round trip ({packed.Length} bytes compressed)");
                // Host sends a snapshot; the friend moves an item from the cargo into their merc's pack.
                _shipSent = ShipSync.Snapshot(global, merc);
                _shipVersion = 1;
                ItemStorage tab = cargo.ShipCargo.FirstOrDefault(t => !t.Empty);
                Check(tab != null, "cargo has items");
                BasePickupItem item = tab.Items.First();
                tab.Remove(item);
                bool taken = merc.CreatureData.Inventory.TakeOrEquip(item, putIfSlotBusy: true);
                Check(taken, $"merc took {item.Id}");
                Dictionary<string, string> after = ShipSync.Snapshot(global, merc);
                Dictionary<string, string> change = ShipSync.Changes(_shipSent, after);
                report.Add("change: " + string.Join(" ", change.Keys));
                Check(change.Keys.Any(k => k.StartsWith("c")) && change.Keys.Any(k => k.StartsWith("m")), "change covers cargo and merc");
                // Back to what the host has, then the host applies the friend's change.
                ShipSync.Load(global, merc, _shipSent);
                Dictionary<string, string> reverted = ShipSync.Snapshot(global, merc);
                Check(ShipSync.Changes(_shipSent, reverted).Count == 0, "snapshot reloads unchanged: " + string.Join(" ", ShipSync.Changes(_shipSent, reverted).Keys));
                string reason = HostApplyChange(1, change);
                Check(reason == null, "change applied" + (reason == null ? "" : ": " + reason));
                Dictionary<string, string> applied = ShipSync.Snapshot(global, merc);
                Check(change.All(kv => applied[kv.Key] == kv.Value), "host now has the friend's storages");
                Check(_shipVersion == 2 && _shipSent != null, "fresh snapshot sent");

                // Stale: a change based on version 1 after version 2 went out.
                reason = HostApplyChange(1, change);
                Check(reason != null && reason.Contains("older"), "stale change refused: " + reason);

                // Conflict: the friend puts the item back while the host takes another item from that tab.
                Dictionary<string, string> sent2 = _shipSent;
                int version2 = _shipVersion;
                BasePickupItem back = merc.CreatureData.Inventory.AllContainers.Where(c => c != null).SelectMany(c => c.Items).FirstOrDefault(i => i.Id == item.Id);
                back?.Storage?.Remove(back);
                bool returned = back != null && tab.TryPutItem(back, CellPosition.Zero);
                Check(returned, "friend put the item back");
                Dictionary<string, string> change2 = ShipSync.Changes(sent2, ShipSync.Snapshot(global, merc));
                ShipSync.Load(global, merc, sent2);
                BasePickupItem other = tab.Items.FirstOrDefault();
                Check(other != null, "tab has another item for the host to take");
                if (other != null)
                {
                    tab.Remove(other);
                }
                reason = HostApplyChange(version2, change2);
                Check(reason != null && reason.Contains("same storage"), "conflicting change refused: " + reason);

                // Equipment: the friend puts a vest or backpack on from the cargo (it changes the
                // grids) and takes it off again, in whichever order the merc's gear allows.
                ShipSync.Load(global, merc, original);
                Inventory inv = merc.CreatureData.Inventory;
                ItemStorage slot = null;
                bool wearing = false;
                foreach (ItemStorage candidate in new[] { inv.VestSlot, inv.BackpackSlot })
                {
                    ItemStorage grid = candidate == inv.VestSlot ? inv.VestStore : inv.BackpackStore;
                    if (!candidate.Empty && grid.Empty)
                    {
                        slot = candidate;
                        wearing = true;
                        _equipTestItem = candidate.Items.First().Id;
                        break;
                    }
                    bool vest = candidate == inv.VestSlot;
                    BasePickupItem spare = candidate.Empty ? cargo.ShipCargo.SelectMany(t => t.Items).FirstOrDefault(i => vest ? i.Is<VestRecord>() : i.Is<BackpackRecord>()) : null;
                    if (spare != null)
                    {
                        slot = candidate;
                        _equipTestItem = spare.Id;
                        break;
                    }
                }
                Check(slot != null, "a vest or backpack to put on or take off");
                for (int step = 0; slot != null && step < 2; step++)
                {
                    bool takeOff = wearing;
                    _shipSent = ShipSync.Snapshot(global, merc);
                    int version = ++_shipVersion;
                    Dictionary<string, string> sent = _shipSent;
                    BasePickupItem gear = takeOff ? slot.Items.FirstOrDefault() : cargo.ShipCargo.SelectMany(t => t.Items).LastOrDefault(i => i.Id == _equipTestItem);
                    if (gear == null)
                    {
                        Check(false, (takeOff ? "gear to take off" : "gear to put on") + " found");
                        break;
                    }
                    gear.Storage.Remove(gear);
                    bool moved = takeOff ? cargo.ShipCargo.Any(t => t.TryPutItem(gear, CellPosition.Zero)) : slot.TryPutItem(gear, CellPosition.Zero);
                    Check(moved, (takeOff ? "took off " : "put on ") + gear.Id);
                    Dictionary<string, string> edited = ShipSync.Snapshot(global, merc);
                    Dictionary<string, string> equipChange = ShipSync.Changes(sent, edited);
                    ShipSync.Load(global, merc, sent);
                    reason = HostApplyChange(version, equipChange);
                    Dictionary<string, string> result = ShipSync.Snapshot(global, merc);
                    Check(reason == null && result.Count == edited.Count && edited.All(kv => result.TryGetValue(kv.Key, out string v) && v == kv.Value),
                        $"host matches after {(takeOff ? "unequip" : "equip")} ({string.Join(" ", equipChange.Keys)}){(reason == null ? "" : ": " + reason)}");
                    wearing = !wearing;
                }
            }
            catch (Exception e)
            {
                Check(false, "exception " + e);
            }
            finally
            {
                try
                {
                    ShipSync.Load(global, merc, original);
                }
                catch (Exception e)
                {
                    report.Add("cleanup failed: " + e.Message);
                }
                _shipTesting = false;
                _onShip = onShip;
                _friendMercId = friend;
                _shipSent = null;
            }
            return (ok ? "PASS " : "FAIL ") + string.Join("; ", report);
        }

        // ---------------------------------------------------------------- transport

        // Compressed and split like the save transfer; one message per frame while Steam's buffer is full.
        private static void SendShip(ShipMsg kind, Action<BinaryWriter> body)
        {
            if (_shipTesting || _loopback != Loopback.None || SteamNet.Instance == null)
            {
                return;
            }
            byte[] data;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write((byte)kind);
                body(w);
                w.Flush();
                data = ShipSync.Compress(ms.ToArray());
            }
            int id = ++_shipBlobId;
            int total = Math.Max(1, (data.Length + SteamNet.MaxChunkBytes - 1) / SteamNet.MaxChunkBytes);
            List<byte[]> chunks = new List<byte[]>(total);
            for (int i = 0; i < total; i++)
            {
                int index = i, offset = i * SteamNet.MaxChunkBytes;
                int length = Math.Min(SteamNet.MaxChunkBytes, data.Length - offset);
                chunks.Add(Wire.Build(MsgType.ShipChunk, w =>
                {
                    w.Write(id);
                    w.Write(index);
                    w.Write(total);
                    w.Write(length);
                    w.Write(data, offset, length);
                }));
            }
            SteamNet.Instance.StartCoroutine(SendChunks(chunks));
        }

        private static IEnumerator SendChunks(List<byte[]> chunks)
        {
            foreach (byte[] chunk in chunks)
            {
                int attempts = 0;
                while (!SteamNet.Instance.Send(chunk))
                {
                    if (_phase == Phase.Idle || ++attempts > 600)
                    {
                        Log.Warn("Gave up sending ship data.");
                        yield break;
                    }
                    yield return new WaitForSecondsRealtime(0.05f);
                }
            }
        }

        private static void OnShipChunk(BinaryReader r)
        {
            int id = r.ReadInt32(), index = r.ReadInt32(), total = r.ReadInt32(), length = r.ReadInt32();
            if (!_shipIncoming.TryGetValue(id, out byte[][] parts))
            {
                parts = new byte[total][];
                _shipIncoming[id] = parts;
            }
            parts[index] = r.ReadBytes(length);
            if (parts.Any(p => p == null))
            {
                return;
            }
            _shipIncoming.Remove(id);
            try
            {
                byte[] data = ShipSync.Decompress(parts.SelectMany(p => p).ToArray());
                using (BinaryReader body = new BinaryReader(new MemoryStream(data)))
                {
                    OnShipMessage((ShipMsg)body.ReadByte(), body);
                }
            }
            catch (Exception e)
            {
                Log.Error("Bad ship message: " + e);
            }
        }
    }
}
