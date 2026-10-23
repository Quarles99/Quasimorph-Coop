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
    internal static class NetSession
    {
        public const string ProtocolVersion = "2";

        private enum Role { None, Host, Client }

        // Order matters: everything from AwaitingWorld up blocks unsent local actions (Engaged).
        private enum Phase { Idle, Lobby, Handshake, Connected, AwaitingWorld, Loading, WaitingGo, Running }

        private sealed class Batch
        {
            public int ApSeq;
            public byte[] Payload;
        }

        private static Role _role;
        private static Phase _phase;

        private static int _apSeq;
        private static readonly List<NetEntry> _outbox = new List<NetEntry>();
        private static int _outboxApSeq;
        private static readonly Queue<Batch> _inbox = new Queue<Batch>();
        private static TurnController _subscribed;
        private static int _actionDepth;
        private static readonly Dictionary<int, string> _localHashes = new Dictionary<int, string>();
        private static readonly Dictionary<int, KeyValuePair<string, string>> _remoteHashes = new Dictionary<int, KeyValuePair<string, string>>();
        private static readonly Dictionary<int, string> _localDescriptions = new Dictionary<int, string>();
        private static bool _desyncReported;

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

        private static int LocalOwner => _role == Role.Host ? 0 : 1;

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
            _phase = Phase.Loading;
            _hostLoaded = _clientReady = false;
            SteamNet.Instance.StartCoroutine(SendWorldThenReload(slot, SlotFiles.Read(slot)));
        }

        // Steam's per-connection send buffer is smaller than a save (~1.3 MB), so send one chunk per
        // frame and back off while the buffer is full. Abort (without reloading) if it never drains.
        private static IEnumerator SendWorldThenReload(int slot, Dictionary<string, byte[]> files)
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
            }));

            foreach (byte[] chunk in chunks)
            {
                int attempts = 0;
                while (!SteamNet.Instance.Send(chunk))
                {
                    if (_phase != Phase.Loading)
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
            Log.Info($"Sent {files.Count} files ({files.Values.Sum(f => f.Length)} bytes, {chunks.Count} messages) from slot {slot}; reloading it.");
            SteamNet.Instance.StartCoroutine(LoadSlot(slot));
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
            TryLoadPendingWorld();
        }

        public static void OnSpaceFinished()
        {
            _onShip = false;
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
                    if (CoopSession.Party.Count != 2)
                    {
                        Leave($"party has {CoopSession.Party.Count} mercs after loading, expected 2");
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
            _subscribed = CoopSession.State.Get<TurnController>();
            _subscribed.OnActionPointsProcessed += OnActionPointProcessed;
            _phase = Phase.Running;
            Player mine = CoopSession.Party[LocalOwner];
            Log.Info($"Lockstep running as {_role} (loopback {_loopback}), seed {seed}; local merc '{mine.Mercenary?.ProfileId}'.");
            if (_loopback == Loopback.None)
            {
                Notify($"Co-op: playing with {SteamNet.Instance.PeerName}. You control {CoopSession.DisplayName(mine.Mercenary)}.");
            }
        }

        // ---------------------------------------------------------------- lockstep

        private static bool IsLocal(Player player)
        {
            switch (_loopback)
            {
                case Loopback.Record: return CoopSession.IsMember(player);
                case Loopback.Play: return false;
                default: return CoopSession.Party.Count > LocalOwner && CoopSession.Party[LocalOwner] == player;
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
            return state != null && IsLocal(state.Get<Creatures>().Player)
                && TurnSystem.CanProcessPlayerTurn(state.Get<TurnController>(), state.Get<TurnMetadata>(), state.Get<Creatures>())
                && state.Get<PlayerCommandQueue>().Empty;
        }

        public static bool LocalInputAllowed => !Engaged || (Running && IsLocal(CoopSession.Creatures?.Player));

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

        private static void Record(NetEntry entry)
        {
            if (_outbox.Count == 0)
            {
                _outboxApSeq = _apSeq;
            }
            _outbox.Add(entry);
        }

        public static void Flush()
        {
            if (_outbox.Count == 0 || !Running)
            {
                _outbox.Clear();
                return;
            }
            List<NetEntry> entries = new List<NetEntry>(_outbox);
            _outbox.Clear();
            try
            {
                Send(Wire.Build(MsgType.Batch, w =>
                {
                    w.Write(_outboxApSeq);
                    w.Write(entries.Count);
                    foreach (NetEntry e in entries)
                    {
                        CommandCodec.WriteEntry(w, e);
                    }
                }));
                Log.Info($"Sent batch ap={_outboxApSeq}: {Describe(entries)}");
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
            if (!Running || _inbox.Count == 0 || CoopSession.State == null)
            {
                return;
            }
            State state = CoopSession.State;
            Creatures creatures = state.Get<Creatures>();
            if (IsLocal(creatures.Player) || _apSeq < _inbox.Peek().ApSeq
                || !TurnSystem.CanProcessPlayerTurn(state.Get<TurnController>(), state.Get<TurnMetadata>(), creatures)
                || !state.Get<PlayerCommandQueue>().Empty)
            {
                return;
            }
            Batch batch = _inbox.Dequeue();
            if (_apSeq != batch.ApSeq)
            {
                Log.Warn($"Batch for ap={batch.ApSeq} applied at ap={_apSeq}.");
            }
            List<NetEntry> entries = new List<NetEntry>();
            using (BinaryReader r = new BinaryReader(new MemoryStream(batch.Payload)))
            {
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    entries.Add(CommandCodec.ReadEntry(r, state));
                }
            }
            Log.Info($"Applying batch ap={batch.ApSeq}: {Describe(entries)}");
            Replaying = true;
            try
            {
                foreach (NetEntry e in entries)
                {
                    if (e.Command != null)
                    {
                        state.Get<PlayerCommandQueue>().Add(e.Command);
                    }
                    else
                    {
                        Execute(state, e);
                    }
                }
            }
            finally
            {
                Replaying = false;
            }
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
            string description = StateHash.Describe(state.Get<Creatures>(), state.Get<RaidMetadata>().TurnNumber);
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
            if (localHash == remoteHash || _desyncReported)
            {
                return;
            }
            _desyncReported = true;
            Log.Error($"DESYNC at ap={seq}\n  local:  {localDescription}\n  remote: {remoteDescription}");
            Notify($"Co-op: the two games went out of sync at action {seq}. Send Player.log to the mod author.");
        }
    }
}
