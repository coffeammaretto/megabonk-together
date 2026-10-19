using Il2CppInterop.Runtime;
using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using MegabonkTogether.Common.Messages.WsMessages;
using MegabonkTogether.Common.Models;
using MegabonkTogether.Configuration;
using MegabonkTogether.Scripts;
using MegabonkTogether.Services;
using MemoryPack;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MegabonkTogether.Steam
{
    /// <summary>
    /// Friendlies rooms through Steam instead of the matchmaking server. The room code is the host's Steam account.
    /// Clients open a Steam session with the host and say hello; the host is the room authority: it accepts the
    /// session, checks the mod version, and sends the match info (players, seed) to everyone, like the server did.
    /// Both sides repeat the hello every second, silence means the other side left. Game traffic goes through
    /// <see cref="SteamTransport"/>. Random matchmaking, and everything when Steam networking is disabled, stays on
    /// the server (WebsocketClientService).
    /// </summary>
    internal class SteamMatchmakingService(
        IUdpClientService udpClientService,
        IPlayerManagerService playerManagerService,
        ISteamTransport transport,
        WebsocketClientService serverMatchmaking) : IWebsocketClientService
    {
        private const int MAX_PLAYERS = 6;
        private const int LOOP_INTERVAL_MS = 50;
        private static readonly TimeSpan HELLO_INTERVAL = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan PEER_TIMEOUT = TimeSpan.FromSeconds(8);
        // Join wait + connection wait stay under the menu's 30 s
        private static readonly TimeSpan JOIN_TIMEOUT = TimeSpan.FromSeconds(10);

        private bool usesServer = false;
        private bool isHost = false;
        private ulong selfSteamId = 0;
        private ulong hostSteamId = 0;
        private uint seed = 0;
        private volatile bool isRoomLocked = false;
        private bool hasReceivedMatchInfo = false;
        private bool hasLostHost = false;
        private DateTime joinStartedAt;
        private DateTime lastHostMessageAt;

        // Host only, touched by the loop thread
        private readonly Dictionary<ulong, DateTime> members = [];
        private readonly Dictionary<ulong, string> memberNames = [];
        private readonly ConcurrentQueue<ulong> sessionRequests = new();

        private NetworkHandler networkHandler;
        private int session = 0;
        private Thread loopThread;
        private volatile bool running = false;

        public async Task ConnectAndMatchAsync(string serverUrl, uint rdvServerPort, NetworkHandler networkHandler)
        {
            usesServer = !ModConfig.UseSteamNetworking.Value || Plugin.Instance.Mode.Mode != NetworkModeType.Friendlies;
            if (usesServer)
            {
                await serverMatchmaking.ConnectAndMatchAsync(serverUrl, rdvServerPort, networkHandler);
                return;
            }

            if (running)
            {
                Plugin.Log.LogWarning("Steam room already active");
                return;
            }

            this.networkHandler = networkHandler;
            session++;

            if (!SteamApi.Initialize())
            {
                networkHandler.OnFailedToConnectToMatchMaker("Steam is not available. Is Steam running?");
                return;
            }

            if (!udpClientService.Initialize())
            {
                networkHandler.OnFailedToConnectToMatchMaker("Could not start networking");
                return;
            }

            udpClientService.ResetHandledHost();

            selfSteamId = SteamApi.GetLocalSteamId();
            isHost = Plugin.Instance.Mode.Role == Role.Host;
            isRoomLocked = false;
            hasReceivedMatchInfo = false;
            hasLostHost = false;
            transport.Start(udpClientService.GetLocalPort());

            if (isHost)
            {
                hostSteamId = selfSteamId;
                seed = (uint)new Random().Next();
                SteamApi.SetSessionRequestHandler(sessionRequests.Enqueue);
                Plugin.Instance.Mode.RoomCode = SteamApi.SteamIdToCode(selfSteamId);
                Plugin.Log.LogInfo($"Steam room open, code {Plugin.Instance.Mode.RoomCode}");

                networkHandler.OnConnectedToMatchMaker();
                StartLoop();
                networkHandler.OnMatchFound(true);
            }
            else
            {
                var code = Plugin.Instance.Mode.RoomCode;
                if (!SteamApi.TryCodeToSteamId(code, out hostSteamId) || hostSteamId == selfSteamId)
                {
                    transport.Stop();
                    networkHandler.OnFailedToConnectToMatchMaker($"Invalid room code {code}");
                    return;
                }

                transport.HostProxyPort = transport.AddPeer(hostSteamId);
                joinStartedAt = DateTime.UtcNow;
                lastHostMessageAt = joinStartedAt;
                Plugin.Log.LogInfo($"Joining Steam room of {hostSteamId}");

                networkHandler.OnConnectedToMatchMaker();
                StartLoop();
            }
        }

        private void StartLoop()
        {
            running = true;
            loopThread = new Thread(Loop) { IsBackground = true, Name = "SteamMatchmaking" };
            loopThread.Start();
        }

        private void Loop()
        {
            var thread = IL2CPP.il2cpp_thread_attach(IL2CPP.il2cpp_domain_get());
            var nextHello = DateTime.MinValue;

            try
            {
                while (running)
                {
                    try
                    {
                        SteamApi.RunNetworkingCallbacks();

                        while (sessionRequests.TryDequeue(out var requester))
                        {
                            OnSessionRequest(requester);
                        }

                        while (transport.TryReceiveControl(out var sender, out var data))
                        {
                            HandleControl(sender, data);
                        }

                        var now = DateTime.UtcNow;
                        if (now >= nextHello)
                        {
                            nextHello = now + HELLO_INTERVAL;
                            if (isHost)
                            {
                                HostTick(now);
                            }
                            else
                            {
                                ClientTick(now);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"Steam matchmaking loop error: {ex}");
                    }

                    Thread.Sleep(LOOP_INTERVAL_MS);
                }
            }
            finally
            {
                IL2CPP.il2cpp_thread_detach(thread);
            }
        }

        private void OnSessionRequest(ulong requester)
        {
            if (!isHost || requester == 0)
            {
                return;
            }

            // Control messages only: the game route is opened once the hello passes the checks
            Plugin.Log.LogInfo($"Steam session request from {requester}");
            SteamApi.AcceptSession(requester);
        }

        private void HostTick(DateTime now)
        {
            var hello = new SteamHello { Version = ModVersionInfo.CurrentVersion, Name = ModConfig.PlayerName.Value };
            foreach (var member in members.Keys)
            {
                SendControl(member, hello);
            }

            foreach (var member in members.Where(m => now - m.Value > PEER_TIMEOUT).Select(m => m.Key).ToList())
            {
                Plugin.Log.LogInfo($"Steam member {member} timed out");
                RemoveMember(member);
            }
        }

        private void ClientTick(DateTime now)
        {
            if (hasLostHost)
            {
                return;
            }

            SendControl(hostSteamId, new SteamHello { Version = ModVersionInfo.CurrentVersion, Name = ModConfig.PlayerName.Value });

            if (!hasReceivedMatchInfo && now - joinStartedAt > JOIN_TIMEOUT)
            {
                Plugin.Log.LogWarning($"No answer from Steam room host {hostSteamId}");
                hasLostHost = true;
                networkHandler?.OnNetworkInterrupted("Room not found: the host is not hosting, or the code is wrong");
            }
            else if (hasReceivedMatchInfo && now - lastHostMessageAt > PEER_TIMEOUT)
            {
                Plugin.Log.LogWarning($"Steam room host {hostSteamId} timed out");
                hasLostHost = true;
                OnHostDisconnected();
            }
        }

        private void HandleControl(ulong sender, byte[] data)
        {
            IWsMessage message;
            try
            {
                message = MemoryPackSerializer.Deserialize<IWsMessage>(data);
            }
            catch (Exception)
            {
                Plugin.Log.LogWarning($"Undecodable Steam control message from {sender}: does every player have the same mod build?");
                return;
            }

            if (isHost)
            {
                HandleControlAsHost(sender, message);
            }
            else if (sender == hostSteamId)
            {
                lastHostMessageAt = DateTime.UtcNow;
                HandleControlAsClient(message);
            }
        }

        private void HandleControlAsHost(ulong sender, IWsMessage message)
        {
            switch (message)
            {
                case SteamHello hello:
                    if (members.ContainsKey(sender))
                    {
                        members[sender] = DateTime.UtcNow;
                        return;
                    }

                    var reason = hello.Version != ModVersionInfo.CurrentVersion
                        ? $"The host runs mod version {ModVersionInfo.CurrentVersion}, you have {hello.Version}"
                        : isRoomLocked ? "Game has already started"
                        : members.Count >= MAX_PLAYERS - 1 ? "Room is full"
                        : null;

                    if (reason != null)
                    {
                        Plugin.Log.LogInfo($"Steam join refused for {sender} ({hello.Name}): {reason}");
                        SendControl(sender, new SteamJoinRejected { Reason = reason });
                        return;
                    }

                    members[sender] = DateTime.UtcNow;
                    transport.AddPeer(sender);
                    Plugin.Log.LogInfo($"Steam member {sender} ({hello.Name}) joined");

                    var matchInfo = BuildMatchInfo(hello.Name, sender);
                    foreach (var member in members.Keys)
                    {
                        SendControl(member, matchInfo);
                    }
                    _ = HandleMatchAsync(matchInfo);
                    break;
                case ClientDisconnected:
                    if (members.ContainsKey(sender))
                    {
                        Plugin.Log.LogInfo($"Steam member {sender} left");
                        RemoveMember(sender);
                    }
                    break;
            }
        }

        private MatchInfo BuildMatchInfo(string newMemberName, ulong newMember)
        {
            memberNames[newMember] = newMemberName;

            var hostAccountId = SteamApi.AccountId(selfSteamId);
            var peers = new List<PeerInfo>
            {
                new() { ConnectionId = hostAccountId, IsHost = true, HostConnectionId = hostAccountId, Name = ModConfig.PlayerName.Value }
            };
            peers.AddRange(members.Keys.Select(id => new PeerInfo
            {
                ConnectionId = SteamApi.AccountId(id),
                IsHost = false,
                HostConnectionId = hostAccountId,
                Name = memberNames.GetValueOrDefault(id, "")
            }));

            return new MatchInfo
            {
                Peers = peers,
                Seed = seed,
                EnabledSharedExperience = ModConfig.EnabledSharedExperience.Value
            };
        }

        private void RemoveMember(ulong member)
        {
            members.Remove(member);
            memberNames.Remove(member);
            var accountId = SteamApi.AccountId(member);

            // Same as the server's notification: the game connection itself times out on its own
            playerManagerService.RemovePlayer(accountId);
            udpClientService.RemovePeer(accountId);
            var message = new ClientDisconnected { RoomCode = Plugin.Instance.Mode.RoomCode, ClientConnectionId = accountId };
            foreach (var other in members.Keys)
            {
                SendControl(other, message);
            }
        }

        private void HandleControlAsClient(IWsMessage message)
        {
            switch (message)
            {
                case MatchInfo matchInfo:
                    var isFirst = !hasReceivedMatchInfo;
                    hasReceivedMatchInfo = true;
                    OnClientMatchInfo(matchInfo, isFirst);
                    break;
                case SteamJoinRejected rejected:
                    if (!hasLostHost)
                    {
                        Plugin.Log.LogWarning($"Steam room refused: {rejected.Reason}");
                        hasLostHost = true;
                        networkHandler?.OnNetworkInterrupted(rejected.Reason);
                    }
                    break;
                case ClientDisconnected clientDisconnected:
                    Plugin.Log.LogWarning($"Client {clientDisconnected.ClientConnectionId} left the room");
                    playerManagerService.RemovePlayer(clientDisconnected.ClientConnectionId);
                    udpClientService.RemovePeer(clientDisconnected.ClientConnectionId);
                    break;
                case HostDisconnected:
                    if (!hasLostHost)
                    {
                        hasLostHost = true;
                        OnHostDisconnected();
                    }
                    break;
            }
        }

        private void OnClientMatchInfo(MatchInfo matchInfo, bool isFirst)
        {
            if (!isFirst || udpClientService.HasHandledHost())
            {
                var selfAccountId = SteamApi.AccountId(selfSteamId);
                var allPlayers = playerManagerService.GetAllPlayers();
                foreach (var peer in matchInfo.Peers)
                {
                    if (!allPlayers.Any(p => p.ConnectionId == peer.ConnectionId))
                    {
                        playerManagerService.AddPlayer(peer.ConnectionId, peer.IsHost.Value, peer.ConnectionId == selfAccountId);
                    }
                }
                return;
            }

            Plugin.Log.LogInfo($"Steam room match info: {matchInfo.Peers.Count()} players");
            _ = HandleMatchAsync(matchInfo);
        }

        private async Task HandleMatchAsync(MatchInfo matchInfo)
        {
            var handler = networkHandler;
            var matchSession = session;
            try
            {
                var hasFoundMatch = await udpClientService.HandleMatch(matchInfo, SteamApi.AccountId(selfSteamId), null, 0, matchInfo.EnabledSharedExperience);
                Plugin.Log.LogInfo($"Steam HandleMatch result: {hasFoundMatch}");

                if (matchSession != session || handler == null)
                {
                    return;
                }

                // The host's room is already up: one player failing to connect must not flag it as interrupted
                if (isHost && !hasFoundMatch)
                {
                    Plugin.Log.LogWarning("A player could not connect through Steam");
                    return;
                }

                handler.OnMatchFound(hasFoundMatch);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Steam HandleMatch failed: {ex}");
            }
        }

        private void OnHostDisconnected()
        {
            if (GameManager.Instance?.player != null)
            {
                // In a run the game connection notices it and goes back to the menu
                return;
            }

            udpClientService.CancelAnyNatIntroduction();
            networkHandler?.OnNetworkInterrupted("Host has disconnected");

            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                Plugin.Instance.NetworkHandler.ResetNetworking();
                Plugin.GoToMainMenu();
            });
        }

        private void SendControl(ulong steamId, IWsMessage message)
        {
            transport.SendControl(steamId, MemoryPackSerializer.Serialize(message));
        }

        public Task<bool> SendGameStarting()
        {
            if (usesServer)
            {
                return serverMatchmaking.SendGameStarting();
            }

            // Same as the server's room lock: nobody joins a run in progress
            isRoomLocked = true;
            return Task.FromResult(true);
        }

        public Task SendRunStatistics(int playerCount, string mapName, int stageLevel, List<string> characters)
        {
            return usesServer ? serverMatchmaking.SendRunStatistics(playerCount, mapName, stageLevel, characters) : Task.CompletedTask;
        }

        public async Task Reset()
        {
            running = false;
            session++;
            if (loopThread != null && loopThread != Thread.CurrentThread)
            {
                loopThread.Join(1000);
            }
            loopThread = null;

            if (transport.IsActive)
            {
                // Tell the others right away instead of letting them time out
                if (isHost)
                {
                    SteamApi.SetSessionRequestHandler(null);
                    foreach (var member in members.Keys)
                    {
                        SendControl(member, new HostDisconnected { RoomCode = Plugin.Instance.Mode.RoomCode, HostConnectionId = SteamApi.AccountId(selfSteamId) });
                    }
                }
                else if (hostSteamId != 0)
                {
                    SendControl(hostSteamId, new ClientDisconnected { ClientConnectionId = SteamApi.AccountId(selfSteamId) });
                }

                await Task.Delay(100);
                transport.Stop();
            }

            members.Clear();
            memberNames.Clear();
            while (sessionRequests.TryDequeue(out _)) { }
            isHost = false;
            selfSteamId = 0;
            hostSteamId = 0;
            seed = 0;
            isRoomLocked = false;
            hasReceivedMatchInfo = false;
            hasLostHost = false;
            networkHandler = null;
            usesServer = false;

            await serverMatchmaking.Reset();
        }
    }
}
