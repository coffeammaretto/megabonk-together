using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using System;
using System.Linq;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Coop challenges, run by the host, rewarded with chests dropped from the sky (opened by everyone in shared experience):
    /// - no death on a stage (downs don't count): a bonus chest when the next stage starts
    /// - stage boss killed with everyone alive: a chest where the boss died
    /// </summary>
    public interface IChallengeService
    {
        public void OnStageStarted();
        public void OnPlayerDied();
        public void OnStageBossKilled(Vector3 position);
        public void OnStageEnded();
        public void Tick();
        public void Reset();
    }

    internal class ChallengeService : IChallengeService
    {
        private const float REWARD_CHEST_DELAY = 6f;

        private readonly IUdpClientService udpClientService;
        private readonly IPlayerManagerService playerManagerService;
        private readonly IPartySettingsService partySettingsService;

        private int stageDeaths = 0;
        private int pendingRewardChests = 0;
        private float spawnRewardsAt = float.MaxValue;
        private bool hasStageBossRewarded = false;

        public ChallengeService(IUdpClientService udpClientService, IPlayerManagerService playerManagerService, IPartySettingsService partySettingsService)
        {
            this.udpClientService = udpClientService;
            this.playerManagerService = playerManagerService;
            this.partySettingsService = partySettingsService;

            EventManager.SubscribeChallengeNoticeEvents(notice => ShowNotice(notice.Text));
        }

        private bool IsActive => (udpClientService.IsHost() ?? false) && partySettingsService.Current.ChallengesEnabled && playerManagerService.GetAllPlayers().Count() > 1;

        public void OnStageStarted()
        {
            stageDeaths = 0;
            hasStageBossRewarded = false;
            if (!IsActive)
            {
                return;
            }

            if (pendingRewardChests > 0)
            {
                spawnRewardsAt = Time.time + REWARD_CHEST_DELAY;
            }

            Announce("Challenges: no death this stage, kill the boss with everyone alive");
        }

        public void OnPlayerDied()
        {
            if (stageDeaths++ == 0 && IsActive)
            {
                Announce("Challenge failed: someone died this stage");
            }
        }

        public void OnStageBossKilled(Vector3 position)
        {
            if (!IsActive || hasStageBossRewarded)
            {
                return;
            }

            var everyoneAlive = playerManagerService.GetAllPlayers().All(p => !playerManagerService.IsDead(p.ConnectionId) && !playerManagerService.IsDowned(p.ConnectionId));
            if (!everyoneAlive)
            {
                return;
            }

            hasStageBossRewarded = true;
            SpawnRewardChest(position + Vector3.up * 2f);
            Announce("Challenge complete: boss killed with everyone alive, a chest dropped!");
        }

        public void OnStageEnded()
        {
            if (!IsActive || stageDeaths > 0)
            {
                return;
            }

            pendingRewardChests++;
            Announce("Challenge complete: no death this stage, a bonus chest awaits on the next one!");
        }

        public void Tick()
        {
            if (pendingRewardChests <= 0 || Time.time < spawnRewardsAt || !(udpClientService.IsHost() ?? false))
            {
                return;
            }

            var player = GameManager.Instance?.player;
            if (player == null)
            {
                return;
            }

            try
            {
                for (var i = 0; i < pendingRewardChests; i++)
                {
                    SpawnRewardChest(player.transform.position + new Vector3(3f + i * 2f, 2f, 3f));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Could not spawn challenge reward chests: {ex}");
            }

            pendingRewardChests = 0;
            spawnRewardsAt = float.MaxValue;
        }

        private static void SpawnRewardChest(Vector3 position)
        {
            // The host's chest spawns are synchronized to the clients by the Object.Instantiate patch
            EffectManager.Instance.SpawnChest(EffectManager.Instance.openChestNormal, position);
        }

        private void Announce(string text)
        {
            IGameNetworkMessage message = new ChallengeNotice { Text = text };
            udpClientService.SendToAllClients(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
            ShowNotice(text);
        }

        private static void ShowNotice(string text)
        {
            Plugin.Log.LogInfo(text);
            Plugin.StartNotification(("MegabonkTogether", "Challenge"), ("MegabonkTogether", "Challenge_Description"), [text]);
        }

        public void Reset()
        {
            stageDeaths = 0;
            pendingRewardChests = 0;
            spawnRewardsAt = float.MaxValue;
            hasStageBossRewarded = false;
        }
    }
}
