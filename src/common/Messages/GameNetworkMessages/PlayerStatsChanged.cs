using MemoryPack;
using System.Collections.Generic;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// A player's own final stats (indexed by EStat, luck and difficulty without the other players' share) and its character
    /// passive bonuses, which apply to the whole party
    /// </summary>
    [MemoryPackable]
    public partial class PlayerStatsChanged : IGameNetworkMessage
    {
        public uint OwnerId { get; set; }
        public float[] Stats { get; set; }
        public List<StatModifierModel> PassiveModifiers { get; set; }
    }
}
