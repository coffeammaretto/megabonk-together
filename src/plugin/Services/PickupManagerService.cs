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
        public Pickup GetSpawnedPickupById(uint id);
        public void RemoveSpawnedPickupById(uint id);
        public void ResetForNextLevel();
    }
    internal class PickupManagerService : IPickupManagerService
    {
        private readonly ConcurrentDictionary<uint, Pickup> spawnedPickups = [];
        // Pickups are pooled by the game: index by Unity instance id so a reused object maps to its current id only
        private readonly ConcurrentDictionary<int, uint> pickupIdsByInstanceId = [];
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

        /// <summary>
        /// Map the pickup object to its id. If the pooled object was still registered under an older id,
        /// that pickup is gone (the object got reused), so drop the stale entry instead of letting it resolve to the wrong id.
        /// </summary>
        private void TrackInstanceId(uint pickupId, Pickup pickup)
        {
            var instanceId = pickup.GetInstanceID();

            if (pickupIdsByInstanceId.TryGetValue(instanceId, out var previousId)
                && previousId != pickupId
                && spawnedPickups.TryGetValue(previousId, out var previous)
                && previous.GetInstanceID() == instanceId)
            {
                spawnedPickups.TryRemove(previousId, out _);
                Plugin.Log.LogInfo($"Pickup object reused for {pickupId}, dropping stale pickup {previousId}");
            }

            pickupIdsByInstanceId[instanceId] = pickupId;
        }

        public KeyValuePair<uint, Pickup> GetPickupByReference(Pickup pickup)
        {
            if (pickup != null
                && pickupIdsByInstanceId.TryGetValue(pickup.GetInstanceID(), out var pickupId)
                && spawnedPickups.TryGetValue(pickupId, out var spawned))
            {
                return new KeyValuePair<uint, Pickup>(pickupId, spawned);
            }

            return default;
        }

        public Pickup GetSpawnedPickupById(uint id)
        {
            if (spawnedPickups.TryGetValue(id, out var pickup))
            {
                return pickup;
            }
            return null;
        }

        public void RemoveSpawnedPickupById(uint id)
        {
            if (!spawnedPickups.TryRemove(id, out var pickup))
            {
                return;
            }

            pickupIdsByInstanceId.TryRemove(new KeyValuePair<int, uint>(pickup.GetInstanceID(), id)); // Only if not already reused

            if (pickup.ePickup == EPickup.Xp) //Properly cleanup XP pickups
            {
                PickupManager.Instance.xpList.RemovePickup(pickup);
            }
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
            pickupIdsByInstanceId.Clear();
        }
    }
}
