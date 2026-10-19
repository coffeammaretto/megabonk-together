using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Menu.Shop;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Stat modifiers the mod adds to the local player for party rules (enemy amount, run modifiers, team buff...).
    /// Kept as named moving stats, refreshed periodically: added, updated or removed when the wanted set changes.
    /// Enemy related stats only matter on the host, which spawns the enemies.
    /// </summary>
    public interface IPartyModifiersService
    {
        public void Tick();
        public void Reset();
    }

    internal class PartyModifiersService(IUdpClientService udpClientService, IPartySettingsService partySettingsService) : IPartyModifiersService
    {
        private const float TICK_INTERVAL = 0.5f;
        private const string PREFIX = "MegabonkTogether_Party_";

        private readonly Dictionary<string, (EStat Stat, EStatModifyType Type, float Value)> applied = [];
        private IntPtr appliedInventory = IntPtr.Zero;
        private float nextTickAt = 0f;

        public void Tick()
        {
            var now = Time.realtimeSinceStartup;
            if (now < nextTickAt)
            {
                return;
            }
            nextTickAt = now + TICK_INTERVAL;

            var inventory = GameManager.Instance?.player?.inventory;
            if (inventory == null || inventory.statInventory == null)
            {
                return;
            }

            if (inventory.Pointer != appliedInventory) //New run: nothing applied on this inventory yet
            {
                applied.Clear();
                appliedInventory = inventory.Pointer;
            }

            try
            {
                Apply(inventory.statInventory, GetWantedModifiers());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Party modifiers tick failed: {ex}");
            }
        }

        private Dictionary<string, (EStat Stat, EStatModifyType Type, float Value)> GetWantedModifiers()
        {
            var wanted = new Dictionary<string, (EStat, EStatModifyType, float)>();
            var settings = partySettingsService.Current;
            var isHost = udpClientService.IsHost() ?? false;

            if (isHost && Mathf.Abs(settings.EnemyAmountMultiplier - 1f) > 0.001f)
            {
                wanted["EnemyAmount"] = (EStat.EnemyAmountMultiplier, EStatModifyType.Addition, settings.EnemyAmountMultiplier - 1f);
            }

            return wanted;
        }

        private void Apply(StatInventory statInventory, Dictionary<string, (EStat Stat, EStatModifyType Type, float Value)> wanted)
        {
            foreach (var (key, modifier) in wanted)
            {
                if (applied.TryGetValue(key, out var current) && current == modifier)
                {
                    continue;
                }

                statInventory.ChangeMovingStat(PREFIX + key, new StatModifier
                {
                    stat = modifier.Stat,
                    modifyType = modifier.Type,
                    modification = modifier.Value
                });
                applied[key] = modifier;
                Plugin.Log.LogInfo($"Party modifier {key}: {modifier.Stat} {modifier.Type} {modifier.Value}");
            }

            foreach (var key in applied.Keys.Where(key => !wanted.ContainsKey(key)).ToList())
            {
                statInventory.RemoveMovingStat(PREFIX + key);
                applied.Remove(key);
                Plugin.Log.LogInfo($"Party modifier {key} removed");
            }
        }

        public void Reset()
        {
            applied.Clear();
            appliedInventory = IntPtr.Zero;
            nextTickAt = 0f;
        }
    }
}
