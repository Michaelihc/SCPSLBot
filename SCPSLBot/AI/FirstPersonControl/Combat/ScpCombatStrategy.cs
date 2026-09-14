using PlayerRoles;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    /// <summary>
    /// Dispatches SCP combat into general, SCP-096, and SCP-173 role strategies.
    /// </summary>
    internal sealed class ScpCombatStrategy
    {
        private const float AttackCooldown = 0.85f;
        private const float DamageStrafeSeconds = 1f;

        private readonly FpcBotCombat combat;
        private readonly StandardScpCombatStrategy standardStrategy;
        private readonly Scp096CombatStrategy scp096Strategy;
        private readonly Scp173CombatStrategy scp173Strategy;

        private float damageStrafeUntil;

        public ScpCombatStrategy(FpcBotCombat combat, CombatTargetSelector targetSelector)
        {
            this.combat = combat;
            scp096Strategy = new Scp096CombatStrategy(combat, targetSelector);
            scp173Strategy = new Scp173CombatStrategy(combat, this);
            standardStrategy = new StandardScpCombatStrategy(combat, scp096Strategy);
        }

        internal float NextAttackTime
        {
            get => combat.NextShotTime;
            set => combat.NextShotTime = value;
        }

        internal float AttackInterval => AttackCooldown;

        public void NotifyDamaged()
        {
            damageStrafeUntil = Time.time + DamageStrafeSeconds;
        }

        public void PrepareForTick(RoleTypeId role)
        {
            scp096Strategy.EndRageIfExpired(role);
            scp173Strategy.ReleaseHeldBlinkIfNeeded(role);
        }

        public bool TrySelectPriorityTarget(RoleTypeId role, out CombatTarget target)
        {
            return scp096Strategy.TrySelectRageTarget(role, out target);
        }

        public void ClearPriorityTarget()
        {
            scp096Strategy.ClearRageTarget();
        }

        public void ReleaseHeldInputsIfNeeded()
        {
            scp096Strategy.ReleaseHeldRageKeyIfNeeded();
        }

        public void Run(CombatTarget target, RoleTypeId role)
        {
            var targetPosition = target.Hub.transform.position;
            combat.MoveToCombatPosition(targetPosition);
            combat.OpenSurfaceDoorTowardTarget(target.Hub);
            ApplyDamageStrafe(targetPosition);

            var aimPoint = GetAimPoint(target);
            combat.BotPlayer.LookToPosition(aimPoint);

            if (role == RoleTypeId.Scp173)
            {
                scp173Strategy.Run(target);
                return;
            }

            standardStrategy.TryUseChaseAbility(role);
            if (standardStrategy.TryUseStrictAttack(role, target, this))
            {
                return;
            }

            var attackRange = standardStrategy.GetAttackRange(role);
            var closeScp096Target = role == RoleTypeId.Scp096 && target.Distance <= attackRange;
            if (!target.HasLineOfSight && !closeScp096Target)
            {
                return;
            }

            if (role == RoleTypeId.Scp096 && !scp096Strategy.IsReadyToAttack(target.Hub))
            {
                return;
            }

            var maxAttackAimAngle = closeScp096Target ? 180f : 35f;
            if (target.Distance > attackRange
                || Time.time < NextAttackTime
                || !combat.IsAimedAt(aimPoint, maxAttackAimAngle))
            {
                return;
            }

            NextAttackTime = Time.time + AttackCooldown;
            if (!standardStrategy.TryUseAttack(role, target.Hub) && role == RoleTypeId.Scp096)
            {
                scp096Strategy.LogDebug("attack dummy action failed or was unavailable.");
            }
        }

        internal static Vector3 GetAimPoint(CombatTarget target)
        {
            var bodyBase = target.Hub.transform.position;
            var camera = target.Hub.PlayerCameraReference;
            var head = camera != null ? camera.position : bodyBase + Vector3.up * 1.65f;
            return Vector3.Lerp(bodyBase, head, 0.55f);
        }

        private void ApplyDamageStrafe(Vector3 targetPosition)
        {
            if (Time.time >= damageStrafeUntil)
            {
                return;
            }

            var botPlayer = combat.BotPlayer;
            var toTarget = Vector3.ProjectOnPlane(targetPosition - botPlayer.PlayerPosition, Vector3.up);
            if (toTarget.sqrMagnitude < 0.01f)
            {
                return;
            }

            var forward = toTarget.normalized;
            var right = Vector3.Cross(Vector3.up, forward).normalized * combat.StrafeDirection;
            var currentWorldMove = Vector3.ProjectOnPlane(
                botPlayer.FpcRole.FpcModule.transform.TransformDirection(botPlayer.Move.DesiredLocalDirection),
                Vector3.up);

            if (currentWorldMove.sqrMagnitude < 0.01f)
            {
                currentWorldMove = forward;
            }

            var worldMove = Vector3.Normalize(currentWorldMove.normalized + right * FpcBotCombat.CurrentSettings.StrafeSpeed);
            botPlayer.Move.DesiredLocalDirection = botPlayer.FpcRole.FpcModule.transform.InverseTransformDirection(worldMove);
        }
    }
}
