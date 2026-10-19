using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Shared experience: host tells clients that a player started choosing a reward, so everyone pauses until it's done
    /// </summary>
    [MemoryPackable]
    public partial class EncounterPaused : IGameNetworkMessage
    {
    }
}
