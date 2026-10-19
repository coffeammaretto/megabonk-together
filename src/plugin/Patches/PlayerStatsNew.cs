using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Menu.Shop;
using HarmonyLib;
using MegabonkTogether.Helpers;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace MegabonkTogether.Patches
{
    /// <summary>
    /// Use netplayer stats from his inventory when requested instead of local player stats
    /// </summary>
    [HarmonyPatch(typeof(PlayerStatsNew))]
    internal static class PlayerStatsNewPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IPartyStatsService partyStatsService = Plugin.Services.GetService<IPartyStatsService>();
        private static bool shouldIgnoreNextPatch = false;

        public static bool Prepare(MethodBase original)
        {
            return original == null || NativeCodeGuard.IsSafeToPatch(original);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(PlayerStatsNew.GetStat))]
        public static bool GetStat_Prefix(EStat stat, ref float __result)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (shouldIgnoreNextPatch)
            {
                shouldIgnoreNextPatch = false;
                return true;
            }

            if (partyStatsService.IsReadingOwnStat)
            {
                return true;
            }

            var peaked = playerManagerService.PeakNetplayerPositionRequest();
            if (peaked.HasValue && playerManagerService.IsRemoteConnectionId(peaked.Value))
            {
                // Stats sent by that player: its copy here lacks shrine buffs, passive growth, the party passives, etc.
                if (partyStatsService.TryGetRemoteStat(peaked.Value, stat, out var remoteStat))
                {
                    __result = remoteStat;
                    return false;
                }

                var netPlayer = playerManagerService.GetNetPlayerByNetplayId(peaked.Value);
                if (netPlayer == null)
                {
                    return true;
                }
                shouldIgnoreNextPatch = true;
                __result = netPlayer.Inventory.playerStats.GetStat(stat);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Luck and difficulty are the sum of every player's, for whoever asks
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(PlayerStatsNew.GetStat))]
        public static void GetStat_Postfix(PlayerStatsNew __instance, EStat stat, ref float __result)
        {
            if (stat != EStat.Luck && stat != EStat.Difficulty)
            {
                return;
            }

            if (partyStatsService.IsReadingOwnStat || !synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (partyStatsService.TryGetPartyStat(__instance, stat, out var partyStat))
            {
                __result = partyStat;
            }
        }

        /// <summary>
        /// Add the other players' luck and difficulty to the local player's, so the stats window shows the party value too
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(PlayerStatsNew.UpdateStat))]
        public static void UpdateStat_Postfix(PlayerStatsNew __instance, EStat stat)
        {
            if (stat != EStat.Luck && stat != EStat.Difficulty)
            {
                return;
            }

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            partyStatsService.OnLocalStatUpdated(__instance, stat);
        }
    }
}
