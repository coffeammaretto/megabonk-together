using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using MegabonkTogether.Configuration;
using System;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Party wide rules: the host's config, sent to clients when a game starts and whenever the host changes a setting
    /// </summary>
    public interface IPartySettingsService
    {
        public PartySettings Current { get; }
        public void SendToClients();
        public void Reset();
    }

    internal class PartySettingsService : IPartySettingsService
    {
        private readonly IUdpClientService udpClientService;
        private PartySettings hostSettings;
        private PartySettings receivedSettings;

        public PartySettings Current => IsHost() || receivedSettings == null ? hostSettings : receivedSettings;

        /// <summary>
        /// Keep values usable whatever is in a config (e.g. 0 seconds to revive would revive instantly)
        /// </summary>
        private static PartySettings Sanitize(PartySettings settings)
        {
            settings.ChestPricePerExtraPlayer = Clamp(settings.ChestPricePerExtraPlayer, 0f, 10f, 0.25f);
            settings.PartyLuckCap = Clamp(settings.PartyLuckCap, 0f, 1000f, 0f);
            settings.PartyDifficultyCap = Clamp(settings.PartyDifficultyCap, 0f, 1000f, 0f);
            settings.EnemyHpPerExtraPlayer = Clamp(settings.EnemyHpPerExtraPlayer, 0f, 10f, 0.1f);
            settings.EnemyAmountMultiplier = Clamp(settings.EnemyAmountMultiplier, 0.1f, 10f, 1f);
            settings.DownedBleedoutSeconds = Clamp(settings.DownedBleedoutSeconds, 3f, 300f, 25f);
            settings.DownedReviveSeconds = Clamp(settings.DownedReviveSeconds, 0.5f, 60f, 3f);
            settings.DownedReviveRadius = Clamp(settings.DownedReviveRadius, 1f, 50f, 4f);
            settings.DownedReviveHpFraction = Clamp(settings.DownedReviveHpFraction, 0.05f, 1f, 0.3f);
            settings.SharedPassiveRadius = Clamp(settings.SharedPassiveRadius, 0f, 1000f, 0f);
            settings.TeamMeterKillsToFill = Clamp(settings.TeamMeterKillsToFill, 10f, 100000f, 250f);
            settings.TeamMeterGrowth = Clamp(settings.TeamMeterGrowth, 1f, 10f, 1.5f);
            settings.TeamBuffSeconds = Clamp(settings.TeamBuffSeconds, 1f, 120f, 10f);
            return settings;
        }

        private static float Clamp(float value, float min, float max, float fallback)
        {
            return float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        }

        public PartySettingsService(IUdpClientService udpClientService)
        {
            this.udpClientService = udpClientService;
            hostSettings = Sanitize(ModConfig.ToPartySettings());

            EventManager.SubscribePartySettingsEvents(settings =>
            {
                receivedSettings = Sanitize(settings);
                Plugin.Log.LogInfo($"Party settings from host: pause {settings.RewardPauseMode}, stats {settings.PartyStatMode}, chest price +{settings.ChestPricePerExtraPlayer:P0}/player, enemy HP +{settings.EnemyHpPerExtraPlayer:P0}/player, enemies x{settings.EnemyAmountMultiplier}");
            });

            ModConfig.RewardPauseMode.ConfigFile.SettingChanged += OnConfigChanged;
        }

        private void OnConfigChanged(object sender, BepInEx.Configuration.SettingChangedEventArgs args)
        {
            hostSettings = Sanitize(ModConfig.ToPartySettings());
            if (IsHost())
            {
                SendToClients();
            }
        }

        public void SendToClients()
        {
            if (!IsHost())
            {
                return;
            }

            try
            {
                IGameNetworkMessage message = hostSettings;
                udpClientService.SendToAllClients(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not send party settings: {ex.Message}");
            }
        }

        private bool IsHost()
        {
            return udpClientService.IsHost() ?? false;
        }

        public void Reset()
        {
            receivedSettings = null;
        }
    }
}
