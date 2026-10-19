using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using HarmonyLib;
using MegabonkTogether.Helpers;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using System.Reflection;

namespace MegabonkTogether.Patches.Items
{
    /// <summary>
    /// Kevin punches the player running the game. Every machine also has Kevin in its copy of the owner's inventory, so the
    /// whole party got punched. Only the owner's own Kevin reacts, and only to the owner's own hits.
    /// </summary>
    [HarmonyPatch(typeof(ItemKevin))]
    internal static class ItemKevinPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();

        public static bool Prepare(MethodBase original)
        {
            return original == null || NativeCodeGuard.IsSafeToPatch(original);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemKevin.OnEnemyDamaged))]
        public static bool OnEnemyDamaged_Prefix(ItemKevin __instance, Enemy enemy, DamageContainer dc)
        {
            return !synchronizationService.HasNetplaySessionStarted() || (IsLocalItem(__instance) && IsLocalHit(dc));
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemKevin.ProcOnHitEffects))]
        public static bool ProcOnHitEffects_Prefix(ItemKevin __instance, DamageContainer dc)
        {
            return !synchronizationService.HasNetplaySessionStarted() || (IsLocalItem(__instance) && IsLocalHit(dc));
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemKevin.CheckSelfDamage))]
        public static bool CheckSelfDamage_Prefix(ItemKevin __instance)
        {
            return !synchronizationService.HasNetplaySessionStarted() || IsLocalItem(__instance);
        }

        private static bool IsLocalItem(ItemBase item)
        {
            var localItems = GameManager.Instance?.player?.inventory?.itemInventory;
            return localItems != null && item.itemInventoryRef != null && item.itemInventoryRef.Pointer == localItems.Pointer;
        }

        /// <summary>
        /// The host applies every player's hits and clients replay them: whose hit is it
        /// </summary>
        private static bool IsLocalHit(DamageContainer dc)
        {
            var localPlayerId = playerManagerService.GetLocalPlayer()?.ConnectionId;
            var attackerId = trackerService.GetCurrentPlayerId();
            if (!attackerId.HasValue && dc != null)
            {
                attackerId = DynamicData.For(dc).Get<uint?>("ownerId");
            }

            return !attackerId.HasValue || attackerId == localPlayerId;
        }
    }
}
