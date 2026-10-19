using Assets.Scripts.Inventory__Items__Pickups;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(PlayerHealth))]
    internal static class PlayerHealthPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IDownedService downedService = Plugin.Services.GetService<IDownedService>();

        public static bool Prepare(System.Reflection.MethodBase original)
        {
            return original == null || Helpers.NativeCodeGuard.IsSafeToPatch(original);
        }

        /// <summary>
        /// Get downed instead of dying when a teammate can still revive
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PlayerHealth.PlayerDied))]
        public static bool PlayerDied_Prefix(PlayerHealth __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            return !downedService.TryGoDown(__instance);
        }

        /// <summary>
        /// Downed players are invulnerable: bleeding out is the threat
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PlayerHealth.Damage))]
        public static bool Damage_Prefix(PlayerHealth __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted() || !downedService.IsLocalDowned)
            {
                return true;
            }

            var localHealth = GameManager.Instance?.player?.inventory?.playerHealth;
            return localHealth == null || __instance.Pointer != localHealth.Pointer;
        }

        /// <summary>
        /// Synchronize player death event
        /// </summary>
        /// <param name="__instance"></param>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(PlayerHealth.PlayerDied))]
        public static void PlayerDied_Postfix(PlayerHealth __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (downedService.IsLocalDowned) //Got downed instead (the prefix skipped the death)
            {
                return;
            }

            synchronizationService.OnPlayerDied();
        }

        /// <summary>
        /// Prevent remote inventory tick
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PlayerHealth.Tick))]
        public static bool Tick_Prefix(PlayerHealth __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isRemotePlayer = playerManagerService.IsRemotePlayerHealth(__instance);
            if (isRemotePlayer)
            {
                return false;
            }

            return true;
        }
    }
}
