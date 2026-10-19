using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// A player got downed (bleeding out, a teammate can revive it) or got back up
    /// </summary>
    [MemoryPackable]
    public partial class PlayerDownedChanged : IGameNetworkMessage
    {
        public uint PlayerId { get; set; }
        public bool Downed { get; set; }
    }
}
