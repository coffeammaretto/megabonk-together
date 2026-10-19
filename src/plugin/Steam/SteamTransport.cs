using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace MegabonkTogether.Steam
{
    /// <summary>
    /// Carries LiteNetLib's UDP packets over Steam networking (P2P with Steam's relays, no NAT punching, no own server).
    /// Each remote player gets a local UDP socket on 127.0.0.1: LiteNetLib talks to that socket as if it was the player,
    /// the transport forwards everything to and from Steam. LiteNetLib keeps doing reliability, ordering and latency.
    /// A second Steam channel carries the lobby control messages.
    /// </summary>
    public interface ISteamTransport
    {
        public bool IsActive { get; }

        /// <summary>
        /// Client: local port standing for the host, LiteNetLib connects to it
        /// </summary>
        public int? HostProxyPort { get; set; }

        public void Start(int localGamePort);
        public int AddPeer(ulong steamId);
        public void SendControl(ulong steamId, byte[] data);
        public bool TryReceiveControl(out ulong sender, out byte[] data);
        public void Stop();
    }

    internal class SteamTransport : ISteamTransport
    {
        private const int DATA_CHANNEL = 0;
        private const int CONTROL_CHANNEL = 1;
        private const int SIO_UDP_CONNRESET = -1744830452;
        private const int SELECT_TIMEOUT_MICROSECONDS = 1000;

        private sealed class Proxy
        {
            public ulong SteamId;
            public Socket Socket;
            public int Port;
        }

        private readonly ConcurrentDictionary<ulong, Proxy> proxiesBySteamId = new();
        private readonly ConcurrentDictionary<Socket, Proxy> proxiesBySocket = new();
        private readonly ConcurrentQueue<(ulong sender, byte[] data)> controlMessages = new();
        private IPEndPoint gameEndPoint;
        private Thread pumpThread;
        private volatile bool running;
        private int pumpErrors = 0;

        public bool IsActive => running;
        public int? HostProxyPort { get; set; }

        public void Start(int localGamePort)
        {
            if (running)
            {
                return;
            }

            gameEndPoint = new IPEndPoint(IPAddress.Loopback, localGamePort);
            pumpErrors = 0;
            running = true;
            pumpThread = new Thread(Pump) { IsBackground = true, Name = "SteamTransport" };
            pumpThread.Start();
            Plugin.Log.LogInfo($"Steam transport started (game port {localGamePort})");
        }

        public int AddPeer(ulong steamId)
        {
            var proxy = proxiesBySteamId.GetOrAdd(steamId, id =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                try
                {
                    // Windows: an ICMP "port unreachable" would make every later receive throw
                    socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
                }
                catch (Exception)
                {
                    // Not Windows
                }

                var created = new Proxy { SteamId = id, Socket = socket, Port = ((IPEndPoint)socket.LocalEndPoint).Port };
                proxiesBySocket[socket] = created;
                Plugin.Log.LogInfo($"Steam peer {id} mapped to local port {created.Port}");
                return created;
            });

            // No-op when we talked first, accepts their pending request otherwise
            SteamApi.AcceptSession(steamId);
            return proxy.Port;
        }

        public void SendControl(ulong steamId, byte[] data)
        {
            SteamApi.SendMessage(steamId, data, data.Length, SteamApi.SEND_RELIABLE | SteamApi.SEND_AUTO_RESTART_BROKEN_SESSION, CONTROL_CHANNEL);
        }

        public bool TryReceiveControl(out ulong sender, out byte[] data)
        {
            if (controlMessages.TryDequeue(out var message))
            {
                sender = message.sender;
                data = message.data;
                return true;
            }

            sender = 0;
            data = null;
            return false;
        }

        private void Pump()
        {
            var slots = new IntPtr[64];
            var steamBuffer = new byte[2048];
            var udpBuffer = new byte[65536];
            var readable = new List<Socket>();
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);

            void ToGame(ulong sender, byte[] data, int length)
            {
                if (proxiesBySteamId.TryGetValue(sender, out var proxy))
                {
                    proxy.Socket.SendTo(data, 0, length, SocketFlags.None, gameEndPoint);
                }
            }

            void ToControl(ulong sender, byte[] data, int length)
            {
                var copy = new byte[length];
                Buffer.BlockCopy(data, 0, copy, 0, length);
                controlMessages.Enqueue((sender, copy));
            }

            while (running)
            {
                try
                {
                    SteamApi.ReceiveMessages(DATA_CHANNEL, slots, ref steamBuffer, ToGame);
                    SteamApi.ReceiveMessages(CONTROL_CHANNEL, slots, ref steamBuffer, ToControl);

                    readable.Clear();
                    readable.AddRange(proxiesBySocket.Keys);
                    if (readable.Count == 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    // Waits up to 1 ms for LiteNetLib to send something, doubles as the loop's pacing
                    Socket.Select(readable, null, null, SELECT_TIMEOUT_MICROSECONDS);
                    foreach (var socket in readable)
                    {
                        if (!proxiesBySocket.TryGetValue(socket, out var proxy))
                        {
                            continue;
                        }

                        while (socket.Available > 0)
                        {
                            var length = socket.ReceiveFrom(udpBuffer, ref from);
                            SteamApi.SendMessage(proxy.SteamId, udpBuffer, length, SteamApi.SEND_UNRELIABLE_NO_NAGLE | SteamApi.SEND_AUTO_RESTART_BROKEN_SESSION, DATA_CHANNEL);
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Stopping
                }
                catch (Exception ex)
                {
                    pumpErrors++;
                    if (pumpErrors <= 5 || pumpErrors % 1000 == 0)
                    {
                        Plugin.Log.LogWarning($"Steam transport error ({pumpErrors}): {ex.Message}");
                    }
                    Thread.Sleep(1);
                }
            }
        }

        public void Stop()
        {
            if (!running && proxiesBySteamId.IsEmpty)
            {
                return;
            }

            running = false;
            pumpThread?.Join(500);
            pumpThread = null;

            foreach (var proxy in proxiesBySteamId.Values)
            {
                try
                {
                    proxy.Socket.Close();
                    SteamApi.CloseSession(proxy.SteamId);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Closing Steam peer {proxy.SteamId} failed: {ex.Message}");
                }
            }

            proxiesBySteamId.Clear();
            proxiesBySocket.Clear();
            while (controlMessages.TryDequeue(out _)) { }
            HostProxyPort = null;
            Plugin.Log.LogInfo("Steam transport stopped");
        }
    }
}
