using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using HarmonyLib;
using MegabonkTogether.Helpers;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MegabonkTogether.Patches.Items
{
    /// <summary>
    /// Items are written for a single player: they listen to static events (player hit, healed, evaded, enemy died...) and act
    /// on the player running the game. Every machine also holds a copy of the other players' inventories, so those copies
    /// reacted to the local player's events and affected the local player (e.g. someone's Mirror made everyone invulnerable,
    /// Kevin punched the whole party).
    /// </summary>
    internal static class ItemOwnership
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();

        public static bool IsNetplay => synchronizationService.HasNetplaySessionStarted();

        public static bool IsLocalItem(ItemBase item)
        {
            return OwnsItem(GameManager.Instance?.player?.inventory?.itemInventory, item);
        }

        /// <summary>
        /// Connection id of the player whose inventory holds this item
        /// </summary>
        public static uint? GetOwnerId(ItemBase item)
        {
            if (IsLocalItem(item))
            {
                return playerManagerService.GetLocalPlayer()?.ConnectionId;
            }

            foreach (var player in playerManagerService.GetAllPlayersExceptLocal())
            {
                var netPlayer = playerManagerService.GetNetPlayerByNetplayId(player.ConnectionId);
                if (OwnsItem(netPlayer?.Inventory?.itemInventory, item))
                {
                    return player.ConnectionId;
                }
            }

            return null;
        }

        /// <summary>
        /// Player whose hit or kill is being processed: the host applies everyone's hits and clients replay them
        /// </summary>
        public static uint? GetCurrentAttackerId()
        {
            return trackerService.GetCurrentPlayerId() ?? playerManagerService.GetLocalPlayer()?.ConnectionId;
        }

        /// <summary>
        /// Processing another player's actions (its weapons and items simulated by the host, or its hits replayed)
        /// </summary>
        public static bool IsInRemotePlayerContext()
        {
            var attackerId = trackerService.GetCurrentPlayerId();
            if (attackerId.HasValue && playerManagerService.IsRemoteConnectionId(attackerId.Value))
            {
                return true;
            }

            var simulatedPlayerId = playerManagerService.PeakNetplayerPositionRequest();
            return simulatedPlayerId.HasValue && playerManagerService.IsRemoteConnectionId(simulatedPlayerId.Value);
        }

        private static bool OwnsItem(ItemInventory inventory, ItemBase item)
        {
            if (inventory == null || item == null)
            {
                return false;
            }

            if (item.itemInventoryRef != null)
            {
                return item.itemInventoryRef.Pointer == inventory.Pointer;
            }

            foreach (var entry in inventory.items)
            {
                if (entry.Value != null && entry.Value.Pointer == item.Pointer)
                {
                    return true;
                }
            }

            return false;
        }

        public static IEnumerable<MethodBase> Safe(IEnumerable<MethodBase> methods)
        {
            return methods.Where(method => method != null && NativeCodeGuard.IsSafeToPatch(method));
        }
    }

    /// <summary>
    /// Reactions to the local player's own events (took damage, healed, evaded, jumped, opened a chest...): owner's copy only
    /// </summary>
    [HarmonyPatch]
    internal static class LocalPlayerEventItemPatches
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return ItemOwnership.Safe(
            [
                AccessTools.Method(typeof(ItemCactus), nameof(ItemCactus.OnTakeDamage)),
                AccessTools.Method(typeof(ItemSpeedBoi), nameof(ItemSpeedBoi.OnTakeDamage)),
                AccessTools.Method(typeof(ItemToxicBarrel), nameof(ItemToxicBarrel.OnTakeDamage)),
                AccessTools.Method(typeof(ItemCowardsCloak), nameof(ItemCowardsCloak.OnDamage)),
                AccessTools.Method(typeof(ItemGoldenShield), nameof(ItemGoldenShield.OnPlayerTakeDamage)),
                AccessTools.Method(typeof(ItemElectricPlug), nameof(ItemElectricPlug.OnPlayerHit)),
                AccessTools.Method(typeof(ItemHolyBook), nameof(ItemHolyBook.OnHeal)),
                AccessTools.Method(typeof(ItemMirror), nameof(ItemMirror.OnCheckStopDamage)),
                AccessTools.Method(typeof(ItemPhantomShroud), nameof(ItemPhantomShroud.OnEvade)),
                AccessTools.Method(typeof(ItemFlappyFeathers), nameof(ItemFlappyFeathers.OnJumped)),
                AccessTools.Method(typeof(ItemScarf), nameof(ItemScarf.OnGroundedChange)),
                AccessTools.Method(typeof(ItemCreditCardGreen), nameof(ItemCreditCardGreen.OnChestWindowOpen)),
                AccessTools.Method(typeof(ItemCreditCardRed), nameof(ItemCreditCardRed.OnChestWindowOpen)),
                AccessTools.Method(typeof(ItemGhost), nameof(ItemGhost.OnInteracted)),
                AccessTools.Method(typeof(ItemGasmask), nameof(ItemGasmask.OnStageStarted)),
                AccessTools.Method(typeof(ItemBloodyCleaver), nameof(ItemBloodyCleaver.OnLifestealProc)),
                AccessTools.Method(typeof(ItemKevin), nameof(ItemKevin.CheckSelfDamage)),
            ]);
        }

        public static bool Prefix(ItemBase __instance)
        {
            return !ItemOwnership.IsNetplay || ItemOwnership.IsLocalItem(__instance);
        }
    }

    /// <summary>
    /// Effects on the owner (heal, self damage, stats) for its own hits and kills: owner's copy only, owner's hits only
    /// </summary>
    [HarmonyPatch]
    internal static class OwnHitSelfEffectItemPatches
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return ItemOwnership.Safe(
            [
                AccessTools.Method(typeof(ItemDemonBlade), nameof(ItemDemonBlade.OnEnemyDamaged)),
                AccessTools.Method(typeof(ItemDemonicBlood), nameof(ItemDemonicBlood.OnEnemyDied)),
                AccessTools.Method(typeof(ItemDemonicSoul), nameof(ItemDemonicSoul.OnEnemyDied)),
                AccessTools.Method(typeof(ItemKevin), nameof(ItemKevin.OnEnemyDamaged)),
                AccessTools.Method(typeof(ItemKevin), nameof(ItemKevin.ProcOnHitEffects)),
            ]);
        }

        public static bool Prefix(ItemBase __instance)
        {
            if (!ItemOwnership.IsNetplay)
            {
                return true;
            }

            return ItemOwnership.IsLocalItem(__instance) && !ItemOwnership.IsInRemotePlayerContext();
        }
    }

    /// <summary>
    /// Effects in the world (spawns, executes) for the owner's hits and kills: any copy (the host simulates the clients'),
    /// but only for hits and kills by the item's owner
    /// </summary>
    [HarmonyPatch]
    internal static class OwnHitWorldEffectItemPatches
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return ItemOwnership.Safe(
            [
                AccessTools.Method(typeof(ItemBorgor), nameof(ItemBorgor.OnEnemyDied)),
                AccessTools.Method(typeof(ItemJoesDagger), nameof(ItemJoesDagger.OnEnemyDamage)),
            ]);
        }

        public static bool Prefix(ItemBase __instance)
        {
            if (!ItemOwnership.IsNetplay)
            {
                return true;
            }

            var ownerId = ItemOwnership.GetOwnerId(__instance);
            return ownerId.HasValue && ownerId == ItemOwnership.GetCurrentAttackerId();
        }
    }

    /// <summary>
    /// While processing another player's actions, the local player gets no heal (e.g. lifesteal on someone else's hit,
    /// healing items of a simulated client) and no gold (e.g. a simulated client's Golden Sneakers)
    /// </summary>
    [HarmonyPatch]
    internal static class RemotePlayerContextHealGuardPatches
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return ItemOwnership.Safe([AccessTools.Method(typeof(PlayerHealth), nameof(PlayerHealth.Heal))]);
        }

        public static bool Prefix(PlayerHealth __instance)
        {
            if (!ItemOwnership.IsNetplay || !ItemOwnership.IsInRemotePlayerContext())
            {
                return true;
            }

            var localHealth = GameManager.Instance?.player?.inventory?.playerHealth;
            return localHealth == null || __instance.Pointer != localHealth.Pointer;
        }
    }

    /// <summary>
    /// Count the gold the local player earns for the end of run team table
    /// </summary>
    [HarmonyPatch]
    internal static class LocalGoldEarnedPatches
    {
        private static readonly IRunStatsService runStatsService = Plugin.Services.GetService<IRunStatsService>();

        public static IEnumerable<MethodBase> TargetMethods()
        {
            return ItemOwnership.Safe([AccessTools.Method(typeof(PlayerInventory), nameof(PlayerInventory.ChangeGold))]);
        }

        public static void Postfix(PlayerInventory __instance, int amount)
        {
            if (amount <= 0 || !ItemOwnership.IsNetplay || ItemOwnership.IsInRemotePlayerContext()) //Blocked by the guard below
            {
                return;
            }

            var localInventory = GameManager.Instance?.player?.inventory;
            if (localInventory != null && __instance.Pointer == localInventory.Pointer)
            {
                runStatsService.OnLocalGoldEarned(amount);
            }
        }
    }

    [HarmonyPatch]
    internal static class RemotePlayerContextGoldGuardPatches
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return ItemOwnership.Safe([AccessTools.Method(typeof(PlayerInventory), nameof(PlayerInventory.ChangeGold))]);
        }

        public static bool Prefix(PlayerInventory __instance, int amount)
        {
            if (amount <= 0 || !ItemOwnership.IsNetplay || !ItemOwnership.IsInRemotePlayerContext())
            {
                return true;
            }

            var localInventory = GameManager.Instance?.player?.inventory;
            return localInventory == null || __instance.Pointer != localInventory.Pointer;
        }
    }
}
