using Interactables.Interobjects;
using Mirror;
using PlayerRoles.Ragdolls;
using PlayerRoles.Subroutines;
using RelativePositioning;
using System;
using UnityEngine;
using Utils.Networking;

namespace SCPSLBot.AI.FirstPersonControl.Combat.ScpAbilities
{
    /// <summary>
    /// Submits SCP ability commands straight to <see cref="SubroutineBase.ServerProcessCmd"/> with the
    /// exact payload the native client writes in <c>ClientWriteCmd</c>. Native server validation still
    /// runs (range, cooldown, role state), so a rejected command costs nothing. Writers and readers
    /// are pooled, so a command allocates nothing per call.
    /// </summary>
    internal static class ScpAbilityCommands
    {
        private static readonly ArraySegment<byte> EmptyPayload = new(Array.Empty<byte>());

        /// <summary>Sends a command that carries no payload (toggle and trigger abilities).</summary>
        public static bool Send(SubroutineBase subroutine)
        {
            if (subroutine == null)
            {
                return false;
            }

            var reader = NetworkReaderPool.Get(EmptyPayload);
            try
            {
                subroutine.ServerProcessCmd(reader);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return false;
            }
            finally
            {
                NetworkReaderPool.Return(reader);
            }
        }

        /// <summary>Gets a pooled writer for a payload; hand it to <see cref="Send(SubroutineBase, NetworkWriterPooled)"/>.</summary>
        public static NetworkWriterPooled BeginPayload()
        {
            return NetworkWriterPool.Get();
        }

        /// <summary>Sends the payload and always returns the writer to the pool.</summary>
        public static bool Send(SubroutineBase subroutine, NetworkWriterPooled payload)
        {
            try
            {
                if (subroutine == null)
                {
                    return false;
                }

                var reader = NetworkReaderPool.Get(payload.ToArraySegment());
                try
                {
                    subroutine.ServerProcessCmd(reader);
                    return true;
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    return false;
                }
                finally
                {
                    NetworkReaderPool.Return(reader);
                }
            }
            finally
            {
                NetworkWriterPool.Return(payload);
            }
        }

        public static bool SendHub(SubroutineBase subroutine, ReferenceHub hub)
        {
            var payload = BeginPayload();
            payload.WriteReferenceHub(hub);
            return Send(subroutine, payload);
        }

        public static bool SendBool(SubroutineBase subroutine, bool value)
        {
            var payload = BeginPayload();
            payload.WriteBool(value);
            return Send(subroutine, payload);
        }

        /// <summary>Ragdoll abilities (resurrect, consume, disguise): a null ragdoll cancels a running process.</summary>
        public static bool SendRagdoll(SubroutineBase subroutine, DynamicRagdoll ragdoll)
        {
            var payload = BeginPayload();
            payload.WriteNetworkBehaviour(ragdoll);
            return Send(subroutine, payload);
        }

        public static bool SendPryableDoor(SubroutineBase subroutine, PryableDoor door)
        {
            var payload = BeginPayload();
            payload.WriteNetworkBehaviour(door);
            return Send(subroutine, payload);
        }

        /// <summary>Scp096AttackAbility: owner position and camera rotation, no backtracked players.</summary>
        public static bool SendOwnerPose(SubroutineBase subroutine, Vector3 ownerPosition, Quaternion cameraRotation)
        {
            var payload = BeginPayload();
            payload.WriteRelativePosition(new RelativePosition(ownerPosition));
            payload.WriteQuaternion(cameraRotation);
            return Send(subroutine, payload);
        }

        /// <summary>Scp106Attack: target, its position, the owner camera rotation and the owner position.</summary>
        public static bool SendTargetedStrike(
            SubroutineBase subroutine,
            ReferenceHub target,
            Vector3 targetPosition,
            Quaternion ownerCameraRotation,
            Vector3 ownerPosition)
        {
            var payload = BeginPayload();
            payload.WriteReferenceHub(target);
            payload.WriteRelativePosition(new RelativePosition(targetPosition));
            payload.WriteQuaternion(ownerCameraRotation);
            payload.WriteRelativePosition(new RelativePosition(ownerPosition));
            return Send(subroutine, payload);
        }

        /// <summary>Scp3114Strangle: target, target position, attacker position.</summary>
        public static bool SendStrangle(
            SubroutineBase subroutine,
            ReferenceHub target,
            Vector3 targetPosition,
            Vector3 attackerPosition)
        {
            var payload = BeginPayload();
            payload.WriteReferenceHub(target);
            payload.WriteRelativePosition(new RelativePosition(targetPosition));
            payload.WriteRelativePosition(new RelativePosition(attackerPosition));
            return Send(subroutine, payload);
        }

        /// <summary>
        /// Scp106HuntersAtlasAbility: room origin as a relative position, then the destination offset
        /// from it in 1/50 m steps. Returns false when the offset does not fit the native shorts.
        /// </summary>
        public static bool SendAtlasDestination(SubroutineBase subroutine, Transform room, Vector3 destination)
        {
            var roomPosition = room.position;
            var offset = Vector3Int.RoundToInt((destination - roomPosition) * 50f);
            if (offset.x < short.MinValue || offset.x > short.MaxValue
                || offset.z < short.MinValue || offset.z > short.MaxValue)
            {
                return false;
            }

            var payload = BeginPayload();
            payload.WriteRelativePosition(new RelativePosition(roomPosition));
            payload.WriteShort((short)offset.x);
            payload.WriteShort((short)offset.z);
            return Send(subroutine, payload);
        }
    }
}
