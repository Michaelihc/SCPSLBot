using HarmonyLib;
using PlayerRoles.FirstPersonControl;
using System;
using System.Reflection;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Rotation
{
    [HarmonyPatch(typeof(FpcMouseLook))]
    internal static class FpcMouseLookPatches
    {
        [HarmonyPatch(nameof(FpcMouseLook.UpdateRotation))]
        [HarmonyPrefix()]
        public static bool UpdateBotRotation(FpcMouseLook __instance)
        {
            if (BotManager.Instance.BotPlayers.Count == 0)
            {
                return true;
            }

            var hub = HubFieldRef(__instance);

            if (hub != null && BotManager.Instance.BotPlayers.TryGetValue(hub, out var botHub)
                && botHub.CurrentBotPlayer is FpcBotPlayer fpcPlayer)
            {
                var hRotation = fpcPlayer.Look.DesiredHorizontalRotation;
                var vRotation = fpcPlayer.Look.DesiredVerticalRotation;

                //__instance.CurrentHorizontal = Mathf.DeltaAngle(0f, hub.transform.eulerAngles.y);
                //__instance.CurrentVertical = -Mathf.DeltaAngle(0f, hub.PlayerCameraReference.localEulerAngles.x);

                __instance.CurrentHorizontal += hRotation.eulerAngles.y;
                __instance.CurrentVertical += -Mathf.DeltaAngle(0f, vRotation.eulerAngles.x);

                Quaternion rotation = Quaternion.Euler(Vector3.up * __instance.CurrentHorizontal);
                Quaternion cameraRotation = Quaternion.Euler(Vector3.left * __instance.CurrentVertical);

                hub.transform.rotation = rotation;
                hub.PlayerCameraReference.localRotation = cameraRotation;

                fpcPlayer.Look.DesiredHorizontalRotation = Quaternion.identity;
                fpcPlayer.Look.DesiredVerticalRotation = Quaternion.identity;
                fpcPlayer.Look.TargetHorizontalRotation = Quaternion.identity;

                return false;
            }

            return true;
        }

        private static readonly AccessTools.FieldRef<FpcMouseLook, ReferenceHub> HubFieldRef =
            AccessTools.FieldRefAccess<FpcMouseLook, ReferenceHub>("_hub");
    }
}
