using MemoryPack;
using System.Numerics;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// A player marked a spot (chest, shrine, danger...) for the team
    /// </summary>
    [MemoryPackable]
    public partial class PingPlaced : IGameNetworkMessage
    {
        public uint OwnerId { get; set; }
        public Vector3 Position { get; set; }
    }
}
