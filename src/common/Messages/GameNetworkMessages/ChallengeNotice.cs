using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Host announces a coop challenge or its outcome, shown to every player
    /// </summary>
    [MemoryPackable]
    public partial class ChallengeNotice : IGameNetworkMessage
    {
        public string Text { get; set; }
    }
}
