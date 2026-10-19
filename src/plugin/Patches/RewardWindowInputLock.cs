using Assets.Scripts.UI.InGame.Rewards;
using HarmonyLib;
using MegabonkTogether.Configuration;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
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
            yield return AccessTools.Method(typeof(LevelupScreen), nameof(LevelupScreen.Open));
            yield return AccessTools.Method(typeof(ChestWindowUi), nameof(ChestWindowUi.Open));
            yield return AccessTools.Method(typeof(ChestWindowUi), nameof(ChestWindowUi.OpeningFinished)); // Take/discard buttons appear
            yield return AccessTools.Method(typeof(EncounterUi), nameof(EncounterUi.Open));
            yield return AccessTools.Method(typeof(MicrowaveUi), nameof(MicrowaveUi.Open));
        }

        public static void Postfix()
        {
            RewardWindowInputLock.Start();
        }
    }

    [HarmonyPatch]
    internal static class RewardWindowButtonClickPatches
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(UpgradeButton), nameof(UpgradeButton.OnClick));
            yield return AccessTools.Method(typeof(EncounterButton), nameof(EncounterButton.OnClick));
            yield return AccessTools.Method(typeof(MicrowaveItemButton), nameof(MicrowaveItemButton.OnClick));
            yield return AccessTools.Method(typeof(MyButtonOffersUtility), nameof(MyButtonOffersUtility.OnClick));
            yield return AccessTools.Method(typeof(MyButtonNormal), nameof(MyButtonNormal.OnClick));
            yield return AccessTools.Method(typeof(MyButton), nameof(MyButton.OnClick));
        }

        public static bool Prefix(MyButton __instance, MethodBase __originalMethod)
        {
            if (!RewardWindowInputLock.IsLocked())
            {
                return true;
            }

            // Generic buttons are used everywhere (e.g. chest take/leave, but also menus): only block those of reward windows
            var isGenericButton = __originalMethod.DeclaringType == typeof(MyButton) || __originalMethod.DeclaringType == typeof(MyButtonNormal);
            if (isGenericButton && __instance.GetComponentInParent<BaseEncounterWindow>() == null)
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
