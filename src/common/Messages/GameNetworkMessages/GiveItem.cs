using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// A player gave a chest item to a teammate instead of taking it
    /// </summary>
    [MemoryPackable]
    public partial class GiveItem : IGameNetworkMessage
    {
        public uint FromId { get; set; }
        public uint ToId { get; set; }
        public int Item { get; set; }
    }
}
