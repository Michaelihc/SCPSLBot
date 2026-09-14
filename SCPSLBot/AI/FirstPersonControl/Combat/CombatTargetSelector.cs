using MapGeneration;
using PlayerRoles;
using UnityEngine;
using SCPSLBot.Warmup;

namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    internal sealed class CombatTarget
    {
        public CombatTarget(ReferenceHub hub, float distance, Vector3 aimPoint, bool hasLineOfSight)
        {
            Hub = hub;
            Distance = distance;
            AimPoint = aimPoint;
            HasLineOfSight = hasLineOfSight;
        }

        public ReferenceHub Hub { get; }
        public float Distance { get; }
        public Vector3 AimPoint { get; }
        public bool HasLineOfSight { get; }
    }

    /// <summary>
    /// Owns hostile classification, line-of-sight probing, and sticky target memory.
    /// </summary>
    internal sealed class CombatTargetSelector
    {
        private const float MaxTargetDistance = 36f;
        private const float SurfaceTargetDistance = 1000f;
        private const float TargetSwitchLockSeconds = 2f;
        private const float TargetSwitchDistanceRatio = 0.65f;

        private static readonly int CombatVisionMask =
            LayerMask.GetMask("Default", "Door", "InteractableNoPlayerCollision", "Glass");

        private readonly FpcBotPlayer botPlayer;
        private float currentTargetChaseUntil;
        private float currentTargetSelectedTime;

        public CombatTargetSelector(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        public ReferenceHub CurrentTarget { get; private set; }

        public bool NotifyDamagedBy(ReferenceHub attacker, float chaseSeconds)
        {
            var botHub = botPlayer.BotHub.PlayerHub;
            if (botHub.roleManager.CurrentRole.Team != Team.SCPs || !AreHostile(botHub, attacker))
            {
                return false;
            }

            CurrentTarget = attacker;
            currentTargetSelectedTime = Time.time;
            currentTargetChaseUntil = Time.time + chaseSeconds;
            return true;
        }

        public void SetCurrentTarget(ReferenceHub target)
        {
            CurrentTarget = target;
            currentTargetSelectedTime = Time.time;
        }

        public void ExtendChase(float chaseSeconds)
        {
            currentTargetChaseUntil = Time.time + chaseSeconds;
        }

        public void ExpireChase()
        {
            currentTargetChaseUntil = 0f;
        }

        public void Clear()
        {
            CurrentTarget = null;
            currentTargetSelectedTime = 0f;
        }

        public bool TrySelectVisibleTarget(out CombatTarget target)
        {
            target = null;
            var botHub = botPlayer.BotHub.PlayerHub;
            var botPosition = botPlayer.PlayerPosition;

            CombatTarget best = null;
            CombatTarget currentVisible = null;
            foreach (var candidate in CombatWorldSnapshot.Get(Time.time))
            {
                if (!AreHostile(botHub, candidate))
                {
                    continue;
                }

                var distance = Vector3.Distance(botPosition, candidate.Position);
                if (distance > MaxTargetDistance)
                {
                    continue;
                }

                var selection = BuildTarget(candidate.Hub, distance);
                if (selection == null)
                {
                    continue;
                }

                if (candidate.Hub == CurrentTarget)
                {
                    currentVisible = selection;
                }

                if (best == null || selection.Distance < best.Distance)
                {
                    best = selection;
                }
            }

            target = SelectStickyVisibleTarget(best, currentVisible);
            return target != null;
        }

        private CombatTarget SelectStickyVisibleTarget(CombatTarget best, CombatTarget currentVisible)
        {
            if (best == null || currentVisible == null || best.Hub == CurrentTarget)
            {
                return best;
            }

            if (Time.time - currentTargetSelectedTime < TargetSwitchLockSeconds)
            {
                return currentVisible;
            }

            return best.Distance <= currentVisible.Distance * TargetSwitchDistanceRatio
                ? best
                : currentVisible;
        }

        public bool TrySelectSurfaceTarget(out CombatTarget target)
        {
            target = null;
            var botHub = botPlayer.BotHub.PlayerHub;
            if (!IsOnSurface(botPlayer.PlayerPosition))
            {
                return false;
            }

            CombatTarget best = null;
            foreach (var candidate in CombatWorldSnapshot.Get(Time.time))
            {
                if (!AreHostile(botHub, candidate) || !candidate.IsOnSurface)
                {
                    continue;
                }

                var distance = Vector3.Distance(botPlayer.PlayerPosition, candidate.Position);
                if (distance > SurfaceTargetDistance)
                {
                    continue;
                }

                var selection = BuildTarget(candidate.Hub, distance, false);
                if (best == null || selection.Distance < best.Distance)
                {
                    best = selection;
                }
            }

            target = best;
            return target != null;
        }

        public bool TrySelectRememberedTarget(out CombatTarget target)
        {
            target = null;
            if (CurrentTarget == null || Time.time > currentTargetChaseUntil)
            {
                return false;
            }

            if (!AreHostile(botPlayer.BotHub.PlayerHub, CurrentTarget))
            {
                return false;
            }

            var distance = Vector3.Distance(botPlayer.PlayerPosition, CurrentTarget.transform.position);
            if (distance > MaxTargetDistance * 1.5f)
            {
                return false;
            }

            target = BuildTarget(CurrentTarget, distance, false);
            return true;
        }

        public CombatTarget BuildTarget(ReferenceHub hub, float distance, bool requireLineOfSight = true)
        {
            var bodyBase = hub.transform.position;
            var camera = hub.PlayerCameraReference;
            var headAimPoint = camera != null ? camera.position : bodyBase + Vector3.up * 1.65f;
            var torsoAimPoint = Vector3.Lerp(bodyBase, headAimPoint, 0.55f);

            if (!requireLineOfSight)
            {
                return new CombatTarget(hub, distance, torsoAimPoint, false);
            }

            if (HasLineOfSight(botPlayer.CameraPosition, headAimPoint))
            {
                return new CombatTarget(hub, distance, headAimPoint, true);
            }

            if (HasLineOfSight(botPlayer.CameraPosition, torsoAimPoint))
            {
                return new CombatTarget(hub, distance, torsoAimPoint, true);
            }

            return null;
        }

        public static bool AreHostile(ReferenceHub bot, ReferenceHub candidate)
        {
            if (candidate == null || candidate == bot || !IsCombatTarget(candidate) || !IsCombatTarget(bot))
            {
                return false;
            }

            if (!WarmupManager.Instance.CanHubsFightInWarmup(bot, candidate))
            {
                return false;
            }

            var botTeam = bot.roleManager.CurrentRole.Team;
            var candidateTeam = candidate.roleManager.CurrentRole.Team;
            if (botTeam == Team.SCPs)
            {
                return candidateTeam != Team.SCPs;
            }

            if (candidateTeam == Team.SCPs)
            {
                return true;
            }

            var botRole = bot.roleManager.CurrentRole.RoleTypeId;
            var candidateRole = candidate.roleManager.CurrentRole.RoleTypeId;
            if (IsFoundationHumanRole(botRole) && IsFoundationHumanRole(candidateRole))
            {
                return false;
            }

            if (IsChaosHumanRole(botRole) && IsChaosHumanRole(candidateRole))
            {
                return false;
            }

            return candidateTeam != botTeam;
        }

        private static bool AreHostile(ReferenceHub bot, in CombatWorldEntry candidate)
        {
            if (candidate.Hub == bot || !IsCombatTarget(bot) || !IsCombatTarget(candidate.Hub))
            {
                return false;
            }

            if (!WarmupManager.Instance.CanHubsFightInWarmup(bot, candidate.Hub))
            {
                return false;
            }

            var currentCandidateRole = candidate.Hub.roleManager.CurrentRole;
            if (currentCandidateRole.RoleTypeId != candidate.Role || currentCandidateRole.Team != candidate.Team)
            {
                return AreHostile(bot, candidate.Hub);
            }

            var botRole = bot.roleManager.CurrentRole.RoleTypeId;
            var botTeam = bot.roleManager.CurrentRole.Team;
            if (botTeam == Team.SCPs)
            {
                return candidate.Team != Team.SCPs;
            }

            if (candidate.Team == Team.SCPs)
            {
                return true;
            }

            if (IsFoundationHumanRole(botRole) && IsFoundationHumanRole(candidate.Role))
            {
                return false;
            }

            if (IsChaosHumanRole(botRole) && IsChaosHumanRole(candidate.Role))
            {
                return false;
            }

            return candidate.Team != botTeam;
        }

        public static bool IsOnSurface(Vector3 position)
        {
            return RoomUtils.TryGetRoom(position, out var room) && room.Zone == FacilityZone.Surface;
        }

        private static bool IsCombatTarget(ReferenceHub hub)
        {
            if (hub == null)
            {
                return false;
            }

            var role = hub.roleManager.CurrentRole;
            return role.RoleTypeId != RoleTypeId.None
                   && role.RoleTypeId != RoleTypeId.Spectator
                   && role.Team != Team.Dead
                   && hub.IsAlive();
        }

        private static bool IsFoundationHumanRole(RoleTypeId role)
        {
            return role is RoleTypeId.NtfCaptain
                or RoleTypeId.NtfPrivate
                or RoleTypeId.NtfSergeant
                or RoleTypeId.NtfSpecialist
                or RoleTypeId.FacilityGuard
                or RoleTypeId.Scientist;
        }

        private static bool IsChaosHumanRole(RoleTypeId role)
        {
            return role is RoleTypeId.ChaosConscript
                or RoleTypeId.ChaosMarauder
                or RoleTypeId.ChaosRepressor
                or RoleTypeId.ChaosRifleman
                or RoleTypeId.ClassD;
        }

        private static bool HasLineOfSight(Vector3 origin, Vector3 aimPoint)
        {
            var direction = aimPoint - origin;
            if (direction.sqrMagnitude < 0.0001f)
            {
                return true;
            }

            // Environment-only linecast retains the allocation-free hot path from the original combat loop.
            return !Physics.Linecast(origin, aimPoint, CombatVisionMask, QueryTriggerInteraction.Ignore);
        }
    }
}
