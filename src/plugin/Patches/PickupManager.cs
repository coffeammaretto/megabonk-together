using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(PickupManager))]
    internal static class PickupManagerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IPickupManagerService pickupManagerService = Plugin.Services.GetService<IPickupManagerService>();

        /// <summary>
        /// Spawn pickups only on the server unless explicitly allowed
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PickupManager.SpawnPickup))]
        public static bool SpawnPickup_Prefix(ref bool useRandomOffsetPosition)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            useRandomOffsetPosition = false;

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                return true;
            }

            return Plugin.CAN_SPAWN_PICKUPS;
        }

        /// <summary>
        /// Synchronize spawned pickups to clients
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(PickupManager.SpawnPickup))]
        public static void SpawnPickup_Postfix(Pickup __result, EPickup ePickup, ref Vector3 pos, int value)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (__result == null)
            {
                return;
            }

            // Stacking returns an already tracked, possibly owned pickup: only reset new/recycled objects
            if (pickupManagerService.GetPickupByReference(__result).Value == null)
            {
                DynamicData.For(__result).Data.Clear();
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                synchronizationService.OnPickupSpawned(__result, ePickup, pos, value);
            }
        }

        /// <summary>
        /// The pool hands out a recycled object for a brand new pickup (stacking returns the existing one instead):
        /// any network id still attached to that object belongs to its previous life
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(PickupManager.SpawnPooledPickup))]
        public static void SpawnPooledPickup_Postfix(Pickup __result)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (!hasLoggedPoolHook)
            {
                hasLoggedPoolHook = true;
                Plugin.Log.LogInfo("Pickup pool hook active");
            }

            pickupManagerService.RemovePickupByReference(__result, false);
        }

        private static bool hasLoggedPoolHook = false;

        /// <summary>
        /// Use random player instead of only local player (Trigerred by magnet shrines and other ?). Server side only.
        /// </summary>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PickupManager.PickupAllXp))]
        public static bool PickupAllXp_Postfix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return false;
            }

            var xpPickups = pickupManagerService.GetAllPickupXp();
            var ownerByPickupObject = new Dictionary<int, uint>(); // Stacked ids share one object: give it a single owner
            foreach ((var pickupId, var pickup) in xpPickups)
            {
                if (ownerByPickupObject.TryGetValue(pickup.GetInstanceID(), out var stackOwnerId))
                {
                    synchronizationService.SendPickupFollowingPlayer(stackOwnerId, pickupId);
                    continue;
                }

                var dynPickup = DynamicData.For(pickup);

                var randomConnectionId = playerManagerService.GetRandomPlayerAliveConnectionId();
                if (randomConnectionId.HasValue)
                {
                    if (playerManagerService.IsLocalConnectionId(randomConnectionId.Value))
                    {
                        dynPickup.Set("ownerId", playerManagerService.GetLocalPlayer().ConnectionId);
                        pickup.StartFollowingPlayer(GameManager.Instance.player.transform);
                        synchronizationService.SendPickupFollowingPlayer(playerManagerService.GetLocalPlayer().ConnectionId, pickupId);
                        ownerByPickupObject[pickup.GetInstanceID()] = playerManagerService.GetLocalPlayer().ConnectionId;
                        continue;
                    }
                    else
                    {
                        var randomNetPlayer = playerManagerService.GetNetPlayerByNetplayId(randomConnectionId.Value);
                        if (randomNetPlayer != null)
                        {
                            dynPickup.Set("ownerId", randomNetPlayer.ConnectionId);
                            pickup.StartFollowingPlayer(randomNetPlayer.Model.transform);
                            synchronizationService.SendPickupFollowingPlayer(randomNetPlayer.ConnectionId, pickupId);
                            ownerByPickupObject[pickup.GetInstanceID()] = randomNetPlayer.ConnectionId;
                            continue;
                        }
                    }
                }
            }

            return false;
        }
    }
}
