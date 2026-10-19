using MegabonkTogether.Common.Messages.GameNetworkMessages;
using MegabonkTogether.Helpers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Shared experience reward pause. Each player is choosing (reward window open), waiting (paused until the others are
    /// done) or free. As soon as someone is choosing, the host pauses everyone else; once nobody is choosing anymore, it
    /// releases the waiting players.
    /// Based on states rather than counting "done" events, so a level up only one player got, or a lost/duplicated message,
    /// can no longer leave everyone waiting.
    /// </summary>
    public interface IEncounterService
    {
        public bool IsWaiting { get; }
        public float WaitingSince { get; }

        /// <summary>
        /// Waiting with its own reward done but not closed yet (window hidden), rather than just paused for the others
        /// </summary>
        public bool IsWaitingOnHiddenReward { get; }

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
        /// Host: whether the local player or one of the given remote players is choosing a reward
        /// </summary>
        public bool IsAnyoneChoosing(IEnumerable<uint> remotePlayerIds);

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
        public bool IsWaitingOnHiddenReward { get; private set; } = false;
        public bool IsReleasing { get; set; } = false;
        public uint CloseSeq { get; set; } = 0;

        public EncounterSyncState GetLocalState()
        {
            var encounterWindows = UiManager.Instance?.encounterWindows;
            var encounterInProgress = encounterWindows != null && encounterWindows.encounterInProgress;

            if (IsWaiting)
            {
                if (IsWaitingOnHiddenReward || !encounterInProgress)
                {
                    return EncounterSyncState.Waiting;
                }

                // Got its own reward while paused for the others (e.g. xp received just before the pause): choosing now
                IsWaiting = false;
                ScreenTextHelper.Clear();
            }

            return encounterInProgress ? EncounterSyncState.Choosing : EncounterSyncState.Free;
        }

        public void StartWaiting()
        {
            var encounterWindows = UiManager.Instance?.encounterWindows;
            var encounterInProgress = encounterWindows != null && encounterWindows.encounterInProgress;

            if (IsWaiting)
            {
                // Paused for the others, then finished a reward of its own before GetLocalState noticed it: release must close it
                IsWaitingOnHiddenReward |= encounterInProgress;
                return;
            }

            IsWaiting = true;
            IsWaitingOnHiddenReward = encounterInProgress;
            WaitingSince = Time.realtimeSinceStartup;
        }

        public void StopWaiting()
        {
            IsWaiting = false;
            IsWaitingOnHiddenReward = false;
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

        public bool IsAnyoneChoosing(IEnumerable<uint> remotePlayerIds)
        {
            return GetPlayersStatus(remotePlayerIds).AnyChoosing;
        }

        public bool TryClose(IEnumerable<uint> remotePlayerIds)
        {
            var now = Time.realtimeSinceStartup;
            if (now - lastCloseAt < MIN_CLOSE_INTERVAL)
            {
                return false;
            }

            var (anyWaiting, anyChoosing) = GetPlayersStatus(remotePlayerIds);
            if (!forceCloseRequested && (!anyWaiting || anyChoosing))
            {
                return false;
            }

            forceCloseRequested = false;
            lastCloseAt = now;
            CloseSeq++;
            return true;
        }

        private (bool AnyWaiting, bool AnyChoosing) GetPlayersStatus(IEnumerable<uint> remotePlayerIds)
        {
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

            return (anyWaiting, anyChoosing);
        }

        public void ResetForNextLevel()
        {
            StopWaiting();
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
