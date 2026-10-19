using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Menu.Shop;
using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Party wide stats:
    /// - every player's character passive bonuses (grown by its owner as usual) apply to the whole party, duplicates stack
    /// - luck and difficulty are the sum of every player's
    /// Each player sends its own final stats and passive bonuses. They're also what the other machines use for that player's
    /// copy (e.g. the host simulating a client's weapons), which otherwise lacks shrine buffs, passive growth, etc.
    /// </summary>
    public interface IPartyStatsService
    {
        /// <summary>
        /// Set while reading a vanilla value, so the stat patches leave it alone
        /// </summary>
        public bool IsReadingOwnStat { get; }

        public void Tick();
        public void OnLocalStatUpdated(PlayerStatsNew playerStats, EStat stat);
        public bool TryGetPartyStat(PlayerStatsNew playerStats, EStat stat, out float value);
        public bool TryGetRemoteStat(uint playerId, EStat stat, out float value);
        public void Reset();
    }

    internal class PartyStatsService : IPartyStatsService
    {
        private const float TICK_INTERVAL = 0.25f;
        private const float MIN_SEND_INTERVAL = 0.5f;
        private const float HEARTBEAT_INTERVAL = 5f;
        private const float EPSILON = 0.0001f;
        private const string PASSIVE_MOVING_STAT_PREFIX = "MegabonkTogether_PartyPassive";

        private const int MAX_STAT_READ_FAILURES = 5;

        private static readonly EStat[] SummedStats = [EStat.Luck, EStat.Difficulty];
        private static readonly EStat[] DefinedStats = Enum.GetValues(typeof(EStat)).Cast<EStat>().ToArray();
        private static readonly int StatCount = DefinedStats.Max(stat => (int)stat) + 1;

        private readonly IUdpClientService udpClientService;
        private readonly IPlayerManagerService playerManagerService;
        private readonly IPartySettingsService partySettingsService;

        private readonly Dictionary<uint, float[]> remoteStats = [];
        private readonly Dictionary<uint, Dictionary<string, StatModifierModel>> appliedRemotePassives = [];
        private readonly Dictionary<uint, List<StatModifierModel>> remotePassives = [];
        private readonly Dictionary<EStat, float[]> remoteValues = [];
        private (PartyStatMode Mode, float LuckCap, float DifficultyCap) currentRules;
        private readonly Dictionary<EStat, float> ownValues = [];
        private readonly Dictionary<EStat, float> partyTotals = [];
        private readonly Dictionary<EStat, float> lastWrittenTotals = [];
        private readonly Dictionary<EStat, int> statReadFailures = [];
        private readonly Dictionary<EStat, float> lastLoggedTotals = [];

        private IntPtr localStatsPointer = IntPtr.Zero;
        private int readingOwnStatDepth = 0;
        private float nextTickAt = 0f;
        private float lastSentAt = float.MinValue;
        private float[] lastSentStats = null;
        private List<StatModifierModel> lastSentPassives = null;

        public bool IsReadingOwnStat => readingOwnStatDepth > 0;

        public PartyStatsService(IUdpClientService udpClientService, IPlayerManagerService playerManagerService, IPartySettingsService partySettingsService)
        {
            this.udpClientService = udpClientService;
            this.playerManagerService = playerManagerService;
            this.partySettingsService = partySettingsService;

            EventManager.SubscribePlayerStatsChangedEvents(OnReceivedPlayerStats);
        }

        public void Tick()
        {
            var now = Time.realtimeSinceStartup;
            if (now < nextTickAt)
            {
                return;
            }
            nextTickAt = now + TICK_INTERVAL;

            try
            {
                var localPlayer = playerManagerService.GetLocalPlayer();
                var localInventory = GameManager.Instance?.player?.inventory;
                if (localPlayer == null || localInventory == null || localInventory.playerStats == null)
                {
                    return;
                }

                if (localInventory.playerStats.Pointer != localStatsPointer)
                {
                    OnNewLocalStats(localInventory);
                }

                ForgetDisconnectedPlayers(localPlayer.ConnectionId, localInventory);
                foreach (var ownerId in remotePassives.Keys.ToList())
                {
                    ApplyRemotePassivesInRange(localInventory, ownerId); //Owners move in and out of the radius
                }
                RefreshRemoteSums(localPlayer.ConnectionId, localInventory); //Also catches party rule changes
                SendOwnStatsIfChanged(localPlayer.ConnectionId, localInventory, now);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Party stats tick failed: {ex}");
            }
        }

        /// <summary>
        /// New run or new character: values of the previous stats don't apply, and the shared passives must be applied again
        /// </summary>
        private void OnNewLocalStats(PlayerInventory localInventory)
        {
            localStatsPointer = localInventory.playerStats.Pointer;
            ownValues.Clear();
            partyTotals.Clear();
            lastWrittenTotals.Clear();
            statReadFailures.Clear();
            appliedRemotePassives.Clear();

            foreach (var stat in SummedStats)
            {
                localInventory.playerStats.QueueUpdateStat(stat);
            }
        }

        /// <summary>
        /// The game just recomputed a stat of the local player: remember its own value, then add the other players' share
        /// </summary>
        public void OnLocalStatUpdated(PlayerStatsNew playerStats, EStat stat)
        {
            if (!SummedStats.Contains(stat) || playerStats.Pointer != localStatsPointer)
            {
                return;
            }

            var ownValue = ReadOwnStat(playerStats, stat);
            if (lastWrittenTotals.TryGetValue(stat, out var lastWritten) && Mathf.Abs(ownValue - lastWritten) < EPSILON && ownValues.TryGetValue(stat, out var previousOwn))
            {
                ownValue = previousOwn; //Not recomputed, still the value written below: don't add the share twice
            }

            ownValues[stat] = ownValue;
            var total = Combine(stat, ownValue, remoteValues.GetValueOrDefault(stat) ?? []);
            partyTotals[stat] = total;

            if (playerStats.stats != null && playerStats.stats.ContainsKey(stat))
            {
                playerStats.stats[stat] = total;
                lastWrittenTotals[stat] = total;
            }

            if (!lastLoggedTotals.TryGetValue(stat, out var lastLogged) || Mathf.Abs(lastLogged - total) > EPSILON)
            {
                lastLoggedTotals[stat] = total;
                Plugin.Log.LogInfo($"Party {stat}: own {ownValue}, others [{string.Join(", ", remoteValues.GetValueOrDefault(stat) ?? [])}], {currentRules.Mode} = {total} (GetStat now {playerStats.GetStat(stat)})");
            }
        }

        public bool TryGetPartyStat(PlayerStatsNew playerStats, EStat stat, out float value)
        {
            value = 0f;
            if (playerStats.Pointer != localStatsPointer || !partyTotals.TryGetValue(stat, out value))
            {
                return false;
            }

            return (remoteValues.GetValueOrDefault(stat)?.Length ?? 0) > 0 || GetCap(stat) > 0f;
        }

        /// <summary>
        /// Party value of a summed stat from the local player's own value and the other players' ones
        /// </summary>
        private float Combine(EStat stat, float own, float[] others)
        {
            var all = others.Prepend(own).ToList();
            var sum = all.Sum();
            var max = all.Max();

            var total = currentRules.Mode switch
            {
                PartyStatMode.MaxPlusHalf => max + 0.5f * (sum - max),
                PartyStatMode.Average => sum / all.Count,
                _ => sum,
            };

            var cap = GetCap(stat);
            return cap > 0f ? Mathf.Min(total, cap) : total;
        }

        private float GetCap(EStat stat)
        {
            return stat == EStat.Luck ? currentRules.LuckCap : currentRules.DifficultyCap;
        }

        public bool TryGetRemoteStat(uint playerId, EStat stat, out float value)
        {
            value = 0f;
            if (!remoteStats.TryGetValue(playerId, out var stats) || (int)stat >= stats.Length)
            {
                return false;
            }

            value = stats[(int)stat];
            return float.IsFinite(value); //NaN: that player couldn't read it, use its local copy instead
        }

        private float ReadOwnStat(PlayerStatsNew playerStats, EStat stat)
        {
            readingOwnStatDepth++;
            try
            {
                return playerStats.GetStat(stat);
            }
            finally
            {
                readingOwnStatDepth--;
            }
        }

        private void SendOwnStatsIfChanged(uint localPlayerId, PlayerInventory localInventory, float now)
        {
            var stats = Enumerable.Repeat(float.NaN, StatCount).ToArray();
            foreach (var stat in DefinedStats)
            {
                if (statReadFailures.GetValueOrDefault(stat) >= MAX_STAT_READ_FAILURES)
                {
                    continue;
                }

                try
                {
                    stats[(int)stat] = SummedStats.Contains(stat) && ownValues.TryGetValue(stat, out var own) ? own : ReadOwnStat(localInventory.playerStats, stat);
                    statReadFailures.Remove(stat);
                }
                catch (Exception ex)
                {
                    statReadFailures[stat] = statReadFailures.GetValueOrDefault(stat) + 1;
                    Plugin.Log.LogDebug($"Stat {stat} can't be read, not shared this time: {ex.Message}");
                }
            }

            var passives = GetOwnPassiveModifiers(localInventory);

            var hasChanged = lastSentStats == null
                || stats.Where((value, i) => !SameValue(value, lastSentStats[i])).Any()
                || !SamePassives(passives, lastSentPassives);

            if (!(hasChanged && now - lastSentAt >= MIN_SEND_INTERVAL) && now - lastSentAt < HEARTBEAT_INTERVAL)
            {
                return;
            }

            IGameNetworkMessage message = new PlayerStatsChanged
            {
                OwnerId = localPlayerId,
                Stats = stats,
                PassiveModifiers = passives
            };

            if (udpClientService.IsHost() ?? false)
            {
                udpClientService.SendToAllClients(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }

            lastSentStats = stats;
            lastSentPassives = passives;
            lastSentAt = now;
        }

        private static List<StatModifierModel> GetOwnPassiveModifiers(PlayerInventory inventory)
        {
            var modifiers = new List<StatModifierModel>();
            var passive = inventory.passiveAbility;
            if (passive == null || passive.statModifiers == null)
            {
                return modifiers;
            }

            foreach (var statEntry in passive.statModifiers)
            {
                if (statEntry.Value == null || statEntry.Value.statContainers == null)
                {
                    continue;
                }

                foreach (var modifierEntry in statEntry.Value.statContainers)
                {
                    var modifier = modifierEntry.Value;
                    if (modifier == null)
                    {
                        continue;
                    }

                    modifiers.Add(new StatModifierModel
                    {
                        StatType = (int)modifier.stat,
                        Value = modifier.modification,
                        ModificationType = (int)modifier.modifyType
                    });
                }
            }

            return modifiers;
        }

        private static bool SameValue(float a, float b)
        {
            return float.IsNaN(a) ? float.IsNaN(b) : Mathf.Abs(a - b) <= EPSILON;
        }

        private static bool SamePassives(List<StatModifierModel> a, List<StatModifierModel> b)
        {
            if (a == null || b == null || a.Count != b.Count)
            {
                return false;
            }

            for (var i = 0; i < a.Count; i++)
            {
                if (a[i].StatType != b[i].StatType || a[i].ModificationType != b[i].ModificationType || Mathf.Abs(a[i].Value - b[i].Value) > EPSILON)
                {
                    return false;
                }
            }

            return true;
        }

        private void OnReceivedPlayerStats(PlayerStatsChanged message)
        {
            var localPlayer = playerManagerService.GetLocalPlayer();
            var localInventory = GameManager.Instance?.player?.inventory;
            if (localPlayer == null || localInventory == null || message.OwnerId == localPlayer.ConnectionId || message.Stats == null)
            {
                return;
            }

            if (!playerManagerService.GetAllPlayers().Any(p => p.ConnectionId == message.OwnerId))
            {
                return; //Not (or no longer) in the party
            }

            if (!remoteStats.ContainsKey(message.OwnerId))
            {
                Plugin.Log.LogInfo($"Party stats: receiving stats of player {message.OwnerId} ({message.PassiveModifiers?.Count ?? 0} passive bonuses)");
            }

            remoteStats[message.OwnerId] = message.Stats;
            remotePassives[message.OwnerId] = message.PassiveModifiers ?? [];
            ApplyRemotePassivesInRange(localInventory, message.OwnerId);
            RefreshRemoteSums(localPlayer.ConnectionId, localInventory);
        }

        /// <summary>
        /// With a shared passive radius, a player's passive only applies while its owner is close enough
        /// </summary>
        private void ApplyRemotePassivesInRange(PlayerInventory localInventory, uint ownerId)
        {
            if (!remotePassives.TryGetValue(ownerId, out var modifiers))
            {
                return;
            }

            var radius = partySettingsService.Current.SharedPassiveRadius;
            if (radius > 0f)
            {
                var owner = playerManagerService.GetNetPlayerByNetplayId(ownerId);
                var isInRange = owner?.Model != null && Vector3.Distance(owner.Model.transform.position, GameManager.Instance.player.transform.position) <= radius;
                if (!isInRange)
                {
                    modifiers = [];
                }
            }

            ApplyRemotePassives(localInventory, ownerId, modifiers);
        }

        /// <summary>
        /// Another player's passive bonuses, as moving stats of the local player (one per stat and modification type)
        /// </summary>
        private void ApplyRemotePassives(PlayerInventory localInventory, uint ownerId, List<StatModifierModel> modifiers)
        {
            if (!appliedRemotePassives.TryGetValue(ownerId, out var applied))
            {
                applied = [];
                appliedRemotePassives[ownerId] = applied;
            }

            var statInventory = localInventory.statInventory;
            var current = new HashSet<string>();
            foreach (var modifier in modifiers)
            {
                if (!float.IsFinite(modifier.Value))
                {
                    continue;
                }

                var name = $"{PASSIVE_MOVING_STAT_PREFIX}_{ownerId}_{modifier.StatType}_{modifier.ModificationType}";
                current.Add(name);

                // Skip if unchanged and still there (the game may rebuild its moving stats)
                if (applied.TryGetValue(name, out var previous) && Mathf.Abs(previous.Value - modifier.Value) < EPSILON
                    && statInventory.movingStats != null && statInventory.movingStats.ContainsKey(name))
                {
                    continue;
                }

                statInventory.ChangeMovingStat(name, new StatModifier
                {
                    stat = (EStat)modifier.StatType,
                    modifyType = (EStatModifyType)modifier.ModificationType,
                    modification = modifier.Value
                });
                applied[name] = modifier;
            }

            foreach (var name in applied.Keys.Where(name => !current.Contains(name)).ToList())
            {
                statInventory.RemoveMovingStat(name);
                applied.Remove(name);
            }
        }

        private void RefreshRemoteSums(uint localPlayerId, PlayerInventory localInventory)
        {
            var remotePlayerIds = playerManagerService.GetAllPlayers().Select(p => p.ConnectionId).Where(id => id != localPlayerId).ToHashSet();

            var settings = partySettingsService.Current;
            var rules = (settings.PartyStatMode, settings.PartyLuckCap, settings.PartyDifficultyCap);
            var rulesChanged = rules != currentRules;
            currentRules = rules;

            foreach (var stat in SummedStats)
            {
                var values = remoteStats
                    .Where(entry => remotePlayerIds.Contains(entry.Key) && entry.Value.Length > (int)stat && float.IsFinite(entry.Value[(int)stat]))
                    .Select(entry => entry.Value[(int)stat])
                    .ToArray();
                var previous = remoteValues.GetValueOrDefault(stat) ?? [];
                var valuesChanged = values.Length != previous.Length || values.Where((value, i) => Mathf.Abs(value - previous[i]) > EPSILON).Any();
                if (!valuesChanged && !rulesChanged)
                {
                    continue;
                }

                remoteValues[stat] = values;
                if (ownValues.TryGetValue(stat, out var own))
                {
                    partyTotals[stat] = Combine(stat, own, values);
                }
                localInventory.playerStats.QueueUpdateStat(stat); //Recomputed by the game, then OnLocalStatUpdated adds the share
            }
        }

        private void ForgetDisconnectedPlayers(uint localPlayerId, PlayerInventory localInventory)
        {
            var playerIds = playerManagerService.GetAllPlayers().Select(p => p.ConnectionId).ToHashSet();
            var gone = remoteStats.Keys.Concat(appliedRemotePassives.Keys).Where(id => !playerIds.Contains(id)).Distinct().ToList();
            if (gone.Count == 0)
            {
                return;
            }

            foreach (var playerId in gone)
            {
                remoteStats.Remove(playerId);
                remotePassives.Remove(playerId);
                ApplyRemotePassives(localInventory, playerId, []);
                appliedRemotePassives.Remove(playerId);
            }

            RefreshRemoteSums(localPlayerId, localInventory);
        }

        public void Reset()
        {
            remoteStats.Clear();
            appliedRemotePassives.Clear();
            remotePassives.Clear();
            remoteValues.Clear();
            currentRules = default;
            ownValues.Clear();
            partyTotals.Clear();
            lastWrittenTotals.Clear();
            statReadFailures.Clear();
            lastLoggedTotals.Clear();
            localStatsPointer = IntPtr.Zero;
            lastSentStats = null;
            lastSentPassives = null;
            lastSentAt = float.MinValue;
            nextTickAt = 0f;
        }
    }
}
