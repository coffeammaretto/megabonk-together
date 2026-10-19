using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Shared experience: a client tells the host where it is in the reward flow (sent on change and periodically)
    /// </summary>
    [MemoryPackable]
    public partial class EncounterStateChanged : IGameNetworkMessage
    {
        public uint OwnerId { get; set; }
        public EncounterSyncState State { get; set; }

        /// <summary>
        /// Last CloseEncounter sequence received, so the host can ignore a "waiting" sent before its last close
        /// </summary>
        public uint CloseSeq { get; set; }

        /// <summary>
        /// The player pressed the unstuck key: release every waiting player
        /// </summary>
        public bool ForceClose { get; set; }
    }

    public enum EncounterSyncState : byte
    {
        Free = 0,
        Choosing = 1,
        Waiting = 2
    }
}
