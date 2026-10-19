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

        public PartySettingsService(IUdpClientService udpClientService)
        {
            this.udpClientService = udpClientService;
            hostSettings = ModConfig.ToPartySettings();

            EventManager.SubscribePartySettingsEvents(settings =>
            {
                receivedSettings = settings;
                Plugin.Log.LogInfo($"Party settings from host: pause {settings.RewardPauseMode}, stats {settings.PartyStatMode}, chest price +{settings.ChestPricePerExtraPlayer:P0}/player, enemy HP +{settings.EnemyHpPerExtraPlayer:P0}/player, enemies x{settings.EnemyAmountMultiplier}");
            });

            ModConfig.RewardPauseMode.ConfigFile.SettingChanged += OnConfigChanged;
        }

        private void OnConfigChanged(object sender, BepInEx.Configuration.SettingChangedEventArgs args)
        {
            hostSettings = ModConfig.ToPartySettings();
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
