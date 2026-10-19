using MemoryPack;

namespace MegabonkTogether.Common.Messages.WsMessages
{
    /// <summary>
    /// Steam rooms: a client's join request and heartbeat, also the host's heartbeat
    /// </summary>
    [MemoryPackable]
    public partial class SteamHello : IWsMessage
    {
        public string Version { get; set; }
        public string Name { get; set; }
    }

    /// <summary>
    /// Steam rooms: the host refuses a client (other mod version, room full or run started)
    /// </summary>
    [MemoryPackable]
    public partial class SteamJoinRejected : IWsMessage
    {
        public string Reason { get; set; }
    }
}
