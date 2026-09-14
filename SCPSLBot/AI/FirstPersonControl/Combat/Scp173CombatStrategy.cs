using PlayerRoles;
using PlayerRoles.PlayableScps.Scp173;
using PlayerRoles.Subroutines;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    /// <summary>
    /// Owns SCP-173 snap, tantrum, breakneck, and held-blink state transitions.
    /// </summary>
    internal sealed class Scp173CombatStrategy
    {
        private const float SnapRange = 1.45f;
        private const float BlinkRange = 8f;
        private const float BreakneckBlinkRange = 14.4f;
        private const float BlinkHoldSeconds = 0.28f;
        private const float BlinkAimAngle = 12f;
        private const float BreakneckChaseDistance = 9f;
        private const float StrictAttackAimAngle = 14f;

        private static readonly string[] SnapActions =
        {
            "Scp173SnapAbility:Shoot->Click",
            "Shoot->Click",
        };

        private static readonly string[] TantrumActions =
        {
            "Scp173TantrumAbility:ToggleFlashlight->Click",
        };

        private static readonly string[] BreakneckActions =
        {
            "Scp173BreakneckSpeedsAbility:Run->Click",
        };

        private readonly FpcBotCombat combat;
        private readonly ScpCombatStrategy coordinator;

        private bool blinkHeld;
        private bool breakneckLikelyActive;
        private Vector3 heldBlinkAimPoint;
        private float blinkReleaseTime;
        private float breakneckDisableAllowedTime;

        public Scp173CombatStrategy(FpcBotCombat combat, ScpCombatStrategy coordinator)
        {
            this.combat = combat;
            this.coordinator = coordinator;
        }

        public void Run(CombatTarget target)
        {
            var observed = IsObserved();
            var breakneckActive = IsBreakneckActive();
            breakneckLikelyActive = breakneckActive
                                    || breakneckLikelyActive && Time.time < breakneckDisableAllowedTime + 12f;

            if (blinkHeld)
            {
                combat.BotPlayer.LookToPosition(heldBlinkAimPoint);
                return;
            }

            if (target.Distance <= 3f && !observed)
            {
                combat.TryClickFirstDummyAction(TantrumActions);
            }

            if (breakneckActive
                && target.Distance <= SnapRange + 0.4f
                && Time.time >= breakneckDisableAllowedTime)
            {
                if (combat.TryClickFirstDummyAction(BreakneckActions))
                {
                    breakneckLikelyActive = false;
                }

                return;
            }

            if (target.Distance <= SnapRange)
            {
                var aimPoint = ScpCombatStrategy.GetAimPoint(target);
                combat.BotPlayer.LookToPosition(aimPoint);
                if (!observed
                    && !breakneckActive
                    && Time.time >= coordinator.NextAttackTime
                    && combat.IsAimedAt(aimPoint, StrictAttackAimAngle))
                {
                    coordinator.NextAttackTime = Time.time + coordinator.AttackInterval;
                    combat.TryClickFirstDummyAction(SnapActions);
                }

                return;
            }

            if (target.Distance > BreakneckChaseDistance
                && !breakneckActive
                && TryGetBreakneck(out var breakneck)
                && breakneck.Cooldown.IsReady)
            {
                if (combat.TryClickFirstDummyAction(BreakneckActions))
                {
                    breakneckLikelyActive = true;
                    breakneckDisableAllowedTime = Time.time + 1.2f;
                }
            }

            var blinkRange = breakneckActive || breakneckLikelyActive ? BreakneckBlinkRange : BlinkRange;
            var shouldBlinkForward = target.Distance > blinkRange;
            if ((!target.HasLineOfSight && !shouldBlinkForward) || !IsBlinkReady())
            {
                combat.BotPlayer.LookToPosition(ScpCombatStrategy.GetAimPoint(target));
                return;
            }

            var blinkAimPoint = shouldBlinkForward
                ? GetForwardBlinkAimPoint(target, blinkRange)
                : GetBlinkAimPoint(target);
            combat.BotPlayer.LookToPosition(blinkAimPoint);
            if (!combat.IsAimedAt(blinkAimPoint, BlinkAimAngle))
            {
                return;
            }

            if (combat.TryClickGroupedDummyAction("Scp173TeleportAbility", "Zoom->Hold"))
            {
                blinkHeld = true;
                heldBlinkAimPoint = blinkAimPoint;
                blinkReleaseTime = Time.time + BlinkHoldSeconds;
            }
        }

        public void ReleaseHeldBlinkIfNeeded(RoleTypeId role)
        {
            if (!blinkHeld)
            {
                return;
            }

            if (role != RoleTypeId.Scp173)
            {
                blinkHeld = false;
                heldBlinkAimPoint = default;
                blinkReleaseTime = 0f;
                return;
            }

            if (Time.time < blinkReleaseTime)
            {
                return;
            }

            combat.TryClickGroupedDummyAction("Scp173TeleportAbility", "Zoom->Release");
            blinkHeld = false;
            heldBlinkAimPoint = default;
            blinkReleaseTime = 0f;
        }

        private Vector3 GetBlinkAimPoint(CombatTarget target)
        {
            var targetPosition = target.Hub.transform.position;
            var awayFromTarget = Vector3.ProjectOnPlane(combat.BotPlayer.PlayerPosition - targetPosition, Vector3.up);
            if (awayFromTarget.sqrMagnitude < 0.01f)
            {
                awayFromTarget = -combat.BotPlayer.PlayerForward;
            }

            return targetPosition + awayFromTarget.normalized * 0.55f + Vector3.up * 0.15f;
        }

        private Vector3 GetForwardBlinkAimPoint(CombatTarget target, float blinkRange)
        {
            var direction = Vector3.ProjectOnPlane(
                target.Hub.transform.position - combat.BotPlayer.PlayerPosition,
                Vector3.up);
            if (direction.sqrMagnitude < 0.01f)
            {
                direction = Vector3.ProjectOnPlane(combat.BotPlayer.PlayerForward, Vector3.up);
            }

            var forwardDistance = Mathf.Max(SnapRange + 0.5f, blinkRange + 2.25f);
            return combat.BotPlayer.PlayerPosition + direction.normalized * forwardDistance + Vector3.up * 0.2f;
        }

        private bool IsObserved()
        {
            return TryGetSubroutine<Scp173ObserversTracker>(out var observers) && observers.IsObserved;
        }

        private bool IsBlinkReady()
        {
            return TryGetSubroutine<Scp173BlinkTimer>(out var blinkTimer) && blinkTimer.AbilityReady;
        }

        private bool IsBreakneckActive()
        {
            return TryGetBreakneck(out var breakneck) && breakneck.IsActive;
        }

        private bool TryGetBreakneck(out Scp173BreakneckSpeedsAbility breakneck)
        {
            return TryGetSubroutine(out breakneck);
        }

        private bool TryGetSubroutine<T>(out T subroutine) where T : SubroutineBase
        {
            if (combat.BotPlayer.BotHub.PlayerHub.roleManager.CurrentRole is Scp173Role scp173Role)
            {
                return scp173Role.SubroutineModule.TryGetSubroutine(out subroutine);
            }

            subroutine = null;
            return false;
        }
    }
}
