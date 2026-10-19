using MegabonkTogether.Common.Messages.GameNetworkMessages;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Shared experience reward pause. Each player is choosing (reward window open), waiting (done, paused until the others
    /// are done) or free. The host releases the waiting players once nobody is choosing anymore.
    /// Based on states rather than counting "done" events, so a level up only one player got, or a lost/duplicated message,
    /// can no longer leave everyone waiting.
    /// </summary>
    public interface IEncounterService
    {
        public bool IsWaiting { get; }
        public float WaitingSince { get; }

        /// <summary>
        /// Set while releasing the local player, so the RewardFinished patch lets the game close the reward
        /// </summary>
        public bool IsReleasing { get; set; }

        public EncounterSyncState GetLocalState();
        public void StartWaiting();
        public void StopWaiting();

        /// <summary>
        /// Host: last CloseEncounter sent. Client: last CloseEncounter received.
        /// </summary>
        public uint CloseSeq { get; set; }

        public void SetRemoteState(uint playerId, EncounterSyncState state, uint closeSeq);
        public void RemoveRemoteState(uint playerId);
        public void RequestForceClose();

        /// <summary>
        /// Host: whether waiting players can be released. Increments CloseSeq when true.
        /// </summary>
        public bool TryClose(IEnumerable<uint> remotePlayerIds);

        public void ResetForNextLevel();
        public void Reset();
    }

    internal class EncounterService : IEncounterService
    {
        private const float MIN_CLOSE_INTERVAL = 0.25f;

        private readonly ConcurrentDictionary<uint, (EncounterSyncState State, uint CloseSeq)> remoteStates = new();
        private volatile bool forceCloseRequested = false;
        private float lastCloseAt = float.MinValue;

        public bool IsWaiting { get; private set; } = false;
        public float WaitingSince { get; private set; } = 0f;
        public bool IsReleasing { get; set; } = false;
        public uint CloseSeq { get; set; } = 0;

        public EncounterSyncState GetLocalState()
        {
            if (IsWaiting)
            {
                return EncounterSyncState.Waiting;
            }

            var encounterWindows = UiManager.Instance?.encounterWindows;
            if (encounterWindows != null && encounterWindows.encounterInProgress)
            {
                return EncounterSyncState.Choosing;
            }

            return EncounterSyncState.Free;
        }

        public void StartWaiting()
        {
            if (IsWaiting)
            {
                return;
            }

            IsWaiting = true;
            WaitingSince = Time.realtimeSinceStartup;
        }

        public void StopWaiting()
        {
            IsWaiting = false;
        }

        public void SetRemoteState(uint playerId, EncounterSyncState state, uint closeSeq)
        {
            remoteStates[playerId] = (state, closeSeq);
        }

        public void RemoveRemoteState(uint playerId)
        {
            remoteStates.TryRemove(playerId, out _);
        }

        public void RequestForceClose()
        {
            forceCloseRequested = true;
        }

        public bool TryClose(IEnumerable<uint> remotePlayerIds)
        {
            var now = Time.realtimeSinceStartup;
            if (now - lastCloseAt < MIN_CLOSE_INTERVAL)
            {
                return false;
            }

            var localState = GetLocalState();
            var anyWaiting = localState == EncounterSyncState.Waiting;
            var anyChoosing = localState == EncounterSyncState.Choosing;

            foreach (var playerId in remotePlayerIds)
            {
                if (!remoteStates.TryGetValue(playerId, out var remote))
                {
                    continue;
                }

                if (remote.State == EncounterSyncState.Choosing)
                {
                    anyChoosing = true;
                }
                else if (remote.State == EncounterSyncState.Waiting && remote.CloseSeq >= CloseSeq) // Older ones were already released
                {
                    anyWaiting = true;
                }
            }

            if (!forceCloseRequested && (!anyWaiting || anyChoosing))
            {
                return false;
            }

            forceCloseRequested = false;
            lastCloseAt = now;
            CloseSeq++;
            return true;
        }

        public void ResetForNextLevel()
        {
            IsWaiting = false;
            IsReleasing = false;
            forceCloseRequested = false;
            remoteStates.Clear();
        }

        public void Reset()
        {
            ResetForNextLevel();
            CloseSeq = 0;
            lastCloseAt = float.MinValue;
        }
    }
}
