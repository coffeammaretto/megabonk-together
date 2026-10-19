using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Utility;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
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

    internal class PartyModifiersService(IUdpClientService udpClientService, IPartySettingsService partySettingsService, IDownedService downedService, ITeamMeterService teamMeterService) : IPartyModifiersService
    {
        private const float TICK_INTERVAL = 0.5f;
        private const string PREFIX = "MegabonkTogether_Party_";

        private readonly Dictionary<string, (EStat Stat, EStatModifyType Type, float Value)> applied = [];
        private IntPtr appliedInventory = IntPtr.Zero;
        private RunModifier announcedModifiers = RunModifier.None;
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

            AddRunModifiers(wanted, settings.RunModifiers, isHost);

            if (teamMeterService.IsBuffActive)
            {
                wanted["TeamBuffDamage"] = (EStat.DamageMultiplier, EStatModifyType.Addition, 0.25f);
                wanted["TeamBuffSpeed"] = (EStat.MoveSpeedMultiplier, EStatModifyType.Addition, 0.15f);
            }

            if (downedService.IsLocalDowned)
            {
                wanted["Downed"] = (EStat.MoveSpeedMultiplier, EStatModifyType.Addition, -0.6f);
            }

            return wanted;
        }

        /// <summary>
        /// Player stats on every machine, enemy stats (and difficulty, summed across the party) on the host only
        /// </summary>
        private void AddRunModifiers(Dictionary<string, (EStat, EStatModifyType, float)> wanted, RunModifier modifiers, bool isHost)
        {
            if (modifiers == RunModifier.None)
            {
                return;
            }

            if (modifiers != announcedModifiers)
            {
                announcedModifiers = modifiers;
                Plugin.StartNotification(("MegabonkTogether", "RunModifiers"), ("MegabonkTogether", "RunModifiers_Description"), [modifiers.ToString()]);
                Plugin.Log.LogInfo($"Run modifiers: {modifiers}");
            }

            if (modifiers.HasFlag(RunModifier.GlassCannon))
            {
                wanted["GlassCannonDamage"] = (EStat.DamageMultiplier, EStatModifyType.Addition, 1f);
                wanted["GlassCannonHp"] = (EStat.MaxHealth, EStatModifyType.Addition, -0.5f);
            }

            if (modifiers.HasFlag(RunModifier.Horde))
            {
                wanted["HordeXp"] = (EStat.XpIncreaseMultiplier, EStatModifyType.Addition, 0.25f);
                if (isHost)
                {
                    wanted["HordeEnemies"] = (EStat.EnemyAmountMultiplier, EStatModifyType.Addition, 0.5f);
                }
            }

            if (modifiers.HasFlag(RunModifier.ThickSkinned))
            {
                if (isHost) //Luck is combined across the party: added once, on the host
                {
                    wanted["ThickSkinnedLuck"] = (EStat.Luck, EStatModifyType.Flat, 0.25f);
                    wanted["ThickSkinnedEnemyHp"] = (EStat.EnemyHpMultiplier, EStatModifyType.Addition, 1f);
                }
            }

            if (modifiers.HasFlag(RunModifier.Frenzy) && isHost)
            {
                var steps = Mathf.FloorToInt(Math.Max(0f, MyTime.runTimer) / 300f);
                if (steps > 0)
                {
                    wanted["FrenzyEnemySpeed"] = (EStat.EnemySpeedMultiplier, EStatModifyType.Addition, 0.1f * steps);
                }
            }

            if (modifiers.HasFlag(RunModifier.Lucky))
            {
                if (isHost) //Luck and difficulty are combined across the party: added once, on the host
                {
                    wanted["LuckyLuck"] = (EStat.Luck, EStatModifyType.Flat, 0.5f);
                    wanted["LuckyDifficulty"] = (EStat.Difficulty, EStatModifyType.Flat, 0.3f);
                }
            }

            if (modifiers.HasFlag(RunModifier.Poverty))
            {
                wanted["PovertyGold"] = (EStat.GoldIncreaseMultiplier, EStatModifyType.Addition, -0.5f);
                wanted["PovertyXp"] = (EStat.XpIncreaseMultiplier, EStatModifyType.Addition, 0.25f);
            }
        }

        private void Apply(StatInventory statInventory, Dictionary<string, (EStat Stat, EStatModifyType Type, float Value)> wanted)
        {
            foreach (var (key, modifier) in wanted)
            {
                if (applied.TryGetValue(key, out var current) && current == modifier
                    && statInventory.movingStats != null && statInventory.movingStats.ContainsKey(PREFIX + key)) //The game may rebuild its moving stats
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
            announcedModifiers = RunModifier.None;
            nextTickAt = 0f;
        }
    }
}
