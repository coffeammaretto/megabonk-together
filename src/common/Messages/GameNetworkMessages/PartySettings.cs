using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Party wide rules, decided by the host's config and sent to clients when a game starts or a setting changes
    /// </summary>
    [MemoryPackable]
    public partial class PartySettings : IGameNetworkMessage
    {
        // Chests
        public bool ScaleFreeChestsWithPlayers { get; set; } = false;
        public float ChestPricePerExtraPlayer { get; set; } = 0.25f;

        // Luck and difficulty shared across the party
        public PartyStatMode PartyStatMode { get; set; } = PartyStatMode.Sum;
        public float PartyLuckCap { get; set; } = 0f;
        public float PartyDifficultyCap { get; set; } = 0f;

        // Enemies
        public float EnemyHpPerExtraPlayer { get; set; } = 0.1f;
        public float EnemyAmountMultiplier { get; set; } = 1f;

        // Reward windows
        public RewardPauseMode RewardPauseMode { get; set; } = RewardPauseMode.Party;

        // Downed state
        public bool DownedEnabled { get; set; } = true;
        public float DownedBleedoutSeconds { get; set; } = 25f;
        public float DownedReviveSeconds { get; set; } = 3f;
        public float DownedReviveRadius { get; set; } = 4f;
        public float DownedReviveHpFraction { get; set; } = 0.3f;

        /// <summary>
        /// Character passives of the other players apply within this distance of their owner, 0 for everywhere
        /// </summary>
        public float SharedPassiveRadius { get; set; } = 0f;

        // Team meter
        public bool TeamMeterEnabled { get; set; } = true;
        public float TeamMeterKillsToFill { get; set; } = 250f;
        public float TeamMeterGrowth { get; set; } = 1.5f;
        public float TeamBuffSeconds { get; set; } = 10f;
    }

    public enum PartyStatMode : byte
    {
        Sum = 0,
        MaxPlusHalf = 1,
        Average = 2
    }

    public enum RewardPauseMode : byte
    {
        /// <summary>
        /// Everyone pauses while someone picks a reward
        /// </summary>
        Party = 0,

        /// <summary>
        /// Pick without pausing, invulnerable meanwhile; the others keep playing
        /// </summary>
        Own = 1
    }
}
