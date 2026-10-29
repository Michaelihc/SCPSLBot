using LabLogger = LabApi.Features.Console.Logger;
using PlayerRoles;
using PlayerRoles.PlayableScps.Scp096;
using PlayerRoles.PlayableScps.Scp106;
using PlayerRoles.Subroutines;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat.ScpAbilities
{
    /// <summary>
    /// Native attack geometry read from the role prefabs' serialized fields, so the bots swing at the
    /// real reach instead of a hand-tuned guess and follow game updates. Sphere attacks
    /// (<c>ScpAttackAbilityBase</c>: SCP-939 claw, SCP-049-2, SCP-3114 slap; SCP-096 slaps) hit any
    /// hitbox inside a sphere <c>offset</c> metres ahead of the camera; SCP-106 picks a target within
    /// <c>_maxRangeSqr</c> whose horizontal facing dot clears a distance curve. Values are prefab
    /// constants, so each attack type is resolved once.
    /// </summary>
    internal static class ScpAttackReach
    {
        /// <summary>Hitbox colliders reach this far from the aim point; the sphere only has to touch one.</summary>
        private const float HitboxSlack = 0.25f;

        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly Dictionary<Type, Sphere> Spheres = new();
        private static readonly Dictionary<Type, Scp106Geometry> Scp106Attacks = new();

        private readonly struct Sphere
        {
            public Sphere(float offset, float radius)
            {
                Offset = offset;
                Radius = radius;
            }

            public float Offset { get; }
            public float Radius { get; }
            public bool IsValid => Radius > 0f;
        }

        private sealed class Scp106Geometry
        {
            public float MaxRangeSqr;
            public AnimationCurve DotOverDistance;
        }

        /// <summary>
        /// Camera-to-aim-point distance at which a sphere attack connects when aimed straight at the
        /// target; false when the subroutine has no readable sphere.
        /// </summary>
        public static bool TryGetSphereReach(SubroutineBase attack, out float reach)
        {
            var sphere = GetSphere(attack);
            reach = sphere.IsValid ? sphere.Offset + sphere.Radius + HitboxSlack : 0f;
            return sphere.IsValid;
        }

        /// <summary>Whether the attack sphere for the current camera pose contains <paramref name="aimPoint"/> (with hitbox slack).</summary>
        public static bool SphereContains(SubroutineBase attack, Vector3 cameraPosition, Vector3 cameraForward, Vector3 aimPoint)
        {
            var sphere = GetSphere(attack);
            if (!sphere.IsValid)
            {
                return false;
            }

            var center = cameraPosition + cameraForward * sphere.Offset;
            var reach = sphere.Radius + HitboxSlack;
            return (aimPoint - center).sqrMagnitude <= reach * reach;
        }

        /// <summary>SCP-106 attack reach between the two player origins; 0 when unreadable.</summary>
        public static float GetScp106Range(Scp106Attack attack)
        {
            var geometry = GetScp106(attack);
            return geometry != null ? Mathf.Sqrt(geometry.MaxRangeSqr) : 0f;
        }

        /// <summary>
        /// Mirrors <c>Scp106Attack.VerifyShot</c> for the current pose: range, an unobstructed line
        /// between the origins, and the horizontal facing dot required at that distance.
        /// </summary>
        public static bool Scp106WouldHit(Scp106Attack attack, Vector3 ownerPosition, Vector3 cameraForward, Vector3 targetPosition)
        {
            var geometry = GetScp106(attack);
            if (geometry == null)
            {
                return false;
            }

            var offset = targetPosition - ownerPosition;
            var sqrDistance = offset.sqrMagnitude;
            if (sqrDistance > geometry.MaxRangeSqr)
            {
                return false;
            }

            var forward = cameraForward;
            forward.y = 0f;
            offset.y = 0f;
            if (forward.sqrMagnitude < 1e-6f || offset.sqrMagnitude < 1e-6f)
            {
                return true;
            }

            if (geometry.DotOverDistance != null
                && geometry.DotOverDistance.Evaluate(sqrDistance) > Vector3.Dot(offset.normalized, forward.normalized))
            {
                return false;
            }

            return !Physics.Linecast(ownerPosition, targetPosition, PlayerRolesUtils.AttackMask);
        }

        private static Sphere GetSphere(SubroutineBase attack)
        {
            if (attack == null)
            {
                return default;
            }

            var type = attack.GetType();
            if (Spheres.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var sphere = attack is Scp096AttackAbility
                ? ReadSphere(attack, "_sphereHitboxOffset", "_sphereHitboxRadius")
                : ReadSphere(attack, "_detectionOffset", "_detectionRadius");
            Spheres[type] = sphere;
            LabLogger.Info($"[BotCombat] REACH attack={type.Name} offset={sphere.Offset:F2} radius={sphere.Radius:F2} reach={(sphere.IsValid ? sphere.Offset + sphere.Radius : 0f):F2}");
            return sphere;
        }

        private static Sphere ReadSphere(SubroutineBase attack, string offsetField, string radiusField)
        {
            return TryReadField(attack, offsetField, out float offset) && TryReadField(attack, radiusField, out float radius)
                ? new Sphere(offset, radius)
                : default;
        }

        private static Scp106Geometry GetScp106(Scp106Attack attack)
        {
            if (attack == null)
            {
                return null;
            }

            var type = attack.GetType();
            if (Scp106Attacks.TryGetValue(type, out var cached))
            {
                return cached;
            }

            Scp106Geometry geometry = null;
            if (TryReadField(attack, "_maxRangeSqr", out float maxRangeSqr) && maxRangeSqr > 0f)
            {
                TryReadField(attack, "_dotOverDistance", out AnimationCurve curve);
                geometry = new Scp106Geometry { MaxRangeSqr = maxRangeSqr, DotOverDistance = curve };
                LabLogger.Info($"[BotCombat] REACH attack={type.Name} range={Mathf.Sqrt(maxRangeSqr):F2} dot@0={Evaluate(curve, 0f)} dot@max={Evaluate(curve, maxRangeSqr)}");
            }
            else
            {
                LabLogger.Warn($"[BotCombat] REACH attack={type.Name} unreadable; SCP-106 attacks are disabled");
            }

            Scp106Attacks[type] = geometry;
            return geometry;
        }

        private static string Evaluate(AnimationCurve curve, float sqrDistance)
        {
            return curve != null ? curve.Evaluate(sqrDistance).ToString("F2") : "none";
        }

        private static bool TryReadField<T>(object instance, string name, out T value)
        {
            for (var type = instance.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, InstanceFields | BindingFlags.DeclaredOnly);
                if (field != null && field.GetValue(instance) is T typed)
                {
                    value = typed;
                    return true;
                }
            }

            value = default;
            return false;
        }
    }
}
