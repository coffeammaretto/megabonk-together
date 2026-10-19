using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Team table at the end of a run: the host counts every player's damage and kills (it applies all hits), each player
    /// counts its own gold earned and reports it to the host. On game over the host sends the table to everyone.
    /// </summary>
    public interface IRunStatsService
    {
        public void OnDamage(uint attackerId, float damage);
        public void OnKill(uint killerId);
        public void OnLocalGoldEarned(int amount);
        public void PublishTeamStats();
        public void Tick();
        public void Reset();
    }

    internal class RunStatsService : IRunStatsService
    {
        private const float REPORT_INTERVAL = 5f;

        private readonly IUdpClientService udpClientService;
        private readonly IPlayerManagerService playerManagerService;
        private readonly Dictionary<uint, float> damage = [];
        private readonly Dictionary<uint, int> kills = [];
        private readonly Dictionary<uint, int> goldEarned = [];
        private int localGoldEarned = 0;
        private float nextReportAt = 0f;
        private TextMeshProUGUI panel;

        public RunStatsService(IUdpClientService udpClientService, IPlayerManagerService playerManagerService)
        {
            this.udpClientService = udpClientService;
            this.playerManagerService = playerManagerService;

            EventManager.SubscribeRunStatsReportEvents(report => goldEarned[report.OwnerId] = report.GoldEarned);
            EventManager.SubscribeTeamStatsEvents(ShowPanel);
        }

        public void OnDamage(uint attackerId, float amount)
        {
            if (float.IsFinite(amount) && amount > 0f)
            {
                damage[attackerId] = damage.GetValueOrDefault(attackerId) + amount;
            }
        }

        public void OnKill(uint killerId)
        {
            kills[killerId] = kills.GetValueOrDefault(killerId) + 1;
        }

        public void OnLocalGoldEarned(int amount)
        {
            if (amount > 0)
            {
                localGoldEarned += amount;
            }
        }

        public void Tick()
        {
            var localPlayer = playerManagerService.GetLocalPlayer();
            if (localPlayer == null || IsHost() || Time.realtimeSinceStartup < nextReportAt)
            {
                return;
            }

            nextReportAt = Time.realtimeSinceStartup + REPORT_INTERVAL;
            IGameNetworkMessage message = new RunStatsReport
            {
                OwnerId = localPlayer.ConnectionId,
                GoldEarned = localGoldEarned
            };
            udpClientService.SendToHost(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        public void PublishTeamStats()
        {
            if (!IsHost())
            {
                return;
            }

            try
            {
                var localPlayer = playerManagerService.GetLocalPlayer();
                if (localPlayer != null)
                {
                    goldEarned[localPlayer.ConnectionId] = localGoldEarned;
                }

                var stats = new TeamStats
                {
                    Entries = playerManagerService.GetAllPlayers().Select(player => new TeamStatsEntry
                    {
                        Name = player.Name,
                        Damage = damage.GetValueOrDefault(player.ConnectionId),
                        Kills = kills.GetValueOrDefault(player.ConnectionId),
                        GoldEarned = goldEarned.GetValueOrDefault(player.ConnectionId),
                    }).ToList()
                };

                IGameNetworkMessage message = stats;
                udpClientService.SendToAllClients(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
                ShowPanel(stats);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Could not publish team stats: {ex}");
            }
        }

        private void ShowPanel(TeamStats stats)
        {
            var text = new StringBuilder("<b>TEAM</b>\n");
            foreach (var entry in stats.Entries.OrderByDescending(e => e.Damage))
            {
                text.Append($"{entry.Name}: {FormatNumber(entry.Damage)} damage, {entry.Kills} kills, {FormatNumber(entry.GoldEarned)} gold\n");
            }
            Plugin.Log.LogInfo(text.ToString().Replace("<b>", "").Replace("</b>", ""));

            if (panel == null)
            {
                if (UiManager.Instance == null)
                {
                    return;
                }

                var go = new GameObject("MegabonkTogether_TeamStats");
                panel = go.AddComponent<TextMeshProUGUI>();
                panel.transform.SetParent(UiManager.Instance.transform, false);
                panel.rectTransform.anchorMin = new Vector2(0f, 1f);
                panel.rectTransform.anchorMax = new Vector2(0f, 1f);
                panel.rectTransform.pivot = new Vector2(0f, 1f);
                panel.rectTransform.anchoredPosition = new Vector2(30f, -30f);
                panel.rectTransform.sizeDelta = new Vector2(900f, 300f);
                panel.alignment = TextAlignmentOptions.TopLeft;
                panel.fontSize = 28;
                panel.color = Color.white;
                panel.outlineColor = Color.black;
                panel.outlineWidth = 0.2f;
            }

            panel.text = text.ToString();
            panel.enabled = true;
            panel.transform.SetAsLastSibling();
        }

        private static string FormatNumber(float value)
        {
            return value >= 1_000_000f ? $"{value / 1_000_000f:0.#}M" : value >= 1_000f ? $"{value / 1_000f:0.#}k" : $"{value:0}";
        }

        private bool IsHost()
        {
            return udpClientService.IsHost() ?? false;
        }

        public void Reset()
        {
            damage.Clear();
            kills.Clear();
            goldEarned.Clear();
            localGoldEarned = 0;
            nextReportAt = 0f;
            if (panel != null)
            {
                panel.enabled = false;
            }
        }
    }
}
