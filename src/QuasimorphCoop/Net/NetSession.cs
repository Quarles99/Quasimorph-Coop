using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HarmonyLib;
using MGSC;
using QuasimorphCoop.Lockstep;
using UnityEngine;

namespace QuasimorphCoop.Net
{
    // Networked hotseat over Steam. Both machines run the same simulation in lockstep:
    //
    // 1. Host picks a partner merc and hosts from the pause menu; the friend joins from their main
    //    menu (Steam invite or the friends list in the Co-op menu). Their game sends Hello (versions).
    // 2. The connection then lasts across floors and missions. Every time the host starts a level
    //    with the partner on the map, it saves, sends its slot files, and both machines load that
    //    same save (the friend into hidden slot 9). Party merc 0 belongs to the host, merc 1 to the friend.
    // 3. Once both are in, the host sends a seed for the gameplay RNG and lockstep starts. It stops
    //    when the level ends; the next level start syncs again (a new floor is generated from a
    //    time-based seed, so the two machines can't generate it independently).
    // 4. Whoever owns the active merc plays normally; their inputs (queued commands plus the few
    //    direct actions) are sent and replayed on the other machine at the same action point.
    //    The other machine blocks local input meanwhile. After every action point both sides
    //    exchange a state hash to catch desyncs.
    internal static partial class NetSession
    {
        public const string ProtocolVersion = "4";

        private enum Role { None, Host, Client }

        // Order matters: everything from AwaitingWorld up blocks unsent local actions (Engaged).
        private enum Phase { Idle, Lobby, Handshake, Connected, AwaitingWorld, Loading, WaitingGo, Running }

        private sealed class Batch
        {
            public int ApSeq;
            public byte[] Payload;
        }

        private sealed class Recorded
        {
            public NetEntry Entry;
            public byte[] Bytes;
        }

        private static Role _role;
        private static Phase _phase;

        private static int _apSeq;
        private static readonly List<Recorded> _outbox = new List<Recorded>();
        private static int _outboxApSeq;
        private static readonly Queue<Batch> _inbox = new Queue<Batch>();
        private static TurnController _subscribed;
        private static int _actionDepth;
        private static readonly Dictionary<int, string> _localHashes = new Dictionary<int, string>();
        private static readonly Dictionary<int, KeyValuePair<string, string>> _remoteHashes = new Dictionary<int, KeyValuePair<string, string>>();
        private static readonly Dictionary<int, string> _localDescriptions = new Dictionary<int, string>();
        private static bool _desyncReported;
        private static bool _resyncPending;
        private static int _resyncs;
        private static float _nextItemCheck;

        private static readonly Dictionary<string, byte[][]> _incoming = new Dictionary<string, byte[][]>();
        private static Dictionary<string, byte[]> _pendingWorld;
        private static int _seed;
        private static bool _hostLoaded;
        private static bool _clientReady;

        private static string _modHash;

        // Offline test of the whole capture -> encode -> decode -> replay path (see LockstepTest).
        // Record: every merc counts as local and sent batches go to a file instead of Steam.
        // Play: no merc is local and the inbox is preloaded from that file.
        internal enum Loopback { None, Record, Play }

        private static Loopback _loopback;
        private static string _loopbackFile;

        public static bool Replaying { get; private set; }

        // A level is being synced or played together: local actions must not reach the simulation unsent.
        public static bool Engaged => _phase >= Phase.AwaitingWorld;

        public static bool Running => _phase == Phase.Running;

        public static bool Active => _phase != Phase.Idle;

        public static bool IsHost => _role == Role.Host;

        public static string Status => $"role={_role} phase={_phase} peer={SteamNet.Instance?.PeerName ?? "none"} ap={_apSeq} inbox={_inbox.Count}";

        // One line for the Co-op menu.
        public static string Summary
        {
            get
            {
                string peer = SteamNet.Instance?.PeerName ?? "friend";
                switch (_phase)
                {
                    case Phase.Idle: return "Not connected";
                    case Phase.Lobby: return "Hosting - invite a friend";
                    case Phase.Handshake: return $"Connecting to {peer}...";
                    case Phase.Connected: return _role == Role.Host ? $"{peer} connected - start a level" : $"With {peer} - waiting for host";
                    case Phase.AwaitingWorld:
                    case Phase.Loading:
                    case Phase.WaitingGo: return $"Syncing with {peer}...";
                    default: return $"Playing with {peer}";
                }
            }
        }

        // The friend plays this merc (the host's chosen partner); the host plays the rest of the party.
        // Decided by the host for every level sync, so it doesn't depend on party order.
        private static string _friendMercId;

        private static string ModHash
        {
            get
            {
                if (_modHash == null)
                {
                    using (SHA1 sha = SHA1.Create())
                    {
                        _modHash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(typeof(NetSession).Assembly.Location))).Replace("-", "").Substring(0, 12);
                    }
                }
                return _modHash;
            }
        }

        private static string ModList(State state) => string.Join(",", state.Get<UserMods>().Values.Select(m => m.UniqueModName).OrderBy(n => n));

        public static void Notify(string text)
        {
            Log.Info("Notify: " + text);
            try
            {
                UI.Staff.NotificationPanel.AddNotification(text);
            }
            catch (Exception)
            {
                // No UI yet (e.g. while loading); the log line is enough.
            }
        }

        internal static State GetGlobalState()
        {
            GameModeStateMachine machine = UnityEngine.Object.FindObjectOfType<GameModeStateMachine>();
            return machine == null ? null : Traverse.Create(machine).Field("_state").GetValue<State>();
        }

        // ---------------------------------------------------------------- setup

        // From the pause menu, on the ship or in a mission.
        public static string StartHosting()
        {
            if (_phase != Phase.Idle)
            {
                return "Already in a co-op session.";
            }
            if (!SteamNet.SteamReady)
            {
                return "Steam isn't available.";
            }
            if (GetGlobalState()?.Get<SavedGameMetadata>() == null)
            {
                return "Load a game first.";
            }
            _role = Role.Host;
            _phase = Phase.Lobby;
            SteamNet.Ensure();
            SteamNet.Instance.CreateLobby();
            return "Creating a friends-only lobby...";
        }

        public static void InviteFriend()
        {
            SteamNet.Instance?.OpenInviteDialog();
        }

        public static void OnJoinedHost()
        {
            if (UnityEngine.Object.FindObjectOfType<MainMenuGameMode>() == null)
            {
                Notify("Co-op: go back to the main menu, then join again.");
                SteamNet.Instance.Leave();
                return;
            }
            _role = Role.Client;
            _phase = Phase.Handshake;
            _incoming.Clear();
            _pendingWorld = null;
            State state = GetGlobalState();
            SteamNet.Instance.Send(Wire.Build(MsgType.Hello, w =>
            {
                w.Write(ProtocolVersion);
                w.Write(Application.version);
                w.Write(ModHash);
                w.Write(state != null ? ModList(state) : "");
            }));
            Notify($"Co-op: connecting to {SteamNet.Instance.PeerName}...");
        }

        public static void Leave(string reason)
        {
            if (_phase == Phase.Idle)
            {
                return;
            }
            SteamNet.Instance?.Send(Wire.Build(MsgType.Bye, w => w.Write(reason)));
            Stop("you left: " + reason);
        }

        public static void OnPeerLost(string reason)
        {
            if (_phase != Phase.Idle)
            {
                Stop($"{SteamNet.Instance?.PeerName} {reason}");
            }
        }

        private static void Stop(string reason)
        {
            Log.Warn($"Session ended ({reason}) at {Status}.");
            Notify("Co-op ended: " + reason);
            StopLockstep();
            _loopback = Loopback.None;
            _phase = Phase.Idle;
            _role = Role.None;
            _incoming.Clear();
            _pendingWorld = null;
            _hostLoaded = _clientReady = false;
            _resyncs = 0;
            _shipIncoming.Clear();
            _shipSent = _shipBaseline = null;
            _friendEditing = _editingOwnMerc = false;
            _pendingShip = null;
            SteamNet.Instance?.Leave();
        }

        private static void StopLockstep()
        {
            if (_subscribed != null)
            {
                _subscribed.OnActionPointsProcessed -= OnActionPointProcessed;
                _subscribed = null;
            }
            GameplayRng.Enabled = false;
            _inbox.Clear();
            _outbox.Clear();
            ItemSync.Clear();
            _resyncPending = false;
        }

        // ---------------------------------------------------------------- messages

        public static void OnMessage(byte[] data)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(data)))
            {
                MsgType type = (MsgType)r.ReadByte();
                switch (type)
                {
                    case MsgType.Hello: OnHello(r); break;
                    case MsgType.Welcome: OnWelcome(); break;
                    case MsgType.Reject: Stop("host rejected: " + r.ReadString()); break;
                    case MsgType.FileChunk: OnFileChunk(r); break;
                    case MsgType.FilesDone: OnFilesDone(r); break;
                    case MsgType.Ready: _clientReady = true; Log.Info("Client is ready."); TryGo(); break;
                    case MsgType.Go: StartLockstep(r.ReadInt32()); break;
                    case MsgType.Batch: _inbox.Enqueue(new Batch { ApSeq = r.ReadInt32(), Payload = r.ReadBytes(data.Length - 5) }); break;
                    case MsgType.Hash: OnRemoteHash(r.ReadInt32(), r.ReadString(), r.ReadString()); break;
                    case MsgType.Bye: Stop($"{SteamNet.Instance.PeerName} left: {r.ReadString()}"); break;
                    case MsgType.Ping: break;
                    case MsgType.ShipChunk: OnShipChunk(r); break;
                }
            }
        }

        private static void OnHello(BinaryReader r)
        {
            if (_role != Role.Host || _phase != Phase.Lobby)
            {
                return;
            }
            string protocol = r.ReadString(), gameVersion = r.ReadString(), modHash = r.ReadString(), mods = r.ReadString();
            string myMods = ModList(GetGlobalState());
            string reason = protocol != ProtocolVersion ? $"protocol {protocol} vs {ProtocolVersion}"
                : gameVersion != Application.version ? $"game version {gameVersion} vs {Application.version}"
                : modHash != ModHash ? $"co-op mod build {modHash} vs {ModHash}"
                : null;
            if (reason != null)
            {
                Log.Warn("Rejecting client: " + reason);
                SteamNet.Instance.Send(Wire.Build(MsgType.Reject, w => w.Write("mismatch: " + reason)));
                Notify($"Co-op: {SteamNet.Instance.PeerName} can't join ({reason}).");
                return;
            }
            if (mods != myMods)
            {
                Log.Warn($"Mod lists differ (may desync). Host: {myMods} Client: {mods}");
                Notify("Co-op warning: your mod lists differ; this can cause desyncs.");
            }
            SteamNet.Instance.Send(Wire.Build(MsgType.Welcome));
            _phase = Phase.Connected;
            Notify($"Co-op: {SteamNet.Instance.PeerName} joined.");
            HostSyncIfPossible();
        }

        private static void OnWelcome()
        {
            if (_role == Role.Client && _phase == Phase.Handshake)
            {
                _phase = Phase.Connected;
                Notify($"Co-op: connected to {SteamNet.Instance.PeerName}. Waiting for them to start a level.");
            }
        }

        // Host: start playing together on the current level, if there is one and the partner is on it.
        public static void HostSyncIfPossible()
        {
            if (_role != Role.Host || _phase != Phase.Connected)
            {
                return;
            }
            if (!CoopSession.InDungeon)
            {
                if (_onShip)
                {
                    SendShipSave();
                }
                Notify($"Co-op: {SteamNet.Instance.PeerName} will join you when you start a mission.");
                return;
            }
            if (CoopSession.Party.Count != 2)
            {
                Notify("Co-op: pick a partner merc in the Co-op menu so your friend has someone to play.");
                return;
            }
            Notify($"Co-op: syncing this level with {SteamNet.Instance.PeerName}...");
            SendWorldAndReload();
        }

        private static void SendWorldAndReload()
        {
            State state = CoopSession.State;
            state.Get<SaveManager>().SaveGame();
            int slot = state.Get<SavedGameMetadata>().Slot;
            string partner = Partner.Get(state);
            _friendMercId = CoopSession.Party.Any(p => p.Mercenary?.ProfileId == partner) ? partner : CoopSession.Party.Last().Mercenary?.ProfileId;
            _phase = Phase.Loading;
            _hostLoaded = _clientReady = false;
            SteamNet.Instance.StartCoroutine(SendWorldThenReload(slot, SlotFiles.Read(slot), reloadHost: true));
        }

        // Joined between missions: the friend loads the host's ship (a copy, like levels) so they can
        // equip their merc there. Nothing to keep in lockstep, so the host doesn't reload.
        private static void SendShipSave()
        {
            State global = GetGlobalState();
            string partner = Partner.Get(global);
            if (global == null || string.IsNullOrEmpty(partner))
            {
                return;
            }
            global.Get<SaveManager>().SaveGame();
            _friendMercId = partner;
            int slot = global.Get<SavedGameMetadata>().Slot;
            SteamNet.Instance.StartCoroutine(SendWorldThenReload(slot, SlotFiles.Read(slot), reloadHost: false));
        }

        // Steam's per-connection send buffer is smaller than a save (~1.3 MB), so send one chunk per
        // frame and back off while the buffer is full. Abort (without reloading) if it never drains.
        private static IEnumerator SendWorldThenReload(int slot, Dictionary<string, byte[]> files, bool reloadHost)
        {
            List<byte[]> chunks = new List<byte[]>();
            foreach (KeyValuePair<string, byte[]> file in files)
            {
                int total = Math.Max(1, (file.Value.Length + SteamNet.MaxChunkBytes - 1) / SteamNet.MaxChunkBytes);
                for (int i = 0; i < total; i++)
                {
                    int index = i, offset = i * SteamNet.MaxChunkBytes;
                    int length = Math.Min(SteamNet.MaxChunkBytes, file.Value.Length - offset);
                    chunks.Add(Wire.Build(MsgType.FileChunk, w =>
                    {
                        w.Write(file.Key);
                        w.Write(index);
                        w.Write(total);
                        w.Write(length);
                        w.Write(file.Value, offset, length);
                    }));
                }
            }
            _seed = Environment.TickCount;
            chunks.Add(Wire.Build(MsgType.FilesDone, w =>
            {
                w.Write(files.Count);
                w.Write(_seed);
                w.Write(_friendMercId ?? "");
            }));

            foreach (byte[] chunk in chunks)
            {
                int attempts = 0;
                while (!SteamNet.Instance.Send(chunk))
                {
                    if (reloadHost ? _phase != Phase.Loading : _phase < Phase.Connected)
                    {
                        yield break;
                    }
                    if (++attempts > 600)
                    {
                        Leave("couldn't send the save to your friend");
                        yield break;
                    }
                    yield return new WaitForSecondsRealtime(0.05f);
                }
                yield return null;
            }
            Log.Info($"Sent {files.Count} files ({files.Values.Sum(f => f.Length)} bytes, {chunks.Count} messages) from slot {slot}{(reloadHost ? "; reloading it" : "")}.");
            if (reloadHost)
            {
                SteamNet.Instance.StartCoroutine(LoadSlot(slot));
            }
        }

        // Loads a save slot from whatever mode the game is in (menu, ship or mission), like the
        // game's own LoadLastSave but for the regular save.
        internal static IEnumerator LoadSlot(int slot)
        {
            _onShip = false;
            GameModeStateMachine machine = UnityEngine.Object.FindObjectOfType<GameModeStateMachine>();
            if (UnityEngine.Object.FindObjectOfType<MainMenuGameMode>() != null)
            {
                AccessTools.Method(typeof(GameModeStateMachine), "MainMenuFinished").Invoke(machine, new object[] { false, slot, null });
                yield break;
            }
            AccessTools.Method(typeof(GameModeStateMachine), "KillDungeon").Invoke(machine, Array.Empty<object>());
            AccessTools.Method(typeof(GameModeStateMachine), "KillSpaceMode").Invoke(machine, Array.Empty<object>());
            yield return new WaitForEndOfFrame();
            IEnumerator start = (IEnumerator)AccessTools.Method(typeof(GameModeStateMachine), "ProcessStartGame")
                .Invoke(machine, new object[] { false, slot, null, false });
            machine.StartCoroutine(start);
        }

        internal static IEnumerator ReloadSlot(int slot) => LoadSlot(slot);

        private static void OnFileChunk(BinaryReader r)
        {
            string name = r.ReadString();
            int index = r.ReadInt32(), total = r.ReadInt32(), length = r.ReadInt32();
            if (!_incoming.TryGetValue(name, out byte[][] parts))
            {
                parts = new byte[total][];
                _incoming[name] = parts;
            }
            parts[index] = r.ReadBytes(length);
        }

        private static void OnFilesDone(BinaryReader r)
        {
            int count = r.ReadInt32();
            _seed = r.ReadInt32();
            _friendMercId = r.ReadString();
            if (_incoming.Count != count || _incoming.Values.Any(p => p.Any(c => c == null)))
            {
                Leave($"incomplete transfer ({_incoming.Count}/{count} files)");
                return;
            }
            _pendingWorld = _incoming.ToDictionary(kv => kv.Key, kv => kv.Value.SelectMany(c => c).ToArray());
            _incoming.Clear();
            Log.Info($"Received {_pendingWorld.Count} files ({_pendingWorld.Values.Sum(f => f.Length)} bytes).");
            TryLoadPendingWorld();
        }

        // The world can arrive while this game is itself mid-transition (e.g. generating its own copy
        // of the next floor); only load once it has settled into a menu, the ship or a level.
        private static void TryLoadPendingWorld()
        {
            if (_pendingWorld == null || _role != Role.Client)
            {
                return;
            }
            bool settled = UnityEngine.Object.FindObjectOfType<MainMenuGameMode>() != null
                || (SingletonMonoBehaviour<DungeonGameMode>.Instance != null && SingletonMonoBehaviour<DungeonGameMode>.Instance.Initialized)
                || _onShip;
            if (!settled)
            {
                Log.Info("World received while loading; will load it once settled.");
                return;
            }
            StopLockstep();
            SlotFiles.Write(SlotFiles.ClientSlot, _pendingWorld);
            _pendingWorld = null;
            Log.Info($"Loading host's world from slot {SlotFiles.ClientSlot}.");
            _phase = Phase.Loading;
            SteamNet.Instance.StartCoroutine(LoadSlot(SlotFiles.ClientSlot));
        }

        private static bool _onShip;

        public static void OnSpaceStarted()
        {
            _onShip = true;
            if (_role == Role.Client && _phase == Phase.Loading)
            {
                // The host sent its ship (joined between missions), not a level.
                _phase = Phase.Connected;
                Log.Info("Loaded the host's ship.");
            }
            TryLoadPendingWorld();
            OnShipArrived();
        }

        public static void OnSpaceFinished()
        {
            _onShip = false;
            OnShipLeft();
        }

        public static void OnMainMenuStarted()
        {
            if (_phase != Phase.Idle && _phase != Phase.Handshake)
            {
                Leave("returned to the main menu");
            }
        }

        public static void OnDungeonStarted()
        {
            switch (_phase)
            {
                case Phase.Loading:
                    if (!CoopSession.Party.Any(p => p.Mercenary?.ProfileId == _friendMercId) && CoopSession.Party.Count != 1)
                    {
                        Leave($"your friend's merc '{_friendMercId}' isn't in the loaded party");
                        return;
                    }
                    if (_role == Role.Host)
                    {
                        _hostLoaded = true;
                        Log.Info("Host reloaded.");
                        TryGo();
                    }
                    else
                    {
                        _phase = Phase.WaitingGo;
                        SteamNet.Instance.Send(Wire.Build(MsgType.Ready));
                        Log.Info("Loaded; waiting for host.");
                    }
                    break;
                case Phase.Connected:
                    if (_role == Role.Host)
                    {
                        HostSyncIfPossible();
                    }
                    else
                    {
                        // Our own copy of a new level; the host's version is on its way.
                        _phase = Phase.AwaitingWorld;
                        TryLoadPendingWorld();
                    }
                    break;
            }
        }

        public static void OnDungeonFinished()
        {
            // The host's own reload also tears the level down; that's part of Loading.
            if (_phase == Phase.Running || _phase == Phase.WaitingGo || _phase == Phase.AwaitingWorld)
            {
                StopLockstep();
                _phase = Phase.Connected;
                Log.Info("Level ended; lockstep paused until the next level syncs.");
                if (_role == Role.Client && _loopback == Loopback.None)
                {
                    Notify($"Co-op: level over. Stay connected; the next level loads by itself when {SteamNet.Instance?.PeerName ?? "the host"} starts it. Changes you make meanwhile aren't kept.");
                }
            }
        }

        private static void TryGo()
        {
            if (_role == Role.Host && _phase == Phase.Loading && _hostLoaded && _clientReady)
            {
                SteamNet.Instance.Send(Wire.Build(MsgType.Go, w => w.Write(_seed)));
                StartLockstep(_seed);
            }
        }

        private static void StartLockstep(int seed)
        {
            GameplayRng.Enabled = true;
            GameplayRng.Reset(seed);
            _apSeq = 0;
            _inbox.Clear();
            _outbox.Clear();
            _localHashes.Clear();
            _remoteHashes.Clear();
            _localDescriptions.Clear();
            _desyncReported = false;
            _resyncPending = false;
            ItemSync.Clear();
            _subscribed = CoopSession.State.Get<TurnController>();
            _subscribed.OnActionPointsProcessed += OnActionPointProcessed;
            _phase = Phase.Running;
            OnActionPointStarted(CoopSession.Creatures.Player);
            Player mine = CoopSession.Party.FirstOrDefault(IsLocal);
            Log.Info($"Lockstep running as {_role} (loopback {_loopback}), seed {seed}; local merc '{mine?.Mercenary?.ProfileId}', friend's merc '{_friendMercId}'.");
            if (_loopback == Loopback.None)
            {
                Notify(mine != null
                    ? $"Co-op: playing with {SteamNet.Instance.PeerName}. You control {CoopSession.DisplayName(mine.Mercenary)}."
                    : $"Co-op: playing with {SteamNet.Instance.PeerName}. Your merc is down; you're watching.");
            }
        }

        // ---------------------------------------------------------------- lockstep

        private static bool IsLocal(Player player)
        {
            switch (_loopback)
            {
                case Loopback.Record: return CoopSession.IsMember(player);
                case Loopback.Play: return false;
                default:
                    if (player == null || !CoopSession.IsMember(player))
                    {
                        return false;
                    }
                    bool friends = player.Mercenary?.ProfileId == _friendMercId;
                    return _role == Role.Client ? friends : !friends;
            }
        }

        public static void StartLoopback(Loopback mode, string file, int seed)
        {
            _loopback = mode;
            _loopbackFile = file;
            _role = Role.Host;
            if (mode == Loopback.Record)
            {
                File.WriteAllBytes(file, Array.Empty<byte>());
            }
            StartLockstep(seed);
            if (mode == Loopback.Play)
            {
                using (BinaryReader r = new BinaryReader(File.OpenRead(file)))
                {
                    while (r.BaseStream.Position < r.BaseStream.Length)
                    {
                        byte[] data = r.ReadBytes(r.ReadInt32());
                        OnMessage(data);
                    }
                }
                Log.Info($"Loopback play: {_inbox.Count} batches queued from {file}.");
            }
        }

        private static void Send(byte[] data)
        {
            if (_loopback == Loopback.None)
            {
                SteamNet.Instance.Send(data);
            }
            else if (_loopback == Loopback.Record && (MsgType)data[0] == MsgType.Batch)
            {
                using (BinaryWriter w = new BinaryWriter(File.Open(_loopbackFile, FileMode.Append)))
                {
                    w.Write(data.Length);
                    w.Write(data);
                }
            }
        }

        // The local player's merc is active and the game is waiting for its input.
        private static bool LocalWaiting()
        {
            State state = CoopSession.State;
            return state != null && IsLocal(state.Get<Creatures>().Player) && CanProcess(state) && state.Get<PlayerCommandQueue>().Empty;
        }

        // TurnSystem.CanProcessPlayerTurn without ReadOnlyScreensPatch, and only while the merc's
        // action point is actually running: between the end of one and the start of the next the
        // game still accepts input (it runs on the next one), which would be sent with the wrong
        // action point number.
        private static bool CanProcess(State state)
        {
            TurnMetadata turn = state.Get<TurnMetadata>();
            Creatures creatures = state.Get<Creatures>();
            return !turn.ForceLocked && !turn.PauseTimer.Started && state.Get<TurnController>().CurrentProcessable == creatures.Player
                && creatures.Player.IsProcessing && !creatures.Player.IsAnimationInProgress && creatures.Player.CreatureData.Health.Alive;
        }

        public static bool LocalInputAllowed => !Engaged || (Running && IsLocal(CoopSession.Creatures?.Player));

        // The merc on screen is the other player's, so windows its replayed actions open here
        // (station and shuttle menus) are theirs to use, not ours.
        public static bool WatchingOther => Running && !IsLocal(CoopSession.Creatures?.Player);

        // Aborting ends the mission for both players, so it's the host's call while the host still
        // has a living merc. Returns why it's blocked, or null.
        public static string AbortBlockedReason()
        {
            if (!Engaged || _loopback != Loopback.None || _role != Role.Client)
            {
                return null;
            }
            bool hostMercAlive = CoopSession.Party.Any(p => p.CreatureData.Health.Alive && p.Mercenary?.ProfileId != _friendMercId);
            return hostMercAlive ? "only the host can abort the mission. Use Leave co-op to stop playing." : null;
        }

        // The other player's merc is on screen and a screen other than the HUD (inventory, health,
        // corpse) is open: let it be looked at but not changed.
        public static bool ScreensReadOnly
        {
            get
            {
                if (!Engaged || Replaying || GameplayRng.InSimulation || _actionDepth > 0 || LocalInputAllowed)
                {
                    return false;
                }
                try
                {
                    return UI.IsAnyShowing(typeof(DungeonHudScreen));
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // Called for every PlayerCommandQueue.Add/AddFirst. Returns false to drop the command.
        public static bool OnCommandAdded(ICommand cmd)
        {
            if (!Engaged || Replaying || GameplayRng.InSimulation)
            {
                return true;
            }
            if (!Running || !LocalWaiting())
            {
                Log.Info($"Dropped {cmd.Type}: not your merc's turn.");
                return false;
            }
            Record(new NetEntry { Command = cmd });
            return true;
        }

        // Called before a direct (non-queued) action. Returns false to block it. When it returns
        // true with recorded=true the caller must call EndAction afterwards.
        public static bool BeginAction(DirectAction action, int arg, out bool recorded)
        {
            recorded = false;
            if (!Engaged || Replaying || GameplayRng.InSimulation || _actionDepth > 0)
            {
                return true;
            }
            if (!Running || !LocalWaiting())
            {
                Log.Info($"Blocked {action}: not your merc's turn.");
                return false;
            }
            Record(new NetEntry { Action = action, Arg = arg });
            _actionDepth++;
            recorded = true;
            return true;
        }

        public static void EndAction()
        {
            _actionDepth--;
        }

        // Called before an inventory op (see InventoryOps). Same rules as BeginAction; a recorded op
        // runs inside the gameplay RNG scope and the caller must call EndOp afterwards.
        public static bool BeginOp(InventoryOp op, Action<BinaryWriter, State> args, out bool recorded)
        {
            recorded = false;
            if (!Engaged || Replaying || GameplayRng.InSimulation || _actionDepth > 0)
            {
                return true;
            }
            if (!Running || !LocalWaiting())
            {
                Log.Info($"Blocked {op}: not your merc's turn.");
                return false;
            }
            byte[] data;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                FlushItems();
                args(w, CoopSession.State);
                w.Flush();
                data = ms.ToArray();
            }
            Record(new NetEntry { Op = op, Data = data });
            _actionDepth++;
            GameplayRng.Enter();
            recorded = true;
            return true;
        }

        public static void EndOp()
        {
            GameplayRng.Exit();
            _actionDepth--;
            // The replay reproduces the op's own storage changes; only what happens next is news.
            Player slot = CoopSession.Creatures?.Player;
            if (Running && slot != null && IsLocal(slot))
            {
                ItemSync.ResetBaseline(CoopSession.State, slot);
            }
        }

        // Player.IsProcessing is about to become false outside the simulation: the game ending an
        // action point after inventory use or a corpse interaction. Returns false to block it.
        public static int InDungeonUpdate;

        public static bool OnPlayerTurnEnding(Player player)
        {
            if (!Running || InDungeonUpdate == 0 || Replaying || GameplayRng.InSimulation || _actionDepth > 0 || !player.IsProcessing || !player.AnyActionProcessedFlag)
            {
                return true;
            }
            if (IsLocal(player))
            {
                Record(new NetEntry { Op = InventoryOp.TurnEnd, Data = Array.Empty<byte>() });
                CanonicalizeItems();
                return true;
            }
            // The other player's merc: their machine says when its turn ends.
            return !CoopSession.IsMember(player);
        }

        private static void Record(NetEntry entry)
        {
            if (entry.Op != InventoryOp.Items)
            {
                // Storage changes made before this input must reach the other machine before it.
                FlushItems();
            }
            byte[] bytes;
            try
            {
                bytes = CommandCodec.Encode(entry);
            }
            catch (Exception e)
            {
                Log.Error("Failed to encode an action (desync likely): " + e);
                Notify("Co-op: an action couldn't be sent; the games may now differ.");
                return;
            }
            if (_outbox.Count == 0)
            {
                _outboxApSeq = _apSeq;
            }
            _outbox.Add(new Recorded { Entry = entry, Bytes = bytes });
            // World commands come with the screens closed; one carrying an item keeps a reference to it.
            if (entry.Command != null && !(entry.Command is UsePlaceableCommand))
            {
                CanonicalizeItems();
            }
        }

        private static void CanonicalizeItems()
        {
            try
            {
                ItemSync.Canonicalize(CoopSession.State);
            }
            catch (Exception e)
            {
                Log.Error("Failed to reload sent item storages (desync likely): " + e);
            }
        }

        // Sends storages the local player's merc changed through inventory screens since last time.
        private static void FlushItems()
        {
            State state = CoopSession.State;
            Player slot = state?.Get<Creatures>().Player;
            if (!Running || slot == null || !IsLocal(slot))
            {
                return;
            }
            byte[] changes;
            try
            {
                changes = ItemSync.TakeChanges(state, slot);
            }
            catch (Exception e)
            {
                Log.Error("Failed to collect item changes (desync likely): " + e);
                return;
            }
            if (changes != null)
            {
                Record(new NetEntry { Op = InventoryOp.Items, Data = changes });
            }
        }

        // A party merc's action point begins: remember its reachable storages as they are now, so
        // anything the player changes through screens during it shows up as a change.
        public static void OnActionPointStarted(Player player)
        {
            if (!Running || player != CoopSession.Creatures?.Player || !IsLocal(player))
            {
                return;
            }
            CanonicalizeItems();
            try
            {
                ItemSync.ResetBaseline(CoopSession.State, player);
            }
            catch (Exception e)
            {
                Log.Error("Failed to read item storages: " + e);
            }
        }

        // Per frame: look for storage changes now and then while the local merc waits for input and
        // the player might be using a screen. (Every recorded input also checks first.)
        private static void TrackItems()
        {
            if (Running && Time.unscaledTime >= _nextItemCheck && LocalWaiting() && (Input.anyKey || UI.IsAnyShowing(typeof(DungeonHudScreen))))
            {
                _nextItemCheck = Time.unscaledTime + 0.2f;
                FlushItems();
            }
        }

        public static void Flush()
        {
            TrackItems();
            if (_outbox.Count == 0 || !Running)
            {
                _outbox.Clear();
                return;
            }
            List<Recorded> entries = new List<Recorded>(_outbox);
            _outbox.Clear();
            try
            {
                Send(Wire.Build(MsgType.Batch, w =>
                {
                    w.Write(_outboxApSeq);
                    w.Write(entries.Count);
                    foreach (Recorded e in entries)
                    {
                        w.Write(e.Bytes.Length);
                        w.Write(e.Bytes);
                    }
                }));
                Log.Info($"Sent batch ap={_outboxApSeq}: {Describe(entries.Select(e => e.Entry).ToList())}");
            }
            catch (Exception e)
            {
                Log.Error("Failed to send batch (desync likely): " + e);
                Notify("Co-op: an action couldn't be sent; the games may now differ.");
            }
        }

        // Replays the other player's inputs once the simulation reaches the point they were made at.
        public static void Update()
        {
            if (!Running || CoopSession.State == null)
            {
                return;
            }
            State state = CoopSession.State;
            Creatures creatures = state.Get<Creatures>();
            bool idle = CanProcess(state) && state.Get<PlayerCommandQueue>().Empty;
            if (_resyncPending && idle && _role == Role.Host)
            {
                _resyncPending = false;
                Resync();
                return;
            }
            // A turn that ended with a command still queued (input in the same frame the inventory
            // closed) ends here the same way; the command then runs on the next action point.
            if (_inbox.Count == 0 || IsLocal(creatures.Player) || _apSeq < _inbox.Peek().ApSeq
                || !(idle || (CanProcess(state) && StartsWithTurnEnd(_inbox.Peek()))))
            {
                return;
            }
            Batch batch = _inbox.Dequeue();
            if (_apSeq != batch.ApSeq)
            {
                Log.Warn($"Batch for ap={batch.ApSeq} applied at ap={_apSeq}.");
            }
            List<byte[]> raw = new List<byte[]>();
            using (BinaryReader r = new BinaryReader(new MemoryStream(batch.Payload)))
            {
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    raw.Add(r.ReadBytes(r.ReadInt32()));
                }
            }
            List<NetEntry> applied = new List<NetEntry>();
            Replaying = true;
            try
            {
                foreach (byte[] bytes in raw)
                {
                    NetEntry e;
                    using (BinaryReader r = new BinaryReader(new MemoryStream(bytes)))
                    {
                        e = CommandCodec.ReadEntry(r, state);
                    }
                    applied.Add(e);
                    if (e.Command != null)
                    {
                        state.Get<PlayerCommandQueue>().Add(e.Command);
                    }
                    else if (e.Op != 0)
                    {
                        InventoryOps.Execute(state, e.Op, e.Data);
                    }
                    else
                    {
                        Execute(state, e);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to apply batch ap={batch.ApSeq} after [{Describe(applied)}]: {ex}");
                ReportDesync(_apSeq, "a received action couldn't be applied");
            }
            finally
            {
                Replaying = false;
            }
            Log.Info($"Applied batch ap={batch.ApSeq}: {Describe(applied)}");
        }

        private static bool StartsWithTurnEnd(Batch batch)
        {
            // count, first entry length, kind 2 (op), op
            return batch.Payload.Length >= 10 && batch.Payload[8] == 2 && batch.Payload[9] == (byte)InventoryOp.TurnEnd;
        }

        private static void Execute(State state, NetEntry e)
        {
            Creatures creatures = state.Get<Creatures>();
            switch (e.Action)
            {
                case DirectAction.SkipTurn: creatures.Player.SkipTurn(); break;
                case DirectAction.MoveStateNext: creatures.Player.ChangeMoveStateToNext(); break;
                case DirectAction.MoveStatePrev: creatures.Player.ChangeMoveStateToPrev(); break;
                case DirectAction.SwapMoveMode: AccessTools.Method(typeof(PlayerInteractionSystem), "SwapMoveMode").Invoke(null, new object[] { creatures }); break;
                case DirectAction.SwapWeapon: PlayerInteractionSystem.SwapWeapon(creatures); break;
                case DirectAction.SwapFiremode: PlayerInteractionSystem.SwapFiremode(creatures); break;
                case DirectAction.Reload: PlayerInteractionSystem.Reload(creatures); break;
                case DirectAction.ActivateUltimate: PlayerInteractionSystem.ActivateUltimatePerk(creatures, state.Get<RaidMetadata>()); break;
                case DirectAction.ChangeDirection: creatures.Player.ChangeDirection((Direction)e.Arg); break;
            }
        }

        private static string Describe(List<NetEntry> entries)
        {
            return string.Join(", ", entries.Select(e => e.Command != null
                ? e.Command.Type + (e.Command is MoveCommand m ? $"({m.target.X},{m.target.Y})" : e.Command is ShootCommand s ? $"({s.target.X},{s.target.Y})" : "")
                : e.Op != 0 ? InventoryOps.Describe(e.Op, e.Data)
                : e.Action + (e.Action == DirectAction.ChangeDirection ? $"({e.Arg})" : "")));
        }

        private static void OnActionPointProcessed(ITurnable turnable)
        {
            if (!Running || CoopSession.State == null)
            {
                return;
            }
            _apSeq++;
            State state = CoopSession.State;
            string description = StateHash.Describe(state.Get<Creatures>(), state.Get<RaidMetadata>().TurnNumber, state.Get<MapObstacles>());
            string hash = StateHash.Hash(description);
            int seq = _apSeq;
            Send(Wire.Build(MsgType.Hash, w =>
            {
                w.Write(seq);
                w.Write(hash);
                w.Write(description);
            }));
            if (_remoteHashes.TryGetValue(seq, out KeyValuePair<string, string> remote))
            {
                _remoteHashes.Remove(seq);
                Compare(seq, hash, description, remote.Key, remote.Value);
            }
            else
            {
                _localHashes[seq] = hash;
                _localDescriptions[seq] = description;
            }
        }

        private static void OnRemoteHash(int seq, string hash, string description)
        {
            if (seq < 0)
            {
                if (Running)
                {
                    Log.Warn($"Friend reports a desync: {description}");
                    ReportDesync(_apSeq, description);
                }
                return;
            }
            if (_localHashes.TryGetValue(seq, out string local))
            {
                string localDescription = _localDescriptions[seq];
                _localHashes.Remove(seq);
                _localDescriptions.Remove(seq);
                Compare(seq, local, localDescription, hash, description);
            }
            else
            {
                _remoteHashes[seq] = new KeyValuePair<string, string>(hash, description);
            }
        }

        private static void Compare(int seq, string localHash, string localDescription, string remoteHash, string remoteDescription)
        {
            if (localHash == remoteHash)
            {
                return;
            }
            Log.Error($"DESYNC at ap={seq}\n  local:  {localDescription}\n  remote: {remoteDescription}");
            ReportDesync(seq, "the games differ");
        }

        // The host fixes a desync by sending its world again (once a merc is waiting for input);
        // the friend waits for it. A few seconds of loading instead of a broken session.
        private static void ReportDesync(int seq, string what)
        {
            if (_desyncReported)
            {
                return;
            }
            _desyncReported = true;
            if (_loopback != Loopback.None)
            {
                Notify($"Co-op: out of sync at action {seq} ({what}).");
                return;
            }
            if (_resyncs >= 5)
            {
                Notify($"Co-op: the games keep going out of sync ({what}). Send both Player.log files to the mod author.");
                return;
            }
            Notify($"Co-op: the games went out of sync at action {seq}; re-syncing...");
            if (_role == Role.Host)
            {
                _resyncPending = true;
            }
            else
            {
                SteamNet.Instance?.Send(Wire.Build(MsgType.Hash, w =>
                {
                    // Make sure the host notices even if its own comparison hasn't run yet.
                    w.Write(-1);
                    w.Write("resync");
                    w.Write(what);
                }));
            }
        }

        private static void Resync()
        {
            _resyncs++;
            Log.Warn($"Re-syncing the level (#{_resyncs}).");
            StopLockstep();
            SendWorldAndReload();
        }
    }
}
