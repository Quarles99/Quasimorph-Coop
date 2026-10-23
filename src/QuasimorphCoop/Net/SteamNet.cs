using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace QuasimorphCoop.Net
{
    // Steam lobby + SteamNetworkingMessages transport. Lives on a DontDestroyOnLoad object because
    // starting a session reloads the game mode. The game's SteamManager runs SteamAPI.RunCallbacks.
    internal class SteamNet : MonoBehaviour
    {
        private const int Channel = 0;
        private const int MaxMessagesPerFrame = 64;
        // Steam's reliable message limit is 512 KB; stay well under it for file chunks.
        public const int MaxChunkBytes = 128 * 1024;

        public static SteamNet Instance { get; private set; }

        public CSteamID Lobby { get; private set; } = CSteamID.Nil;

        public CSteamID Peer { get; private set; } = CSteamID.Nil;

        public bool HasPeer => Peer != CSteamID.Nil;

        // Keep references: Steamworks.NET unregisters callbacks that get garbage collected.
        private Callback<GameLobbyJoinRequested_t> _joinRequested;
        private Callback<LobbyEnter_t> _lobbyEntered;
        private Callback<LobbyChatUpdate_t> _lobbyChatUpdate;
        private Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequest;
        private Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailed;
        private CallResult<LobbyCreated_t> _lobbyCreated;
        private readonly IntPtr[] _messages = new IntPtr[MaxMessagesPerFrame];
        private bool _callbacksReady;
        private float _lastSend;
        private CSteamID _pendingLobby = CSteamID.Nil;

        public static void Ensure()
        {
            if (Instance != null)
            {
                return;
            }
            GameObject go = new GameObject("QMCoop_SteamNet");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<SteamNet>();
        }

        public static bool SteamReady => SteamManager.Initialized;

        private void EnsureCallbacks()
        {
            if (_callbacksReady || !SteamReady)
            {
                return;
            }
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            _lobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            _lobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            _sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
            _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _callbacksReady = true;
            Log.Info($"Steam networking ready as {SteamFriends.GetPersonaName()} ({SteamUser.GetSteamID()}).");

            // Accepting an invite while the game was closed launches it with +connect_lobby <id>.
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "+connect_lobby");
            if (i >= 0 && i + 1 < args.Length && ulong.TryParse(args[i + 1], out ulong lobbyId))
            {
                // The main menu doesn't exist yet this early; join once it does.
                Log.Info($"Launched with +connect_lobby {lobbyId}; joining at the main menu.");
                _pendingLobby = new CSteamID(lobbyId);
            }
        }

        public void JoinPendingLobby()
        {
            if (_pendingLobby != CSteamID.Nil)
            {
                CSteamID lobby = _pendingLobby;
                _pendingLobby = CSteamID.Nil;
                JoinLobby(lobby);
            }
        }

        public void OpenInviteDialog()
        {
            if (Lobby != CSteamID.Nil)
            {
                SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
            }
        }

        // Friends currently in a Quasimorph lobby (i.e. hosting co-op), for the main menu's join list.
        public List<KeyValuePair<string, CSteamID>> FriendsHosting()
        {
            var result = new List<KeyValuePair<string, CSteamID>>();
            if (!SteamReady)
            {
                return result;
            }
            AppId_t app = SteamUtils.GetAppID();
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                CSteamID friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (SteamFriends.GetFriendGamePlayed(friend, out FriendGameInfo_t game) && game.m_gameID.AppID() == app
                    && game.m_steamIDLobby.IsValid() && game.m_steamIDLobby != CSteamID.Nil)
                {
                    result.Add(new KeyValuePair<string, CSteamID>(SteamFriends.GetFriendPersonaName(friend), game.m_steamIDLobby));
                }
            }
            return result;
        }

        public void CreateLobby()
        {
            EnsureCallbacks();
            _lobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2));
        }

        public void JoinLobby(CSteamID lobby)
        {
            EnsureCallbacks();
            Log.Info($"Joining lobby {lobby}.");
            SteamMatchmaking.JoinLobby(lobby);
        }

        public void Leave()
        {
            if (HasPeer)
            {
                SteamNetworkingIdentity id = Identity(Peer);
                SteamNetworkingMessages.CloseSessionWithUser(ref id);
            }
            if (Lobby != CSteamID.Nil)
            {
                SteamMatchmaking.LeaveLobby(Lobby);
            }
            Lobby = CSteamID.Nil;
            Peer = CSteamID.Nil;
        }

        public string PeerName => HasPeer ? SteamFriends.GetFriendPersonaName(Peer) : "nobody";

        public bool Send(byte[] data)
        {
            if (!HasPeer)
            {
                return false;
            }
            SteamNetworkingIdentity id = Identity(Peer);
            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                EResult result = SteamNetworkingMessages.SendMessageToUser(ref id, handle.AddrOfPinnedObject(), (uint)data.Length,
                    Constants.k_nSteamNetworkingSend_Reliable, Channel);
                if (result == EResult.k_EResultOK)
                {
                    _lastSend = Time.realtimeSinceStartup;
                    return true;
                }
                // LimitExceeded just means the send buffer is full; callers retry.
                if (result != EResult.k_EResultLimitExceeded)
                {
                    Log.Warn($"Send of {data.Length} bytes failed: {result}.");
                }
                return false;
            }
            finally
            {
                handle.Free();
            }
        }

        private static SteamNetworkingIdentity Identity(CSteamID steamId)
        {
            SteamNetworkingIdentity id = new SteamNetworkingIdentity();
            id.SetSteamID(steamId);
            return id;
        }

        private void Update()
        {
            EnsureCallbacks();
            if (!_callbacksReady)
            {
                return;
            }
            int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, _messages, MaxMessagesPerFrame);
            for (int i = 0; i < count; i++)
            {
                SteamNetworkingMessage_t msg = SteamNetworkingMessage_t.FromIntPtr(_messages[i]);
                try
                {
                    CSteamID from = msg.m_identityPeer.GetSteamID();
                    byte[] data = new byte[msg.m_cbSize];
                    Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);
                    if (HasPeer && from != Peer)
                    {
                        Log.Warn($"Ignoring {data.Length} bytes from unexpected sender {from}.");
                        continue;
                    }
                    NetSession.OnMessage(data);
                }
                catch (Exception e)
                {
                    Log.Error("Failed to handle message: " + e);
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(_messages[i]);
                }
            }
            if (_pendingLobby != CSteamID.Nil && FindObjectOfType<MGSC.MainMenuGameMode>() != null)
            {
                JoinPendingLobby();
            }
            NetSession.Update();
            // Keep the session from idling out while someone sits in a menu.
            if (HasPeer && NetSession.Engaged && Time.realtimeSinceStartup - _lastSend > 10f)
            {
                Send(Wire.Build(MsgType.Ping));
            }
        }

        private void LateUpdate()
        {
            NetSession.Flush();
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                Log.Error($"Lobby creation failed: {result.m_eResult}.");
                NetSession.Notify("Co-op: couldn't create Steam lobby.");
                return;
            }
            Lobby = new CSteamID(result.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Lobby, "qmcoop", NetSession.ProtocolVersion);
            Log.Info($"Lobby {Lobby} created; opening invite dialog.");
            NetSession.Notify($"Co-op lobby {Lobby.m_SteamID} created. Invite a friend, or they can run: coop_join {Lobby.m_SteamID}");
            SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
        }

        // Friend clicked "Join game" / accepted an invite while the game is running.
        private void OnJoinRequested(GameLobbyJoinRequested_t request)
        {
            Log.Info($"Join requested for lobby {request.m_steamIDLobby} via {request.m_steamIDFriend}.");
            JoinLobby(request.m_steamIDLobby);
        }

        private void OnLobbyEntered(LobbyEnter_t entered)
        {
            CSteamID lobby = new CSteamID(entered.m_ulSteamIDLobby);
            CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobby);
            if (owner == SteamUser.GetSteamID())
            {
                return; // our own lobby; the host learns about the client from its Hello
            }
            Lobby = lobby;
            Peer = owner;
            Log.Info($"Entered lobby {lobby} hosted by {PeerName}.");
            NetSession.OnJoinedHost();
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t update)
        {
            const uint left = (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                | EChatMemberStateChange.k_EChatMemberStateChangeKicked | EChatMemberStateChange.k_EChatMemberStateChangeBanned);
            if ((update.m_rgfChatMemberStateChange & left) != 0 && HasPeer && update.m_ulSteamIDUserChanged == Peer.m_SteamID)
            {
                Log.Warn($"{PeerName} left the lobby.");
                NetSession.OnPeerLost("left the lobby");
            }
        }

        private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
        {
            CSteamID from = request.m_identityRemote.GetSteamID();
            bool memberOfLobby = Lobby != CSteamID.Nil && Enumerable.Range(0, SteamMatchmaking.GetNumLobbyMembers(Lobby))
                .Any(i => SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i) == from);
            if (!memberOfLobby || (HasPeer && from != Peer))
            {
                Log.Warn($"Rejected session request from {from}.");
                return;
            }
            Peer = from;
            SteamNetworkingIdentity id = request.m_identityRemote;
            SteamNetworkingMessages.AcceptSessionWithUser(ref id);
            Log.Info($"Accepted session with {PeerName}.");
        }

        private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t failed)
        {
            Log.Warn($"Session failed: {failed.m_info.m_eEndReason} {failed.m_info.m_szEndDebug}.");
            NetSession.OnPeerLost("connection failed");
        }
    }
}
