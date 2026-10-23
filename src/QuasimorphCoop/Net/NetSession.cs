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
    // 1. Host (in a mission with a 2-merc party) runs coop_host and invites a friend.
    // 2. Friend accepts from the main menu; their game sends Hello (versions).
    // 3. Host saves, sends its slot files, and both machines load that same save
    //    (client into hidden slot 9). Party merc 0 belongs to the host, merc 1 to the client.
    // 4. Once both are in, the host sends a seed for the gameplay RNG and lockstep starts.
    // 5. Whoever owns the active merc plays normally; their inputs (queued commands plus the few
    //    direct actions) are sent and replayed on the other machine at the same action point.
    //    The other machine blocks local input meanwhile. After every action point both sides
    //    exchange a state hash to catch desyncs.
    internal static class NetSession
    {
        public const string ProtocolVersion = "1";

        private enum Role { None, Host, Client }

        private enum Phase { Idle, Lobby, Handshake, Loading, WaitingGo, Running }

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

        // A session is being set up or running: local actions must not reach the simulation unsent.
        public static bool Engaged => _phase >= Phase.Loading;

        public static bool Running => _phase == Phase.Running;

        public static string Status => $"role={_role} phase={_phase} peer={SteamNet.Instance?.PeerName ?? "none"} ap={_apSeq} inbox={_inbox.Count}";

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

        // ---------------------------------------------------------------- setup

        public static string StartHosting()
        {
            if (_phase != Phase.Idle)
            {
                return "A co-op session is already active: " + Status;
            }
            if (!SteamNet.SteamReady)
            {
                return "Steam isn't initialized.";
            }
            if (!CoopSession.InDungeon || CoopSession.Party.Count != 2)
            {
                return "Start a mission and add your friend's merc first (coop_add <index>); the party must have exactly 2 mercs.";
            }
            _role = Role.Host;
            _phase = Phase.Lobby;
            SteamNet.Ensure();
            SteamNet.Instance.CreateLobby();
            return $"Creating a friends-only lobby. Your friend will play {CoopSession.Party[1].Mercenary?.AgentName}. "
                + "They must have the same game version and this mod, and be on the main menu when they accept.";
        }

        public static void OnJoinedHost()
        {
            if (UnityEngine.Object.FindObjectOfType<MainMenuGameMode>() == null)
            {
                Notify("Co-op: go back to the main menu, then accept the invite again.");
                SteamNet.Instance.Leave();
                return;
            }
            _role = Role.Client;
            _phase = Phase.Handshake;
            _incoming.Clear();
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

        private static State GetGlobalState()
        {
            return Traverse.Create(UnityEngine.Object.FindObjectOfType<GameModeStateMachine>()).Field("_state").GetValue<State>();
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
            if (_subscribed != null)
            {
                _subscribed.OnActionPointsProcessed -= OnActionPointProcessed;
                _subscribed = null;
            }
            GameplayRng.Enabled = false;
            _loopback = Loopback.None;
            _phase = Phase.Idle;
            _role = Role.None;
            _inbox.Clear();
            _outbox.Clear();
            _incoming.Clear();
            _hostLoaded = _clientReady = false;
            SteamNet.Instance?.Leave();
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
                    case MsgType.Reject: Stop("host rejected: " + r.ReadString()); break;
                    case MsgType.FileChunk: OnFileChunk(r); break;
                    case MsgType.FilesDone: OnFilesDone(r); break;
                    case MsgType.Ready: _clientReady = true; Log.Info("Client is ready."); TryGo(); break;
                    case MsgType.Go: StartLockstep(r.ReadInt32()); break;
                    case MsgType.Batch: _inbox.Enqueue(new Batch { ApSeq = r.ReadInt32(), Payload = r.ReadBytes(data.Length - 5) }); break;
                    case MsgType.Hash: OnRemoteHash(r.ReadInt32(), r.ReadString(), r.ReadString()); break;
                    case MsgType.Bye: Stop($"{SteamNet.Instance.PeerName} left: {r.ReadString()}"); break;
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
            string myMods = ModList(CoopSession.State);
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
            Notify($"Co-op: {SteamNet.Instance.PeerName} joined. Syncing...");
            SendWorldAndReload();
        }

        private static void SendWorldAndReload()
        {
            State state = CoopSession.State;
            state.Get<SaveManager>().SaveGame();
            int slot = state.Get<SavedGameMetadata>().Slot;
            Dictionary<string, byte[]> files = SlotFiles.Read(slot);
            int bytes = 0;
            foreach (KeyValuePair<string, byte[]> file in files)
            {
                int total = Math.Max(1, (file.Value.Length + SteamNet.MaxChunkBytes - 1) / SteamNet.MaxChunkBytes);
                for (int i = 0; i < total; i++)
                {
                    int offset = i * SteamNet.MaxChunkBytes;
                    int length = Math.Min(SteamNet.MaxChunkBytes, file.Value.Length - offset);
                    SteamNet.Instance.Send(Wire.Build(MsgType.FileChunk, w =>
                    {
                        w.Write(file.Key);
                        w.Write(i);
                        w.Write(total);
                        w.Write(length);
                        w.Write(file.Value, offset, length);
                    }));
                }
                bytes += file.Value.Length;
            }
            _seed = Environment.TickCount;
            SteamNet.Instance.Send(Wire.Build(MsgType.FilesDone, w =>
            {
                w.Write(files.Count);
                w.Write(_seed);
            }));
            Log.Info($"Sent {files.Count} files ({bytes} bytes) from slot {slot}; reloading it.");
            _phase = Phase.Loading;
            _hostLoaded = _clientReady = false;
            SteamNet.Instance.StartCoroutine(ReloadSlot(slot));
        }

        // Same as the game's LoadLastSave, but from the regular (non-auto) save.
        private static IEnumerator ReloadSlot(int slot)
        {
            GameModeStateMachine machine = UnityEngine.Object.FindObjectOfType<GameModeStateMachine>();
            AccessTools.Method(typeof(GameModeStateMachine), "KillDungeon").Invoke(machine, Array.Empty<object>());
            yield return new WaitForEndOfFrame();
            IEnumerator start = (IEnumerator)AccessTools.Method(typeof(GameModeStateMachine), "ProcessStartGame")
                .Invoke(machine, new object[] { false, slot, null, false });
            machine.StartCoroutine(start);
        }

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
            Dictionary<string, byte[]> files = _incoming.ToDictionary(kv => kv.Key, kv => kv.Value.SelectMany(c => c).ToArray());
            _incoming.Clear();
            SlotFiles.Write(SlotFiles.ClientSlot, files);
            Log.Info($"Received {files.Count} files ({files.Values.Sum(f => f.Length)} bytes); loading slot {SlotFiles.ClientSlot}.");
            _phase = Phase.Loading;
            GameModeStateMachine machine = UnityEngine.Object.FindObjectOfType<GameModeStateMachine>();
            AccessTools.Method(typeof(GameModeStateMachine), "MainMenuFinished").Invoke(machine, new object[] { false, SlotFiles.ClientSlot, null });
        }

        public static void OnDungeonStarted()
        {
            if (_phase != Phase.Loading)
            {
                return;
            }
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
        }

        public static void OnDungeonFinished()
        {
            if (_phase != Phase.Idle && _phase != Phase.Loading)
            {
                // A new floor is generated from a time-based seed, so the two worlds would differ.
                Stop("the mission or floor ended; host again on the new floor");
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
                Notify($"Co-op started with {SteamNet.Instance.PeerName}. You control {mine.Mercenary?.AgentName}.");
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
