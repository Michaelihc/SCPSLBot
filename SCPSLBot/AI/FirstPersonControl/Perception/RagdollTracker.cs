using PlayerRoles;
using PlayerRoles.Ragdolls;
using SCPSLBot.Performance;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Perception
{
    internal readonly struct TrackedRagdoll
    {
        public TrackedRagdoll(DynamicRagdoll ragdoll, Vector3 position, RoleTypeId role)
        {
            Ragdoll = ragdoll;
            Position = position;
            Role = role;
        }

        public DynamicRagdoll Ragdoll { get; }

        /// <summary>Position when the snapshot was taken; use the live ragdoll transforms for exact reach checks.</summary>
        public Vector3 Position { get; }

        public RoleTypeId Role { get; }
    }

    /// <summary>
    /// Process-wide, low-rate view of the corpses ragdoll abilities can act on. The game already
    /// maintains <see cref="RagdollManager.AllRagdolls"/>, so one shared snapshot per refresh replaces
    /// a scan per bot; consumers filter the (short) list themselves.
    /// </summary>
    internal static class RagdollTracker
    {
        internal const float RefreshesPerSecond = 2f;

        private static readonly FixedRateRefreshGate RefreshGate = new(RefreshesPerSecond);
        private static readonly List<TrackedRagdoll> Builder = new(32);
        private static TrackedRagdoll[] entries = System.Array.Empty<TrackedRagdoll>();

        public static TrackedRagdoll[] Get(float now)
        {
            if (!RefreshGate.TryAcquire(now))
            {
                return entries;
            }

            Builder.Clear();
            foreach (var ragdoll in RagdollManager.AllRagdolls)
            {
                if (ragdoll is DynamicRagdoll dynamicRagdoll && dynamicRagdoll != null)
                {
                    Builder.Add(new TrackedRagdoll(dynamicRagdoll, dynamicRagdoll.transform.position, dynamicRagdoll.Info.RoleType));
                }
            }

            entries = Builder.ToArray();
            return entries;
        }

        internal static void Reset()
        {
            entries = System.Array.Empty<TrackedRagdoll>();
            Builder.Clear();
            RefreshGate.Reset();
        }

        /// <summary>Finds the ragdoll bone closest to <paramref name="from"/>, the same points the native reach check tests.</summary>
        public static bool TryGetNearestBone(DynamicRagdoll ragdoll, Vector3 from, out Vector3 bonePosition, out float sqrDistance)
        {
            bonePosition = default;
            sqrDistance = float.MaxValue;
            if (ragdoll == null)
            {
                return false;
            }

            var bones = ragdoll.LinkedRigidbodiesTransforms;
            if (bones == null || bones.Length == 0)
            {
                bonePosition = ragdoll.transform.position;
                sqrDistance = (bonePosition - from).sqrMagnitude;
                return true;
            }

            var found = false;
            foreach (var bone in bones)
            {
                if (bone == null)
                {
                    continue;
                }

                var position = bone.position;
                var distance = (position - from).sqrMagnitude;
                if (distance < sqrDistance)
                {
                    sqrDistance = distance;
                    bonePosition = position;
                    found = true;
                }
            }

            return found;
        }
    }
}
