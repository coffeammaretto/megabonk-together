using Assets.Scripts.UI.InGame.Rewards;
using HarmonyLib;
using MegabonkTogether.Configuration;
using MegabonkTogether.Helpers;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace MegabonkTogether.Patches
{
    /// <summary>
    /// Netplay never pauses and skips the chest opening, so reward windows pop up while the player is still pressing buttons.
    /// Ignore clicks/presses on their buttons for a short time after they appear to prevent accidental picks.
    /// Only player input is blocked (button clicks, number keys), never the window methods themselves, so game or mod driven
    /// calls still go through.
    /// </summary>
    internal static class RewardWindowInputLock
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static float lockedUntil = 0f;

        public static void Start()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            lockedUntil = Time.realtimeSinceStartup + ModConfig.RewardWindowInputDelay.Value;
        }

        public static bool IsLocked()
        {
            return Time.realtimeSinceStartup < lockedUntil && synchronizationService.HasNetplaySessionStarted();
        }
    }

    [HarmonyPatch]
    internal static class RewardWindowOpenedPatches
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase[] candidates =
            [
                AccessTools.Method(typeof(LevelupScreen), nameof(LevelupScreen.Open)),
                AccessTools.Method(typeof(ChestWindowUi), nameof(ChestWindowUi.Open)),
                AccessTools.Method(typeof(ChestWindowUi), nameof(ChestWindowUi.OpeningFinished)), // Take/discard buttons appear
                AccessTools.Method(typeof(EncounterUi), nameof(EncounterUi.Open)),
                AccessTools.Method(typeof(MicrowaveUi), nameof(MicrowaveUi.Open)),
            ];

            return candidates.Where(NativeCodeGuard.IsSafeToPatch);
        }

        public static void Postfix()
        {
            RewardWindowInputLock.Start();
        }
    }

    /// <summary>
    /// The actions behind the reward window buttons. Not the buttons' OnClick: most are empty and their native code is shared
    /// with unrelated methods (see NativeCodeGuard), patching them crashed the game on start.
    /// </summary>
    [HarmonyPatch]
    internal static class RewardWindowChoicePatches
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase[] candidates =
            [
                AccessTools.Method(typeof(UpgradeButton), nameof(UpgradeButton.SelectUpgrade)),
                AccessTools.Method(typeof(LevelupScreen), nameof(LevelupScreen.ChooseOffer)),
                AccessTools.Method(typeof(EncounterButton), nameof(EncounterButton.OnClick)),
                AccessTools.Method(typeof(EncounterUi), nameof(EncounterUi.ChooseOffer)),
                AccessTools.Method(typeof(MicrowaveItemButton), nameof(MicrowaveItemButton.SelectUpgrade)),
                AccessTools.Method(typeof(MicrowaveUi), nameof(MicrowaveUi.ChooseOffer)),
                AccessTools.Method(typeof(LevelupScreen), nameof(LevelupScreen.Skip)),
                AccessTools.Method(typeof(LevelupScreen), nameof(LevelupScreen.Refresh)),
                AccessTools.Method(typeof(LevelupScreen), nameof(LevelupScreen.StartBanish)),
                AccessTools.Method(typeof(ChestWindowUi), nameof(ChestWindowUi.TakeButton)),
                AccessTools.Method(typeof(ChestWindowUi), nameof(ChestWindowUi.DiscardButton)),
                AccessTools.Method(typeof(ChestWindowUi), nameof(ChestWindowUi.BanishButton)),
            ];

            return candidates.Where(NativeCodeGuard.IsSafeToPatch);
        }

        public static bool Prefix()
        {
            if (!RewardWindowInputLock.IsLocked())
            {
                return true;
            }

            Plugin.Log.LogInfo("Ignored reward window input: window just opened");
            return false;
        }
    }

    [HarmonyPatch(typeof(EncounterUi))]
    internal static class EncounterUiKeyboardPatches
    {
        public static bool Prepare(MethodBase original)
        {
            return original == null || NativeCodeGuard.IsSafeToPatch(original);
        }

        /// <summary>
        /// Number keys pick an offer directly, without going through a button
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EncounterUi.KeyboardInput))]
        public static bool KeyboardInput_Prefix()
        {
            return !RewardWindowInputLock.IsLocked();
        }
    }
}
