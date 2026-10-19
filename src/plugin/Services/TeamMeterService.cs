using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Team meter, run by the host: a kill fills it while at least two different players killed recently, so it rewards
    /// playing together. Full: a short damage and speed buff for everyone, and the next fill needs more kills.
    /// </summary>
    public interface ITeamMeterService
    {
        public bool IsBuffActive { get; }
        public void OnEnemyKilled(uint? killerId);
        public void Tick();
        public void ResetForNextLevel();
        public void Reset();
    }

    internal class TeamMeterService : ITeamMeterService
    {
        private const float TEAMWORK_WINDOW = 5f;
        private const float SEND_INTERVAL = 0.5f;

        private readonly IUdpClientService udpClientService;
        private readonly IPlayerManagerService playerManagerService;
        private readonly IPartySettingsService partySettingsService;
        private readonly Queue<(float Time, uint KillerId)> recentKills = new();

        private float points = 0f;
        private int activations = 0;
        private float progress = 0f;
        private float buffSecondsLeft = 0f;
        private float nextSendAt = 0f;
        private (float Progress, float Buff) lastSent = (-1f, -1f);
        private TextMeshProUGUI hudText;

        public bool IsBuffActive => buffSecondsLeft > 0f;

        public TeamMeterService(IUdpClientService udpClientService, IPlayerManagerService playerManagerService, IPartySettingsService partySettingsService)
        {
            this.udpClientService = udpClientService;
            this.playerManagerService = playerManagerService;
            this.partySettingsService = partySettingsService;

            EventManager.SubscribeTeamMeterUpdateEvents(update =>
            {
                progress = update.Progress;
                buffSecondsLeft = update.BuffSecondsLeft;
            });
        }

        public void OnEnemyKilled(uint? killerId)
        {
            var settings = partySettingsService.Current;
            if (!IsHost() || !settings.TeamMeterEnabled || !killerId.HasValue || IsBuffActive)
            {
                return;
            }

            var now = Time.time;
            recentKills.Enqueue((now, killerId.Value));
            while (recentKills.Count > 0 && now - recentKills.Peek().Time > TEAMWORK_WINDOW)
            {
                recentKills.Dequeue();
            }

            if (recentKills.Select(kill => kill.KillerId).Distinct().Count() < 2)
            {
                return; //Only one player killing: no teamwork
            }

            points += 1f;
            var required = Math.Max(1f, settings.TeamMeterKillsToFill) * Mathf.Pow(Math.Max(1f, settings.TeamMeterGrowth), activations);
            progress = Mathf.Clamp01(points / required);

            if (points >= required)
            {
                points = 0f;
                progress = 0f;
                activations++;
                buffSecondsLeft = settings.TeamBuffSeconds;
                recentKills.Clear();
                Plugin.Log.LogInfo($"Team meter full: team buff for {settings.TeamBuffSeconds}s (next fill x{Mathf.Pow(settings.TeamMeterGrowth, activations):0.##})");
                SendUpdate(force: true);
            }
        }

        public void Tick()
        {
            if (buffSecondsLeft > 0f)
            {
                buffSecondsLeft = Math.Max(0f, buffSecondsLeft - Time.deltaTime); //Game time: stops with the reward pause
            }

            var settings = partySettingsService.Current;
            var isActive = settings.TeamMeterEnabled && playerManagerService.GetAllPlayers().Count() > 1;
            if (!isActive)
            {
                HideHud();
                return;
            }

            try
            {

                if (IsHost())
                {
                    SendUpdate(force: false);
                }

                ShowHud();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Team meter tick failed: {ex}");
            }
        }

        private void SendUpdate(bool force)
        {
            var now = Time.realtimeSinceStartup;
            var current = (Progress: (float)Math.Round(progress, 2), Buff: (float)Math.Ceiling(buffSecondsLeft));
            if (!force && (now < nextSendAt || current == lastSent))
            {
                return;
            }

            nextSendAt = now + SEND_INTERVAL;
            lastSent = current;

            IGameNetworkMessage message = new TeamMeterUpdate
            {
                Progress = progress,
                BuffSecondsLeft = buffSecondsLeft
            };
            udpClientService.SendToAllClients(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        private void ShowHud()
        {
            if (hudText == null)
            {
                if (UiManager.Instance == null)
                {
                    return;
                }

                var go = new GameObject("MegabonkTogether_TeamMeterHud");
                hudText = go.AddComponent<TextMeshProUGUI>();
                hudText.transform.SetParent(UiManager.Instance.transform, false);
                hudText.rectTransform.anchorMin = new Vector2(0.5f, 1f);
                hudText.rectTransform.anchorMax = new Vector2(0.5f, 1f);
                hudText.rectTransform.pivot = new Vector2(0.5f, 1f);
                hudText.rectTransform.anchoredPosition = new Vector2(0f, -12f);
                hudText.rectTransform.sizeDelta = new Vector2(600f, 40f);
                hudText.alignment = TextAlignmentOptions.Center;
                hudText.fontSize = 24;
                hudText.fontStyle = FontStyles.Bold;
                hudText.outlineColor = Color.black;
                hudText.outlineWidth = 0.2f;
            }

            hudText.enabled = true;
            if (IsBuffActive)
            {
                hudText.text = $"TEAM BUFF! {buffSecondsLeft:0}s";
                hudText.color = new Color(1f, 0.85f, 0.2f);
            }
            else
            {
                const int segments = 20;
                var filled = Mathf.RoundToInt(progress * segments);
                hudText.text = $"TEAM [{new string('|', filled)}{new string('.', segments - filled)}]";
                hudText.color = Color.white;
            }
        }

        private void HideHud()
        {
            if (hudText != null)
            {
                hudText.enabled = false;
            }
        }

        private bool IsHost()
        {
            return udpClientService.IsHost() ?? false;
        }

        public void ResetForNextLevel()
        {
            recentKills.Clear();
            buffSecondsLeft = 0f;
        }

        public void Reset()
        {
            ResetForNextLevel();
            points = 0f;
            activations = 0;
            progress = 0f;
            lastSent = (-1f, -1f);
            HideHud();
        }
    }
}
