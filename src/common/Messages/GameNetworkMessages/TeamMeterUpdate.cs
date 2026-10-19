using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Host's team meter: fill progress and remaining team buff time
    /// </summary>
    [MemoryPackable]
    public partial class TeamMeterUpdate : IGameNetworkMessage
    {
        public float Progress { get; set; }
        public float BuffSecondsLeft { get; set; }
    }
}
