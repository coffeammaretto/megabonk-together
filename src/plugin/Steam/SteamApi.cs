using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MegabonkTogether.Steam
{
    /// <summary>
    /// Direct calls into the game's steam_api64.dll (flat API). The game's Steamworks.NET wrapper is not used:
    /// its generic callback types are not instantiated in the IL2CPP build, and the game dispatches Steam callbacks
    /// manually, consuming every callback and call result (lobbies are unusable: their results never reach us).
    /// Only networking is used: it is polled, and its session request callback is a plain function pointer.
    /// The game initializes Steam itself, this only fetches the interfaces.
    /// </summary>
    internal static class SteamApi
    {
        private const string Dll = "steam_api64";

        public const int SEND_UNRELIABLE_NO_NAGLE = 1;
        public const int SEND_RELIABLE = 8;
        public const int SEND_AUTO_RESTART_BROKEN_SESSION = 32;

        private const int IDENTITY_SIZE = 136;
        private const int RESULT_OK = 1;

        // Regular user accounts: universe public, account type individual, desktop instance
        private const ulong INDIVIDUAL_PREFIX = 0x0110000100000000UL;

        private static IntPtr user;
        private static IntPtr friends;
        private static IntPtr utils;
        private static IntPtr messages;
        private static IntPtr sockets;
        private static IntPtr networkingUtils;

        private static readonly Dictionary<ulong, IntPtr> identities = new();
        private static readonly object identitiesLock = new();

        public static bool IsAvailable { get; private set; }

        public static bool Initialize()
        {
            if (IsAvailable)
            {
                return true;
            }

            try
            {
                user = SteamAPI_SteamUser_v023();
                friends = SteamAPI_SteamFriends_v017();
                utils = SteamAPI_SteamUtils_v010();
                messages = SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
                sockets = SteamAPI_SteamNetworkingSockets_SteamAPI_v012();
                networkingUtils = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();

                IsAvailable = user != IntPtr.Zero && friends != IntPtr.Zero && utils != IntPtr.Zero
                    && messages != IntPtr.Zero && sockets != IntPtr.Zero && networkingUtils != IntPtr.Zero;

                if (IsAvailable)
                {
                    // Start finding the relay network now, the first connection is faster
                    SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(networkingUtils);
                    Plugin.Log.LogInfo($"Steam ready: {GetPersonaName()} ({GetLocalSteamId()}), app {SteamAPI_ISteamUtils_GetAppID(utils)}");
                }
                else
                {
                    Plugin.Log.LogWarning("Steam interfaces unavailable (is Steam running?)");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Steam API unavailable: {ex.Message}");
                IsAvailable = false;
            }

            return IsAvailable;
        }

        public static ulong GetLocalSteamId() => SteamAPI_ISteamUser_GetSteamID(user);

        public static uint AccountId(ulong steamId) => (uint)(steamId & 0xFFFFFFFFUL);

        public static string GetPersonaName() => PtrToUtf8(SteamAPI_ISteamFriends_GetPersonaName(friends));

        #region Room codes

        /// <summary>
        /// Short room code for a user: base36 of its account id (regular accounts all share the same upper bits)
        /// </summary>
        public static string SteamIdToCode(ulong steamId)
        {
            return (steamId & 0xFFFFFFFF00000000UL) == INDIVIDUAL_PREFIX ? ToBase36(steamId & 0xFFFFFFFFUL) : ToBase36(steamId);
        }

        public static bool TryCodeToSteamId(string code, out ulong steamId)
        {
            steamId = 0;
            if (!TryFromBase36(code, out var value) || value == 0)
            {
                return false;
            }

            steamId = value <= 0xFFFFFFFFUL ? INDIVIDUAL_PREFIX | value : value;
            return true;
        }

        private static string ToBase36(ulong value)
        {
            const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var sb = new StringBuilder();
            do
            {
                sb.Insert(0, digits[(int)(value % 36)]);
                value /= 36;
            } while (value > 0);
            return sb.ToString();
        }

        private static bool TryFromBase36(string code, out ulong value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            foreach (var c in code.Trim().ToUpperInvariant())
            {
                int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'Z' ? c - 'A' + 10 : -1;
                if (digit < 0 || value > (ulong.MaxValue - (ulong)digit) / 36)
                {
                    return false;
                }
                value = value * 36 + (ulong)digit;
            }
            return true;
        }

        #endregion

        #region Networking messages

        public static bool SendMessage(ulong steamId, byte[] data, int length, int flags, int channel)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var result = SteamAPI_ISteamNetworkingMessages_SendMessageToUser(messages, GetIdentity(steamId), handle.AddrOfPinnedObject(), (uint)length, flags, channel);
                return result == RESULT_OK;
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>
        /// Drain one channel. The callback gets the sender, a buffer and the length; the buffer is reused.
        /// </summary>
        public static int ReceiveMessages(int channel, IntPtr[] slots, ref byte[] buffer, Action<ulong, byte[], int> onMessage)
        {
            var count = SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(messages, channel, slots, slots.Length);
            for (var i = 0; i < count; i++)
            {
                var message = slots[i];
                try
                {
                    // SteamNetworkingMessage_t: data pointer, size, connection, then the sender identity
                    var data = Marshal.ReadIntPtr(message, 0);
                    var size = Marshal.ReadInt32(message, 8);
                    var sender = SteamAPI_SteamNetworkingIdentity_GetSteamID64(message + 16);

                    if (size > buffer.Length)
                    {
                        buffer = new byte[size];
                    }
                    Marshal.Copy(data, buffer, 0, size);
                    onMessage(sender, buffer, size);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Steam message handling failed: {ex.Message}");
                }
                finally
                {
                    SteamAPI_SteamNetworkingMessage_t_Release(message);
                }
            }
            return count;
        }

        public static bool AcceptSession(ulong steamId) => SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(messages, GetIdentity(steamId));

        public static void CloseSession(ulong steamId) => SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(messages, GetIdentity(steamId));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SessionRequestCallback(IntPtr request);

        // Kept alive here: native code holds a pointer to it
        private static SessionRequestCallback sessionRequestCallback;
        private static Action<ulong> sessionRequestHandler;

        /// <summary>
        /// Called (from any thread) when someone we did not talk to first wants a session. Null to stop listening.
        /// </summary>
        public static void SetSessionRequestHandler(Action<ulong> handler)
        {
            sessionRequestHandler = handler;
            if (handler == null)
            {
                SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionRequest(networkingUtils, IntPtr.Zero);
                return;
            }

            sessionRequestCallback ??= request =>
            {
                try
                {
                    // SteamNetworkingMessagesSessionRequest_t starts with the remote identity
                    sessionRequestHandler?.Invoke(SteamAPI_SteamNetworkingIdentity_GetSteamID64(request));
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Steam session request handling failed: {ex.Message}");
                }
            };
            SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionRequest(networkingUtils, Marshal.GetFunctionPointerForDelegate(sessionRequestCallback));
        }

        /// <summary>
        /// Dispatches the networking function pointer callbacks (session requests)
        /// </summary>
        public static void RunNetworkingCallbacks() => SteamAPI_ISteamNetworkingSockets_RunCallbacks(sockets);

        private static IntPtr GetIdentity(ulong steamId)
        {
            lock (identitiesLock)
            {
                if (!identities.TryGetValue(steamId, out var identity))
                {
                    identity = Marshal.AllocHGlobal(IDENTITY_SIZE);
                    for (var i = 0; i < IDENTITY_SIZE; i++)
                    {
                        Marshal.WriteByte(identity, i, 0);
                    }
                    SteamAPI_SteamNetworkingIdentity_SetSteamID64(identity, steamId);
                    identities[steamId] = identity;
                }
                return identity;
            }
        }

        #endregion

        private static string PtrToUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
            {
                return "";
            }

            var length = 0;
            while (Marshal.ReadByte(ptr, length) != 0)
            {
                length++;
            }
            var bytes = new byte[length];
            Marshal.Copy(ptr, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        #region Imports

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamUser_v023();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamFriends_v017();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamUtils_v010();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamNetworkingSockets_SteamAPI_v012();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern uint SteamAPI_ISteamUtils_GetAppID(IntPtr self);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, IntPtr identity, IntPtr data, uint size, int flags, int channel);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int channel, [Out] IntPtr[] messages, int maxMessages);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, IntPtr identity);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, IntPtr identity);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void SteamAPI_SteamNetworkingIdentity_SetSteamID64(IntPtr identity, ulong steamId);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern ulong SteamAPI_SteamNetworkingIdentity_GetSteamID64(IntPtr identity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void SteamAPI_ISteamNetworkingSockets_RunCallbacks(IntPtr self);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionRequest(IntPtr self, IntPtr callback);

        #endregion
    }
}
