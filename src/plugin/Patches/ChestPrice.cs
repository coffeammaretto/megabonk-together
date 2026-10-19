using Assets.Scripts.Inventory__Items__Pickups.Chests;
using HarmonyLib;
using MegabonkTogether.Helpers;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(InteractableChest))]
    internal static class ChestPricePatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IPartySettingsService partySettingsService = Plugin.Services.GetService<IPartySettingsService>();

        public static bool Prepare(MethodBase original)
        {
            return original == null || NativeCodeGuard.IsSafeToPatch(original);
        }

        /// <summary>
        /// Shared experience: a bought chest gives an item to everyone, so it costs more with more players
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(InteractableChest.GetPrice))]
        public static void GetPrice_Postfix(ref int __result)
        {
            if (__result <= 0 || !synchronizationService.HasNetplaySessionStarted() || !synchronizationService.IsSharedExperienceEnabled())
            {
                return;
            }

            var extraPlayers = playerManagerService.GetAllPlayers().Count() - 1;
            var increasePerPlayer = partySettingsService.Current.ChestPricePerExtraPlayer;
            if (extraPlayers <= 0 || increasePerPlayer <= 0f)
            {
                return;
            }

            __result = Mathf.RoundToInt(__result * (1f + increasePerPlayer * extraPlayers));
        }
    }
}
