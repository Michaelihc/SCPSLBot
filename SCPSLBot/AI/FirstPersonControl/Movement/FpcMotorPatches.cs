using HarmonyLib;
using PlayerRoles.FirstPersonControl;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Movement
{
    [HarmonyPatch(typeof(FpcMotor))]
    internal static class FpcMotorPatches
    {
        // These prefixes run for EVERY FPC player every frame. Keep them allocation- and
        // reflection-free: early-out when no bots exist, then read the public readonly
        // FpcMotor.Hub / FpcMotor.MainModule fields directly instead of reflecting.
        [HarmonyPatch("DesiredMove", MethodType.Getter)]
        [HarmonyPrefix()]
        public static bool GetBotDesiredMoveIfBot(FpcMotor __instance, ref Vector3 __result)
        {
            if (BotManager.Instance.BotPlayers.Count == 0)
            {
                return true;
            }

            var hub = __instance.Hub;
            if (hub != null
                && BotManager.Instance.BotPlayers.TryGetValue(hub, out var botHub)
                && botHub.CurrentBotPlayer is FpcBotPlayer fpcPlayer)
            {
                __result = __instance.MainModule.transform.TransformDirection(fpcPlayer.Move.DesiredLocalDirection);
                fpcPlayer.Move.DesiredLocalDirection = Vector3.zero;

                return false;
            }

            return true;
        }

        [HarmonyPatch("GetFrameMove")]
        [HarmonyPrefix()]
        public static bool AssignNewReceivedPositionIfBot(FpcMotor __instance, ref Vector3 __result)
        {
            if (BotManager.Instance.BotPlayers.Count == 0)
            {
                return true;
            }

            var hub = __instance.Hub;
            if (hub != null
                && BotManager.Instance.BotPlayers.TryGetValue(hub, out var botHub)
                && botHub.CurrentBotPlayer is FpcBotPlayer)
            {
                __instance.ReceivedPosition = new RelativePositioning.RelativePosition(__instance.MainModule.Position + __instance.MoveDirection);
            }

            return true;
        }
    }
}
