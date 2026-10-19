using Assets.Scripts.Inventory__Items__Pickups.Items;
using HarmonyLib;
using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using MegabonkTogether.Helpers;
using MegabonkTogether.Scripts.Button;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;

namespace MegabonkTogether.Patches
{
    /// <summary>
    /// "Give" button in the chest window: the item goes to the closest alive teammate instead of the local player
    /// </summary>
    [HarmonyPatch(typeof(ChestWindowUi))]
    internal static class ChestGiveButtonPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IUdpClientService udpClientService = Plugin.Services.GetService<IUdpClientService>();

        private static GameObject giveButton;
        private static TextMeshProUGUI giveButtonText;
        private static ChestWindowUi currentWindow;
        private static uint? currentRecipientId;

        /// <summary>
        /// A new chest: hide the button until its item is shown
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ChestWindowUi.Open))]
        public static void Open_Prefix()
        {
            currentRecipientId = null;
            if (giveButton != null)
            {
                giveButton.SetActive(false);
            }
        }

        public static bool Prepare(MethodBase original)
        {
            return original == null || NativeCodeGuard.IsSafeToPatch(original);
        }

        /// <summary>
        /// Take/discard buttons appear: show the give button next to them
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ChestWindowUi.OpeningFinished))]
        public static void OpeningFinished_Postfix(ChestWindowUi __instance)
        {
            try
            {
                var recipient = synchronizationService.HasNetplaySessionStarted() ? GetRecipient() : null;
                if (recipient == null || __instance.itemData == null || __instance.b_leave == null)
                {
                    if (giveButton != null)
                    {
                        giveButton.SetActive(false);
                    }
                    return;
                }

                if (giveButton == null)
                {
                    CreateGiveButton(__instance.b_leave.gameObject);
                }

                currentWindow = __instance;
                currentRecipientId = recipient.ConnectionId;
                giveButton.SetActive(true);
                if (giveButtonText != null)
                {
                    giveButtonText.text = $"Give to {recipient.Name}";
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Could not show the give button: {ex}");
            }
        }

        private static void CreateGiveButton(GameObject leaveButton)
        {
            giveButton = Object.Instantiate(leaveButton, leaveButton.transform.parent);
            giveButton.name = "MegabonkTogether_GiveButton";

            // Cloned from the leave button: replace its behaviour, keep its look
            for (var i = 0; i < 3; i++)
            {
                var original = giveButton.GetComponent<MyButton>();
                if (original == null)
                {
                    break;
                }
                if (original.disabledOverlay != null)
                {
                    original.disabledOverlay.SetActive(false);
                }
                Object.DestroyImmediate(original);
            }

            var unityButton = giveButton.GetComponentInChildren<UnityEngine.UI.Button>();
            if (unityButton != null)
            {
                unityButton.onClick = new();
                unityButton.interactable = true;
            }

            var localizeStringEvent = giveButton.GetComponentInChildren<LocalizeStringEvent>();
            if (localizeStringEvent != null)
            {
                Object.DestroyImmediate(localizeStringEvent);
            }

            var button = giveButton.AddComponent<CustomButton>();
            button.SetOnClickAction(OnGiveClicked);
            button.OverrideStartHoverAction(() => { });
            button.OverrideEndHoverAction(() => { });
            button.state = MyButton.EButtonState.Active;
            button.RefreshState();

            giveButtonText = giveButton.GetComponentInChildren<TextMeshProUGUI>();

            var rect = giveButton.GetComponent<RectTransform>();
            var leaveRect = leaveButton.GetComponent<RectTransform>();
            if (rect != null && leaveRect != null)
            {
                rect.anchoredPosition = leaveRect.anchoredPosition - new Vector2(0f, leaveRect.rect.height + 12f);
            }
        }

        private static void OnGiveClicked()
        {
            if (RewardWindowInputLock.IsLocked() || currentWindow == null || currentWindow.itemData == null)
            {
                return;
            }

            var recipient = currentRecipientId.HasValue ? playerManagerService.GetPlayer(currentRecipientId.Value) : null;
            var localPlayer = playerManagerService.GetLocalPlayer();
            if (recipient == null || localPlayer == null)
            {
                return;
            }
            currentRecipientId = null;

            var item = currentWindow.itemData.eItem;
            IGameNetworkMessage message = new GiveItem
            {
                FromId = localPlayer.ConnectionId,
                ToId = recipient.ConnectionId,
                Item = (int)item
            };

            if (udpClientService.IsHost() ?? false)
            {
                udpClientService.SendToAllClients(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }

            Plugin.Log.LogInfo($"Gave {item} to {recipient.Name}");
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiSelect.sounds[0]);
            giveButton.SetActive(false);
            currentWindow.DiscardButton(); //Close the reward without taking it
        }

        /// <summary>
        /// Closest alive teammate
        /// </summary>
        private static Common.Models.Player GetRecipient()
        {
            var localPlayer = playerManagerService.GetLocalPlayer();
            var localPosition = GameManager.Instance?.player?.transform.position ?? Vector3.zero;
            if (localPlayer == null)
            {
                return null;
            }

            return playerManagerService.GetAllPlayersAlive()
                .Where(p => p.ConnectionId != localPlayer.ConnectionId)
                .OrderBy(p =>
                {
                    var netPlayer = playerManagerService.GetNetPlayerByNetplayId(p.ConnectionId);
                    return netPlayer?.Model != null ? Vector3.Distance(netPlayer.Model.transform.position, localPosition) : float.MaxValue;
                })
                .FirstOrDefault();
        }
    }
}
