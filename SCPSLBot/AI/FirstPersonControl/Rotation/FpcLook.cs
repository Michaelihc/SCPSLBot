using MEC;
using PlayerRoles.FirstPersonControl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Looking
{
    internal class FpcLook
    {
        public Quaternion DesiredHorizontalRotation { get; set; } = Quaternion.identity;
        public Quaternion DesiredVerticalRotation { get; set; } = Quaternion.identity;

        public Quaternion TargetHorizontalRotation { get; set; } = Quaternion.identity;

        private const float MaxSteeringForceDegrees = 640f;

        // Exponential smoothing rate. At 60 FPS this reproduces the original fixed 0.075 alpha
        // (1 - exp(-4.7/60) ~= 0.075) but stays consistent at any frame rate / tick interval.
        private const float TurnResponsiveness = 4.7f;

        private readonly FpcBotPlayer botPlayer;

        public FpcLook(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        public void ToPosition(Vector3 targetPosition)
        {
            var playerTransform = botPlayer.FpcRole.FpcModule.transform;
            var cameraTransform = botPlayer.BotHub.PlayerHub.PlayerCameraReference;

            var relativePos = targetPosition - cameraTransform.position;

            var hDirectionToTarget = Vector3.ProjectOnPlane(relativePos, Vector3.up).normalized;
            var hForward = playerTransform.forward;

            var hRotation = Quaternion.FromToRotation(hForward, hDirectionToTarget);

            var hReverseRotation = Quaternion.Inverse(hRotation);

            var vDirectionToTarget = Vector3.Normalize(hReverseRotation * relativePos);
            var vForward = cameraTransform.forward;
            var vLocalForward = playerTransform.InverseTransformDirection(vForward);
            var vLocalDirToTarget = playerTransform.InverseTransformDirection(vDirectionToTarget);

            var vRotation = Quaternion.FromToRotation(vLocalForward, vLocalDirToTarget);

            TargetHorizontalRotation = hRotation;

            var turnAlpha = 1f - Mathf.Exp(-TurnResponsiveness * Time.deltaTime);
            hRotation = Quaternion.Slerp(Quaternion.identity, hRotation, turnAlpha);
            vRotation = Quaternion.Slerp(Quaternion.identity, vRotation, turnAlpha);

            hRotation = Quaternion.RotateTowards(Quaternion.identity, hRotation, Time.deltaTime * MaxSteeringForceDegrees);
            vRotation = Quaternion.RotateTowards(Quaternion.identity, vRotation, Time.deltaTime * MaxSteeringForceDegrees);

            DesiredHorizontalRotation = hRotation;
            DesiredVerticalRotation = vRotation;
        }

        public IEnumerator<float> ByFpcAsync(Vector3 degreesStep, Vector3 targetDegrees)
        {
            var currentMagnitude = 0f;

            var degreesStepMagnitude = degreesStep.magnitude;
            var targetDegreesMagnitude = targetDegrees.magnitude;

            do
            {
                //DesiredAngles = degreesStep * Time.deltaTime;

                currentMagnitude += degreesStepMagnitude * Time.deltaTime;

                yield return Timing.WaitForOneFrame;
            }
            while (currentMagnitude < targetDegreesMagnitude);

            //DesiredAngles = Vector3.zero;

            yield break;
        }
    }
}
