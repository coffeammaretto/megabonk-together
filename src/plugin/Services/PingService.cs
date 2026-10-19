using Assets.Scripts.Camera;
using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using MegabonkTogether.Configuration;
using MegabonkTogether.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Pings: the ping key marks the spot the camera looks at, for everyone (light beam + minimap arrow), a few seconds
    /// </summary>
    public interface IPingService
    {
        public void Tick();
        public void Reset();
    }

    internal class PingService : IPingService
    {
        private const float PING_DURATION = 6f;
        private const float PING_COOLDOWN = 0.5f;
        private const float MAX_PING_DISTANCE = 400f;
        private const float FALLBACK_PING_DISTANCE = 30f;
        private const float BEAM_HEIGHT = 25f;

        private readonly IUdpClientService udpClientService;
        private readonly IPlayerManagerService playerManagerService;
        private readonly Dictionary<uint, (GameObject Marker, float ExpiresAt)> pings = [];
        private bool wasPingKeyDown = false;
        private float nextPingAllowedAt = 0f;
        private Material beamMaterial;

        public PingService(IUdpClientService udpClientService, IPlayerManagerService playerManagerService)
        {
            this.udpClientService = udpClientService;
            this.playerManagerService = playerManagerService;

            EventManager.SubscribePingPlacedEvents(ping => ShowPing(ping.OwnerId, ping.Position.ToUnityVector3()));
        }

        public void Tick()
        {
            try
            {
                RemoveExpiredPings();
                HandlePingKey();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Ping tick failed: {ex}");
            }
        }

        private void HandlePingKey()
        {
            var isDown = BepInEx.Unity.IL2CPP.UnityEngine.Input.GetKeyInt((BepInEx.Unity.IL2CPP.UnityEngine.KeyCode)(int)ModConfig.PingKey.Value);
            var isPressed = isDown && !wasPingKeyDown;
            wasPingKeyDown = isDown;

            var localPlayer = playerManagerService.GetLocalPlayer();
            if (!isPressed || Time.realtimeSinceStartup < nextPingAllowedAt || localPlayer == null)
            {
                return;
            }

            var camera = GameManager.Instance?.playerCamera?.camera;
            var player = GameManager.Instance?.player;
            if (camera == null || player == null)
            {
                return;
            }

            nextPingAllowedAt = Time.realtimeSinceStartup + PING_COOLDOWN;

            var position = GetLookedAtPoint(camera.transform, player.transform);
            IGameNetworkMessage message = new PingPlaced
            {
                OwnerId = localPlayer.ConnectionId,
                Position = position.ToNumericsVector3()
            };

            if (udpClientService.IsHost() ?? false)
            {
                udpClientService.SendToAllClients(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }

            ShowPing(localPlayer.ConnectionId, position);
        }

        private static Vector3 GetLookedAtPoint(Transform camera, Transform player)
        {
            var ray = new Ray(camera.position, camera.forward);
            var hits = Physics.RaycastAll(ray, MAX_PING_DISTANCE, ~0, QueryTriggerInteraction.Ignore);
            var closest = hits
                .Where(hit => hit.collider != null && !hit.collider.transform.IsChildOf(player))
                .OrderBy(hit => hit.distance)
                .Select(hit => (Vector3?)hit.point)
                .FirstOrDefault();

            return closest ?? camera.position + camera.forward * FALLBACK_PING_DISTANCE;
        }

        private void ShowPing(uint ownerId, Vector3 position)
        {
            RemovePing(ownerId); //One ping per player

            var color = GetPlayerColor(ownerId);
            var marker = new GameObject($"MegabonkTogether_Ping_{ownerId}");
            marker.transform.position = position;

            var beam = marker.AddComponent<LineRenderer>();
            beam.useWorldSpace = true;
            beam.positionCount = 2;
            beam.SetPosition(0, position);
            beam.SetPosition(1, position + Vector3.up * BEAM_HEIGHT);
            beam.startWidth = 0.6f;
            beam.endWidth = 0.1f;
            var material = GetBeamMaterial();
            if (material != null)
            {
                beam.material = material;
            }
            beam.startColor = color;
            beam.endColor = new Color(color.r, color.g, color.b, 0.1f);

            GetMinimap()?.AddArrow(marker.transform, color);
            pings[ownerId] = (marker, Time.realtimeSinceStartup + PING_DURATION);
        }

        private Color GetPlayerColor(uint playerId)
        {
            if (playerManagerService.IsLocalConnectionId(playerId))
            {
                return new Color(0.3f, 1f, 1f);
            }

            try
            {
                return Plugin.Instance.NetPlayersDisplayer.GetPlayerColor(playerId);
            }
            catch
            {
                return Color.yellow;
            }
        }

        private Material GetBeamMaterial()
        {
            if (beamMaterial == null)
            {
                var shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
                if (shader == null)
                {
                    Plugin.Log.LogWarning("No shader found for ping beams");
                    return null;
                }
                beamMaterial = new Material(shader);
            }

            return beamMaterial;
        }

        private static MinimapCamera GetMinimap()
        {
            return GameManager.Instance?.player?.minimapCamera?.GetComponent<MinimapCamera>();
        }

        private void RemoveExpiredPings()
        {
            var now = Time.realtimeSinceStartup;
            foreach (var ownerId in pings.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToList())
            {
                RemovePing(ownerId);
            }
        }

        private void RemovePing(uint ownerId)
        {
            if (!pings.TryGetValue(ownerId, out var ping))
            {
                return;
            }

            pings.Remove(ownerId);
            if (ping.Marker != null)
            {
                GetMinimap()?.RemoveArrow(ping.Marker.transform);
                UnityEngine.Object.Destroy(ping.Marker);
            }
        }

        public void Reset()
        {
            foreach (var ownerId in pings.Keys.ToList())
            {
                RemovePing(ownerId);
            }
        }
    }
}
