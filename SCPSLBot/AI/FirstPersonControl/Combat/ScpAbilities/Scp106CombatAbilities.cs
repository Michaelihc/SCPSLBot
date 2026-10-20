using MapGeneration;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.PlayableScps.Scp106;
using PlayerStatsSystem;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat.ScpAbilities
{
    /// <summary>
    /// SCP-106 attack, Stalk and Hunter's Atlas as direct commands. Stalk crosses distance while
    /// hidden and damage-protected; the native attack pockets a target that is already corroding.
    /// </summary>
    internal sealed class Scp106CombatAbilities
    {
        private const float DecisionIntervalSeconds = 0.3f;
        private const float AtlasRetrySeconds = 4f;

        private const float StalkMinDistance = 24f;
        private const float StalkEmergeDistance = 9f;
        private const float StalkEmergeDistanceHurt = 5f;

        // Native start threshold is 0.35; approaching from afar needs enough left to drain on the way.
        private const float StalkStartVigor = 0.6f;
        private const float StalkEscapeVigor = 0.35f;
        private const float StalkMinimumVigor = 0.08f;

        private const float HurtHealthFraction = 0.35f;
        private const float HurtStalkMaxDistance = 16f;

        private const float AtlasMinDistance = 30f;
        private const float AtlasVigorReserve = 0.25f;
        private const float AtlasBaseCost = 0.15f;
        private const float AtlasCostPerMeter = 0.015f;

        private readonly FpcBotCombat combat;

        private float nextDecisionTime;
        private float nextAtlasTime;

        public Scp106CombatAbilities(FpcBotCombat combat)
        {
            this.combat = combat;
        }

        public void TickChase(CombatTarget target)
        {
            var now = Time.time;
            if (now < nextDecisionTime)
            {
                return;
            }

            nextDecisionTime = now + DecisionIntervalSeconds;
            var abilities = combat.Abilities;
            if (abilities.Role is not Scp106Role role
                || !abilities.TryGet<Scp106StalkAbility>(out var stalk))
            {
                return;
            }

            var sinkhole = role.Sinkhole;
            if (sinkhole == null || sinkhole.IsDuringAnimation)
            {
                return;
            }

            var hub = abilities.Hub;
            var vigor = hub.playerStats.TryGetModule<VigorStat>(out var vigorStat) ? vigorStat.CurValue : 0f;
            var hurt = hub.playerStats.GetModule<HealthStat>().NormalizedValue < HurtHealthFraction;

            if (stalk.StalkActive)
            {
                var emergeDistance = hurt ? StalkEmergeDistanceHurt : StalkEmergeDistance;
                if ((target.Distance <= emergeDistance || vigor <= StalkMinimumVigor)
                    && sinkhole.ReadonlyCooldown.IsReady)
                {
                    ScpAbilityCommands.Send(stalk);
                }

                return;
            }

            if (!sinkhole.ReadonlyCooldown.IsReady || !role.FpcModule.IsGrounded || sinkhole.SubmergeProgress > 0f)
            {
                return;
            }

            // Submerged, 106 ignores most damage: use it to survive while a hurt 106 closes the gap.
            if (hurt && target.Distance <= HurtStalkMaxDistance && vigor >= StalkEscapeVigor)
            {
                ScpAbilityCommands.Send(stalk);
                return;
            }

            if (target.Distance >= AtlasMinDistance
                && !target.HasLineOfSight
                && now >= nextAtlasTime
                && TryUseAtlas(abilities, target, vigor))
            {
                return;
            }

            if (target.Distance >= StalkMinDistance && vigor >= StalkStartVigor)
            {
                ScpAbilityCommands.Send(stalk);
            }
        }

        private bool TryUseAtlas(ScpAbilitySet abilities, CombatTarget target, float vigor)
        {
            if (!abilities.TryGet<Scp106HuntersAtlasAbility>(out var atlas))
            {
                return false;
            }

            // Native cost: distance * 0.015 is required up front, then the real cost plus a flat 0.15.
            var travelCost = target.Distance * AtlasCostPerMeter;
            if (travelCost + AtlasBaseCost + AtlasVigorReserve > vigor)
            {
                return false;
            }

            var destination = target.Hub.transform.position;
            if (!RoomUtils.TryGetRoom(destination, out var room))
            {
                return false;
            }

            nextAtlasTime = Time.time + AtlasRetrySeconds;
            return ScpAbilityCommands.SendAtlasDestination(atlas, room.transform, destination);
        }

        /// <summary>
        /// Native 106 attack: corrodes on the first hit and pockets a corroding target on the next.
        /// Native validation covers range, line of sight, aim and cooldown.
        /// </summary>
        public bool TryAttack(ReferenceHub targetHub)
        {
            var abilities = combat.Abilities;
            if (abilities.Role is not Scp106Role role
                || !abilities.TryGet<Scp106Attack>(out var attack)
                || role.Sinkhole.SubmergeProgress > 0f
                || targetHub.roleManager.CurrentRole is not IFpcRole targetRole)
            {
                return false;
            }

            var hub = abilities.Hub;
            return ScpAbilityCommands.SendTargetedStrike(
                attack,
                targetHub,
                targetRole.FpcModule.Position,
                hub.PlayerCameraReference.rotation,
                role.FpcModule.Position);
        }
    }
}
