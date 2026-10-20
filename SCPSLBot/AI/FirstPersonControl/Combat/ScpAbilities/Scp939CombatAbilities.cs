using PlayerRoles;
using PlayerRoles.PlayableScps.Scp939;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat.ScpAbilities
{
    /// <summary>
    /// SCP-939 focus, lunge and amnestic cloud. The focus key and the cloud toggle are submitted as
    /// direct commands (the native key subroutines only send on a key change, so a held value
    /// persists); the lunge itself goes through the dummy Shoot action because it needs the motor's
    /// lunge request, and the motor reports the hit through the native hit command.
    /// </summary>
    internal sealed class Scp939CombatAbilities
    {
        private const float FocusMinDistance = 4f;
        private const float FocusMaxDistance = 8.5f;
        private const float FocusAimAngle = 7f;
        private const float LungeAimAngle = 15f;
        private const float FocusMaxHoldSeconds = 3f;
        private const float FocusAbortDistance = 12f;
        private const float FocusLostSightSeconds = 0.5f;
        private const float FocusRetrySeconds = 3f;
        private const float LungeRequestRetrySeconds = 0.3f;

        private const float CloudMaxTargetDistance = 10f;
        private const float CloudGroupRadius = 9f;
        private const int CloudGroupSize = 2;
        private const float CloudHoldSeconds = 2.5f;
        private const float CloudRetrySeconds = 6f;

        private readonly FpcBotCombat combat;

        private bool focusHeld;
        private bool cloudHeld;
        private float focusStartedTime;
        private float lastSightTime;
        private float nextFocusTime;
        private float nextLungeRequestTime;
        private float cloudReleaseTime;
        private float nextCloudTime;

        public Scp939CombatAbilities(FpcBotCombat combat)
        {
            this.combat = combat;
        }

        /// <summary>Drops any held input flags once the bot is no longer SCP-939 (native state resets with the role).</summary>
        public void ResetIfNotRole(RoleTypeId role)
        {
            if (role == RoleTypeId.Scp939 || (!focusHeld && !cloudHeld))
            {
                return;
            }

            focusHeld = false;
            cloudHeld = false;
        }

        /// <summary>Releases held inputs when combat ends, so a stuck cloud or focus cannot block the claw.</summary>
        public void ReleaseHeld()
        {
            if (!focusHeld && !cloudHeld)
            {
                return;
            }

            var abilities = combat.Abilities;
            if (focusHeld && abilities.TryGet<Scp939FocusKeySync>(out var focusKey))
            {
                ScpAbilityCommands.SendBool(focusKey, false);
            }

            if (cloudHeld && abilities.TryGet<Scp939AmnesticCloudAbility>(out var cloud))
            {
                ScpAbilityCommands.SendBool(cloud, false);
            }

            focusHeld = false;
            cloudHeld = false;
        }

        /// <summary>Returns true while focusing or recovering, so the caller skips claw attacks.</summary>
        public bool Tick(CombatTarget target, ScpCombatStrategy coordinator)
        {
            var abilities = combat.Abilities;
            if (!abilities.TryGet<Scp939FocusKeySync>(out var focusKey)
                || !abilities.TryGet<Scp939FocusAbility>(out var focus)
                || !abilities.TryGet<Scp939LungeAbility>(out var lunge)
                || !abilities.TryGet<Scp939ClawAbility>(out var claw)
                || !abilities.TryGet<Scp939AmnesticCloudAbility>(out var cloud))
            {
                return false;
            }

            var now = Time.time;
            var botPlayer = combat.BotPlayer;
            var aimPoint = ScpCombatStrategy.GetAimPoint(target);

            if (cloudHeld && now >= cloudReleaseTime)
            {
                cloudHeld = false;
                ScpAbilityCommands.SendBool(cloud, false);
            }

            if (focusHeld)
            {
                return TickFocus(target, focusKey, focus, lunge, aimPoint, now);
            }

            if (lunge.State == Scp939LungeState.Triggered)
            {
                // The motor moves a lunging dummy along its desired direction: keep driving forward.
                botPlayer.Move.DesiredLocalDirection = Vector3.forward;
                botPlayer.LookToPosition(aimPoint);
                return true;
            }

            if (focus.State > 0f || lunge.State != Scp939LungeState.None)
            {
                // Focus is decaying after a lunge or an abort; the native claw refuses while it lasts.
                return true;
            }

            if (!cloudHeld && now >= nextCloudTime && ShouldDeployCloud(target, coordinator, cloud, now))
            {
                cloudHeld = true;
                cloudReleaseTime = now + CloudHoldSeconds;
                nextCloudTime = now + CloudRetrySeconds;
                ScpAbilityCommands.SendBool(cloud, true);
                return false;
            }

            if (!cloudHeld && CanStartFocus(target, claw, cloud, lunge, now)
                && combat.IsAimedAt(aimPoint, FocusAimAngle))
            {
                focusHeld = true;
                focusStartedTime = now;
                lastSightTime = now;
                ScpAbilityCommands.SendBool(focusKey, true);
                botPlayer.Move.DesiredLocalDirection = Vector3.zero;
                return true;
            }

            return false;
        }

        private bool TickFocus(
            CombatTarget target,
            Scp939FocusKeySync focusKey,
            Scp939FocusAbility focus,
            Scp939LungeAbility lunge,
            Vector3 aimPoint,
            float now)
        {
            var botPlayer = combat.BotPlayer;
            if (target.HasLineOfSight)
            {
                lastSightTime = now;
            }

            if (lunge.State != Scp939LungeState.None)
            {
                // The lunge is in flight: release the key so focus does not restart after the landing.
                ReleaseFocus(focusKey, now, FocusRetrySeconds / 2f);
                botPlayer.Move.DesiredLocalDirection = Vector3.forward;
                botPlayer.LookToPosition(aimPoint);
                return true;
            }

            if (now - focusStartedTime > FocusMaxHoldSeconds
                || target.Distance > FocusAbortDistance
                || now - lastSightTime > FocusLostSightSeconds)
            {
                ReleaseFocus(focusKey, now, FocusRetrySeconds);
                return true;
            }

            // Focus slows the body and the lunge direction is the facing at focus start: stand and aim.
            botPlayer.Move.DesiredLocalDirection = Vector3.zero;
            botPlayer.LookToPosition(aimPoint);

            if (focus.State >= 1f
                && now >= nextLungeRequestTime
                && lunge.IsReady
                && combat.IsAimedAt(aimPoint, LungeAimAngle))
            {
                nextLungeRequestTime = now + LungeRequestRetrySeconds;
                combat.TryClickGroupedDummyAction("Scp939LungeAbility", "Shoot->Click");
            }

            return true;
        }

        private void ReleaseFocus(Scp939FocusKeySync focusKey, float now, float retrySeconds)
        {
            focusHeld = false;
            nextFocusTime = now + retrySeconds;
            ScpAbilityCommands.SendBool(focusKey, false);
        }

        private bool CanStartFocus(
            CombatTarget target,
            Scp939ClawAbility claw,
            Scp939AmnesticCloudAbility cloud,
            Scp939LungeAbility lunge,
            float now)
        {
            return now >= nextFocusTime
                   && target.HasLineOfSight
                   && target.Distance >= FocusMinDistance
                   && target.Distance <= FocusMaxDistance
                   && claw.Cooldown.IsReady
                   && !cloud.TargetState
                   && lunge.State == Scp939LungeState.None
                   && combat.BotPlayer.FpcRole.FpcModule.IsGrounded;
        }

        private bool ShouldDeployCloud(
            CombatTarget target,
            ScpCombatStrategy coordinator,
            Scp939AmnesticCloudAbility cloud,
            float now)
        {
            if (target.Distance > CloudMaxTargetDistance
                || !target.HasLineOfSight
                || !cloud.Cooldown.IsReady
                || cloud.TargetState)
            {
                return false;
            }

            if (!coordinator.IsUnderFire && CountEnemiesNear(now) < CloudGroupSize)
            {
                return false;
            }

            return cloud.ValidateFloor();
        }

        private int CountEnemiesNear(float now)
        {
            var position = combat.BotPlayer.PlayerPosition;
            var radiusSqr = CloudGroupRadius * CloudGroupRadius;
            var count = 0;
            foreach (var entry in CombatWorldSnapshot.Get(now))
            {
                if (entry.Team != Team.SCPs && (entry.Position - position).sqrMagnitude <= radiusSqr)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
