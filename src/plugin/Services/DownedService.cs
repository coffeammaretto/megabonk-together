using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups;
using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using System;
using System.Linq;
using TMPro;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Downed state: lethal damage downs the local player instead of killing it (invulnerable, no attacks, slow) while it
    /// bleeds out. A teammate standing next to it revives it. Bleeding out, or nobody left standing to revive, is a real
    /// death (grave revive as before). The downed player's machine decides; others are told through PlayerDownedChanged.
    /// </summary>
    public interface IDownedService
    {
        public bool IsLocalDowned { get; }

        /// <summary>
        /// The local player is about to die: true if it got downed instead
        /// </summary>
        public bool TryGoDown(PlayerHealth health);

        public void Tick();
        public void ResetForNextLevel();
        public void Reset();
    }

    internal class DownedService : IDownedService
    {
        private const float REVIVER_HINT_DISTANCE = 30f;

        private readonly IUdpClientService udpClientService;
        private readonly IPlayerManagerService playerManagerService;
        private readonly IPartySettingsService partySettingsService;

        private bool allowNextDeath = false;
        private float bleedingFor = 0f;
        private float reviveProgress = 0f;
        private TextMeshProUGUI hudText;

        public bool IsLocalDowned { get; private set; } = false;

        public DownedService(IUdpClientService udpClientService, IPlayerManagerService playerManagerService, IPartySettingsService partySettingsService)
        {
            this.udpClientService = udpClientService;
            this.playerManagerService = playerManagerService;
            this.partySettingsService = partySettingsService;

            EventManager.SubscribePlayerDownedChangedEvents(OnReceivedPlayerDownedChanged);
        }

        public bool TryGoDown(PlayerHealth health)
        {
            if (allowNextDeath)
            {
                allowNextDeath = false;
                return false;
            }

            var localHealth = GameManager.Instance?.player?.inventory?.playerHealth;
            var localPlayer = playerManagerService.GetLocalPlayer();
            if (!partySettingsService.Current.DownedEnabled || localHealth == null || localPlayer == null || health.Pointer != localHealth.Pointer)
            {
                return false;
            }

            if (IsLocalDowned)
            {
                health.hp = Math.Max(health.hp, 1); //Already down: stays down
                return true;
            }

            if (!GetOtherStandingPlayerIds(localPlayer.ConnectionId).Any())
            {
                return false; //Nobody left to revive
            }

            IsLocalDowned = true;
            bleedingFor = 0f;
            reviveProgress = 0f;
            health.hp = Math.Max(health.hp, 1);

            SetDowned(localPlayer.ConnectionId, true);
            Plugin.Log.LogInfo("Downed: bleeding out, a teammate can revive");
            return true;
        }

        public void Tick()
        {
            var localPlayer = playerManagerService.GetLocalPlayer();
            var player = GameManager.Instance?.player;
            if (localPlayer == null || player == null || player.inventory?.playerHealth == null)
            {
                return;
            }

            try
            {
                if (IsLocalDowned)
                {
                    TickDowned(localPlayer.ConnectionId, player);
                }
                else
                {
                    TickReviverHint(localPlayer.ConnectionId, player.transform.position);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Downed tick failed: {ex}");
            }
        }

        private void TickDowned(uint localPlayerId, MyPlayer player)
        {
            var settings = partySettingsService.Current;
            var health = player.inventory.playerHealth;
            health.hp = Math.Max(health.hp, 1);

            // Game time: bleeding and reviving stop while the game is paused for a reward
            bleedingFor += Time.deltaTime;
            var isTeammateNear = GetOtherStandingPlayerIds(localPlayerId)
                .Any(id => Vector3.Distance(GetPosition(id), player.transform.position) <= settings.DownedReviveRadius);
            reviveProgress = isTeammateNear ? reviveProgress + Time.deltaTime : Math.Max(0f, reviveProgress - Time.deltaTime);

            if (reviveProgress >= settings.DownedReviveSeconds)
            {
                Revive(localPlayerId, health);
                return;
            }

            var remaining = settings.DownedBleedoutSeconds - bleedingFor;
            if (remaining <= 0f || !GetOtherStandingPlayerIds(localPlayerId).Any())
            {
                BleedOut(localPlayerId, health);
                return;
            }

            ShowHud(isTeammateNear
                ? $"DOWN! Reviving... {reviveProgress / settings.DownedReviveSeconds:P0}"
                : $"DOWN! {remaining:0}s left\nA teammate must stand next to you", Color.red);
        }

        private void TickReviverHint(uint localPlayerId, Vector3 localPosition)
        {
            var settings = partySettingsService.Current;
            var downedNearby = playerManagerService.GetAllPlayersExceptLocal()
                .Where(p => playerManagerService.IsDowned(p.ConnectionId))
                .Select(p => (p.Name, Distance: Vector3.Distance(GetPosition(p.ConnectionId), localPosition)))
                .Where(p => p.Distance <= REVIVER_HINT_DISTANCE)
                .OrderBy(p => p.Distance)
                .FirstOrDefault();

            if (downedNearby.Name == null)
            {
                HideHud();
                return;
            }

            ShowHud(downedNearby.Distance <= settings.DownedReviveRadius
                ? $"Reviving {downedNearby.Name}..."
                : $"{downedNearby.Name} is down! {downedNearby.Distance:0}m", new Color(1f, 0.6f, 0.2f));
        }

        private void Revive(uint localPlayerId, PlayerHealth health)
        {
            IsLocalDowned = false;
            health.hp = Math.Max(1, Mathf.CeilToInt(health.maxHp * partySettingsService.Current.DownedReviveHpFraction));
            SetDowned(localPlayerId, false);
            HideHud();
            Plugin.Log.LogInfo("Revived by a teammate");
        }

        private void BleedOut(uint localPlayerId, PlayerHealth health)
        {
            IsLocalDowned = false;
            SetDowned(localPlayerId, false);
            HideHud();
            Plugin.Log.LogInfo("Bled out");

            allowNextDeath = true;
            health.hp = 0;
            health.PlayerDied();
        }

        private void SetDowned(uint playerId, bool downed)
        {
            playerManagerService.SetDowned(playerId, downed);

            IGameNetworkMessage message = new PlayerDownedChanged
            {
                PlayerId = playerId,
                Downed = downed
            };

            if (udpClientService.IsHost() ?? false)
            {
                udpClientService.SendToAllClients(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
        }

        private void OnReceivedPlayerDownedChanged(PlayerDownedChanged message)
        {
            playerManagerService.SetDowned(message.PlayerId, message.Downed);
            Plugin.Log.LogInfo($"Player {message.PlayerId} {(message.Downed ? "is down" : "is back up")}");
        }

        /// <summary>
        /// Other players able to revive: alive and not downed
        /// </summary>
        private System.Collections.Generic.IEnumerable<uint> GetOtherStandingPlayerIds(uint localPlayerId)
        {
            return playerManagerService.GetAllPlayersAlive().Select(p => p.ConnectionId).Where(id => id != localPlayerId);
        }

        private Vector3 GetPosition(uint playerId)
        {
            if (playerManagerService.IsLocalConnectionId(playerId))
            {
                return GameManager.Instance.player.transform.position;
            }

            var netPlayer = playerManagerService.GetNetPlayerByNetplayId(playerId);
            return netPlayer?.Model != null ? netPlayer.Model.transform.position : new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        }

        private void ShowHud(string text, Color color)
        {
            if (hudText == null)
            {
                if (UiManager.Instance == null)
                {
                    return;
                }

                var go = new GameObject("MegabonkTogether_DownedHud");
                hudText = go.AddComponent<TextMeshProUGUI>();
                hudText.transform.SetParent(UiManager.Instance.transform, false);
                hudText.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
                hudText.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                hudText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                hudText.rectTransform.anchoredPosition = new Vector2(0f, 220f);
                hudText.rectTransform.sizeDelta = new Vector2(900f, 120f);
                hudText.alignment = TextAlignmentOptions.Center;
                hudText.fontSize = 38;
                hudText.fontStyle = FontStyles.Bold;
                hudText.outlineColor = Color.black;
                hudText.outlineWidth = 0.2f;
            }

            hudText.enabled = true;
            hudText.text = text;
            hudText.color = color;
        }

        private void HideHud()
        {
            if (hudText != null)
            {
                hudText.enabled = false;
            }
        }

        public void ResetForNextLevel()
        {
            var health = GameManager.Instance?.player?.inventory?.playerHealth;
            if (IsLocalDowned && health != null)
            {
                health.hp = Math.Max(1, Mathf.CeilToInt(health.maxHp * partySettingsService.Current.DownedReviveHpFraction));
            }

            IsLocalDowned = false;
            allowNextDeath = false;
            bleedingFor = 0f;
            reviveProgress = 0f;
            HideHud();
        }

        public void Reset()
        {
            ResetForNextLevel();
        }
    }
}
