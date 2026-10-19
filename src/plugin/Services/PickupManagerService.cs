using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using MegabonkTogether.Common.Models;
using MegabonkTogether.Extensions;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MegabonkTogether.Services
{
    public interface IPickupManagerService
    {
        public IEnumerable<PickupModel> GetAllPickups();
        public IEnumerable<(uint, Pickup)> GetAllPickupXp();
        public uint AddSpawnedPickup(Pickup pickup);
        public void SetSpawnedPickup(uint pickupId, Pickup pickup);
        public Pickup GetPickupById(uint id);
        public KeyValuePair<uint, Pickup> GetPickupByReference(Pickup pickup);
        public List<uint> RemovePickupByReference(Pickup pickup, bool cleanupXpList);
        public Pickup GetSpawnedPickupById(uint id);
        public void RemoveSpawnedPickupById(uint id);
        public void ResetForNextLevel();
    }
    internal class PickupManagerService : IPickupManagerService
    {
        private readonly ConcurrentDictionary<uint, Pickup> spawnedPickups = [];
        // The game stacks nearby xp/gold into an existing pickup, so one object can carry several network ids (oldest first)
        private readonly Dictionary<int, List<uint>> pickupIdsByInstanceId = [];
        private readonly object pickupIdsLock = new();
        private uint currentPickupId = 0; //TODO: concurrency?

        public IEnumerable<PickupModel> GetAllPickups()
        {
            return spawnedPickups.Select(kv => kv.Value.ToModel(kv.Key)).ToList();
        }

        public Pickup GetPickupById(uint id)
        {
            if (spawnedPickups.TryGetValue(id, out var pickup))
            {
                return pickup;
            }
            return null;
        }


        /// <summary>
        /// Server side
        /// </summary>
        public uint AddSpawnedPickup(Pickup pickup)
        {
            currentPickupId++;
            if (!spawnedPickups.TryAdd(currentPickupId, pickup))
            {
                Plugin.Log.LogWarning($"Attempted to add an pickup that already exists. PickupId: {currentPickupId}");
                return 0;
            }

            TrackInstanceId(currentPickupId, pickup);

            return currentPickupId;
        }

        /// <summary>
        /// Client side
        /// </summary>
        public void SetSpawnedPickup(uint pickupId, Pickup pickup)
        {
            if (!spawnedPickups.TryAdd(pickupId, pickup))
            {
                Plugin.Log.LogWarning($"Attempted to add an pickup that already exists. PickupId: {pickupId}");
                return;
            }

            TrackInstanceId(pickupId, pickup);
        }

        private void TrackInstanceId(uint pickupId, Pickup pickup)
        {
            lock (pickupIdsLock)
            {
                var instanceId = pickup.GetInstanceID();
                if (!pickupIdsByInstanceId.TryGetValue(instanceId, out var ids))
                {
                    ids = [];
                    pickupIdsByInstanceId[instanceId] = ids;
                }

                if (!ids.Contains(pickupId))
                {
                    ids.Add(pickupId);
                }
            }
        }

        /// <summary>
        /// Oldest network id carried by this pickup object
        /// </summary>
        public KeyValuePair<uint, Pickup> GetPickupByReference(Pickup pickup)
        {
            if (pickup == null)
            {
                return default;
            }

            lock (pickupIdsLock)
            {
                if (pickupIdsByInstanceId.TryGetValue(pickup.GetInstanceID(), out var ids))
                {
                    foreach (var id in ids)
                    {
                        if (spawnedPickups.TryGetValue(id, out var spawned))
                        {
                            return new KeyValuePair<uint, Pickup>(id, spawned);
                        }
                    }
                }
            }

            return default;
        }

        /// <summary>
        /// Forget every network id carried by this pickup object (it was consumed, or the pool handed it out again)
        /// </summary>
        public List<uint> RemovePickupByReference(Pickup pickup, bool cleanupXpList)
        {
            if (pickup == null)
            {
                return [];
            }

            List<uint> ids;
            lock (pickupIdsLock)
            {
                if (!pickupIdsByInstanceId.Remove(pickup.GetInstanceID(), out ids))
                {
                    return [];
                }

                foreach (var id in ids)
                {
                    spawnedPickups.TryRemove(id, out _);
                }
            }

            if (cleanupXpList && pickup.ePickup == EPickup.Xp) //Properly cleanup XP pickups
            {
                PickupManager.Instance.xpList.RemovePickup(pickup);
            }

            return ids;
        }

        public Pickup GetSpawnedPickupById(uint id)
        {
            if (spawnedPickups.TryGetValue(id, out var pickup))
            {
                return pickup;
            }
            return null;
        }

        /// <summary>
        /// Remove the pickup and any other id stacked on the same object, since that object is gone
        /// </summary>
        public void RemoveSpawnedPickupById(uint id)
        {
            if (!spawnedPickups.TryRemove(id, out var pickup))
            {
                return;
            }

            RemovePickupByReference(pickup, true);
        }

        public IEnumerable<(uint, Pickup)> GetAllPickupXp()
        {
            return spawnedPickups
                .ToList()
                .Where(p => p.Value.ePickup == EPickup.Xp)
                .Select(p => (p.Key, p.Value));
        }

        public void ResetForNextLevel()
        {
            currentPickupId = 0;
            //spawnedPickups.Select(kv => kv.Value).ToList().ForEach(p => GameObject.Destroy(p.gameObject));
            spawnedPickups.Clear();
            lock (pickupIdsLock)
            {
                pickupIdsByInstanceId.Clear();
            }
        }
    }
}
