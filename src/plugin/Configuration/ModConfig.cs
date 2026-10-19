using BepInEx.Configuration;
using MegabonkTogether.Common.Messages.GameNetworkMessages;

namespace MegabonkTogether.Configuration
{
    public static class ModConfig
    {
        private static ConfigFile configFile;

        // DEV_URL is "ws://127.0.0.1:5000"

        public static ConfigEntry<string> PlayerName { get; private set; }
        public static ConfigEntry<bool> CheckForUpdates { get; private set; }
        public static ConfigEntry<string> ServerUrl { get; private set; }
        public static ConfigEntry<uint> RDVServerPort { get; private set; }
        public static ConfigEntry<bool> UseSteamNetworking { get; private set; }
        public static ConfigEntry<bool> ShowChangelog { get; private set; }
        public static ConfigEntry<string> PreviousVersion { get; private set; }
        public static ConfigEntry<bool> AllowSavesDuringNetplay { get; private set; }
        public static ConfigEntry<bool> EnabledSharedExperience { get; private set; }
        public static ConfigEntry<float> RewardWindowInputDelay { get; private set; }
        public static ConfigEntry<UnityEngine.KeyCode> PingKey { get; private set; }
        public static ConfigEntry<bool> ShowTeammatePicks { get; private set; }

        // Party rules: the host's values apply to everyone
        public static ConfigEntry<bool> ScaleFreeChestsWithPlayers { get; private set; }
        public static ConfigEntry<float> ChestPricePerExtraPlayer { get; private set; }
        public static ConfigEntry<PartyStatMode> PartyStatMode { get; private set; }
        public static ConfigEntry<float> PartyLuckCap { get; private set; }
        public static ConfigEntry<float> PartyDifficultyCap { get; private set; }
        public static ConfigEntry<float> EnemyHpPerExtraPlayer { get; private set; }
        public static ConfigEntry<float> EnemyAmountMultiplier { get; private set; }
        public static ConfigEntry<RewardPauseMode> RewardPauseMode { get; private set; }
        public static ConfigEntry<bool> DownedEnabled { get; private set; }
        public static ConfigEntry<float> DownedBleedoutSeconds { get; private set; }
        public static ConfigEntry<float> DownedReviveSeconds { get; private set; }
        public static ConfigEntry<float> DownedReviveRadius { get; private set; }
        public static ConfigEntry<float> DownedReviveHpFraction { get; private set; }
        public static ConfigEntry<float> SharedPassiveRadius { get; private set; }
        public static ConfigEntry<bool> TeamMeterEnabled { get; private set; }
        public static ConfigEntry<float> TeamMeterKillsToFill { get; private set; }
        public static ConfigEntry<float> TeamMeterGrowth { get; private set; }
        public static ConfigEntry<float> TeamBuffSeconds { get; private set; }
        public static ConfigEntry<RunModifier> RunModifiers { get; private set; }
        public static ConfigEntry<bool> ChallengesEnabled { get; private set; }

        public static void Initialize(ConfigFile config)
        {
            configFile = config;

            PlayerName = config.Bind(
                "Player",
                "PlayerName",
                "Player",
                "Your display name shown to other players. Please be respectful!"
            );
            CheckForUpdates = config.Bind(
                "Updates",
                "CheckForUpdates",
                true,
                "Check for updates on startup . Recommend leaving this enabled"
            );
            ServerUrl = config.Bind(
                "Network",
                "ServerUrl",
                "wss://megabonk-together-matchmaking.balatro-vs-matchmaking.eu",
                "The URL of the matchmaking server. Do not change this unless you know what you're doing (e.g. for self-hosting). Use ws://127.0.0.1:5000 on localhost for testing purpose"
            );
            RDVServerPort = config.Bind(
                "Network",
                "RDVServerPort",
                (uint)5678,
                "The port of the relay server. Do not change this unless you know what you're doing"
            );
            UseSteamNetworking = config.Bind(
                "Network",
                "UseSteamNetworking",
                true,
                "Friendlies rooms go through Steam (Steam lobby and Steam relays, no matchmaking server). Every player of a room must use the same setting. Random matchmaking always uses the server."
            );
            ShowChangelog = config.Bind(
                "Updates",
                "ShowChangelog",
                false,
                "Internal flag to show changelog after an update. Do not modify manually."
            );
            PreviousVersion = config.Bind(
                "Updates",
                "PreviousVersion",
                "",
                "Internal flag to store the previous version before an update. Do not modify manually."
            );
            AllowSavesDuringNetplay = config.Bind(
                "Gameplay",
                "AllowSavesDuringNetplay",
                false,
                "Allow game saves during netplay sessions."
            );
            EnabledSharedExperience = config.Bind(
                "Gameplay",
                "EnabledSharedExperience",
                false,
                "Enable Host experience (Same XP and pause enabled). Disable for no pause and separate XP."
            );
            RewardWindowInputDelay = config.Bind(
                "Gameplay",
                "RewardWindowInputDelay",
                1f,
                "Seconds during which clicks and presses are ignored after a level up, chest or other reward window appears in netplay, to prevent accidental picks. 0 to disable."
            );

            PingKey = config.Bind("Controls", "PingKey", UnityEngine.KeyCode.Mouse2,
                "Key to mark the spot you look at for the whole team (ping).");
            ShowTeammatePicks = config.Bind("Controls", "ShowTeammatePicks", true,
                "Show a notification when a teammate gets an item, weapon or tome, to coordinate builds.");

            const string party = "Party (host settings, apply to everyone)";
            ScaleFreeChestsWithPlayers = config.Bind(party, "ScaleFreeChestsWithPlayers", false,
                "Spawn more free chests with more players. Off by default with shared experience: every chest already gives an item to everyone.");
            ChestPricePerExtraPlayer = config.Bind(party, "ChestPricePerExtraPlayer", 0.25f,
                "Shared experience: chest price increase per player beyond the first (0.25 = +25%), since a bought chest gives an item to everyone.");
            PartyStatMode = config.Bind(party, "PartyStatMode", Common.Messages.GameNetworkMessages.PartyStatMode.Sum,
                "How luck and difficulty combine across the party: Sum, MaxPlusHalf (highest + half of the others) or Average.");
            PartyLuckCap = config.Bind(party, "PartyLuckCap", 0f,
                "Maximum party luck, 0 for no cap.");
            PartyDifficultyCap = config.Bind(party, "PartyDifficultyCap", 0f,
                "Maximum party difficulty, 0 for no cap.");
            EnemyHpPerExtraPlayer = config.Bind(party, "EnemyHpPerExtraPlayer", 0.1f,
                "Enemy HP increase per alive player beyond the first (0.1 = +10%).");
            EnemyAmountMultiplier = config.Bind(party, "EnemyAmountMultiplier", 1f,
                "Multiplier on the amount of enemies (1 = unchanged).");
            RewardPauseMode = config.Bind(party, "RewardPauseMode", Common.Messages.GameNetworkMessages.RewardPauseMode.Party,
                "Shared experience: Party = everyone pauses while someone picks a reward, Own = pick without pausing while invulnerable, the others keep playing.");
            DownedEnabled = config.Bind(party, "DownedEnabled", true,
                "Lethal damage downs a player instead of killing it: it bleeds out and a teammate standing next to it revives it.");
            DownedBleedoutSeconds = config.Bind(party, "DownedBleedoutSeconds", 25f,
                "Seconds a downed player has before dying.");
            DownedReviveSeconds = config.Bind(party, "DownedReviveSeconds", 3f,
                "Seconds a teammate must stay next to a downed player to revive it.");
            DownedReviveRadius = config.Bind(party, "DownedReviveRadius", 4f,
                "How close (meters) a teammate must be to revive a downed player.");
            DownedReviveHpFraction = config.Bind(party, "DownedReviveHpFraction", 0.3f,
                "Fraction of max HP a revived player gets back.");
            SharedPassiveRadius = config.Bind(party, "SharedPassiveRadius", 0f,
                "Character passives of the other players apply within this distance (meters) of their owner. 0 = everywhere.");
            TeamMeterEnabled = config.Bind(party, "TeamMeterEnabled", true,
                "Team meter: kills fill it while at least two players killed in the last seconds. Full = damage and speed buff for everyone.");
            TeamMeterKillsToFill = config.Bind(party, "TeamMeterKillsToFill", 250f,
                "Team kills needed to fill the team meter the first time.");
            TeamMeterGrowth = config.Bind(party, "TeamMeterGrowth", 1.5f,
                "The kills needed are multiplied by this after each team buff.");
            TeamBuffSeconds = config.Bind(party, "TeamBuffSeconds", 10f,
                "Team buff duration (+25% damage, +15% move speed).");
            RunModifiers = config.Bind(party, "RunModifiers", RunModifier.None,
                "Run modifiers, comma separated: GlassCannon (damage x2, max HP x0.5), Horde (enemies x1.5, XP +25%), ThickSkinned (enemy HP x2, luck +0.25), Frenzy (enemies 10% faster every 5 minutes), Lucky (luck +0.5, difficulty +0.3), Poverty (gold -50%, XP +25%). None for a normal run.");
            ChallengesEnabled = config.Bind(party, "ChallengesEnabled", true,
                "Coop challenges rewarded with chests: no death on a stage (bonus chest on the next one), stage boss killed with everyone alive (chest where it died).");
        }

        public static PartySettings ToPartySettings()
        {
            return new PartySettings
            {
                ScaleFreeChestsWithPlayers = ScaleFreeChestsWithPlayers.Value,
                ChestPricePerExtraPlayer = ChestPricePerExtraPlayer.Value,
                PartyStatMode = PartyStatMode.Value,
                PartyLuckCap = PartyLuckCap.Value,
                PartyDifficultyCap = PartyDifficultyCap.Value,
                EnemyHpPerExtraPlayer = EnemyHpPerExtraPlayer.Value,
                EnemyAmountMultiplier = EnemyAmountMultiplier.Value,
                RewardPauseMode = RewardPauseMode.Value,
                DownedEnabled = DownedEnabled.Value,
                DownedBleedoutSeconds = DownedBleedoutSeconds.Value,
                DownedReviveSeconds = DownedReviveSeconds.Value,
                DownedReviveRadius = DownedReviveRadius.Value,
                DownedReviveHpFraction = DownedReviveHpFraction.Value,
                SharedPassiveRadius = SharedPassiveRadius.Value,
                TeamMeterEnabled = TeamMeterEnabled.Value,
                TeamMeterKillsToFill = TeamMeterKillsToFill.Value,
                TeamMeterGrowth = TeamMeterGrowth.Value,
                TeamBuffSeconds = TeamBuffSeconds.Value,
                RunModifiers = RunModifiers.Value,
                ChallengesEnabled = ChallengesEnabled.Value,
            };
        }

        public static void Save()
        {
            configFile?.Save();
        }
    }
}
