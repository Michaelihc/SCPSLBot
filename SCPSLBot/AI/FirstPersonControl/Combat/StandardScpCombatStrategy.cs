using PlayerRoles;
using PlayerRoles.PlayableScps.Scp049.Zombies;
using PlayerRoles.PlayableScps.Scp096;
using PlayerRoles.PlayableScps.Scp3114;
using PlayerRoles.PlayableScps.Scp939;
using PlayerRoles.Subroutines;
using SCPSLBot.AI.FirstPersonControl.Combat.ScpAbilities;
using System;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    /// <summary>
    /// Dispatches the standard SCP chase and attack actions for roles without a full bespoke strategy.
    /// </summary>
    internal sealed class StandardScpCombatStrategy
    {
        // Used only when a role's native attack geometry cannot be read.
        private const float FallbackAttackRange = 3.2f;
        private const float FallbackAttackAimAngle = 35f;
        private const float Scp106RetrySeconds = 0.2f;

        private static readonly string[] Scp939AttackActions =
        {
            "Scp939ClawAbility:Shoot->Click",
        };

        private static readonly string[] Scp3114AttackActions =
        {
            "Scp3114Slap:Shoot->Click",
        };

        private static readonly string[] Scp096AttackActions =
        {
            "Scp096AttackAbility:Shoot->Click",
        };

        private static readonly string[] Scp0492AttackActions =
        {
            "ZombieAttackAbility:Shoot->Click",
            "Shoot->Click",
        };

        private readonly FpcBotCombat combat;
        private readonly Scp096CombatStrategy scp096Strategy;
        private readonly Scp049CombatAbilities scp049;
        private readonly Scp106CombatAbilities scp106;
        private readonly Scp939CombatAbilities scp939;
        private readonly Scp3114CombatAbilities scp3114;

        internal Scp939CombatAbilities Scp939 => scp939;

        public StandardScpCombatStrategy(FpcBotCombat combat, Scp096CombatStrategy scp096Strategy)
        {
            this.combat = combat;
            this.scp096Strategy = scp096Strategy;
            scp049 = new Scp049CombatAbilities(combat);
            scp106 = new Scp106CombatAbilities(combat);
            scp939 = new Scp939CombatAbilities(combat);
            scp3114 = new Scp3114CombatAbilities(combat);
        }

        /// <summary>Camera-to-target distance at which the role's native attack connects.</summary>
        public float GetAttackRange(RoleTypeId role)
        {
            return TryGetSphereAttack(role, out var attack) && ScpAttackReach.TryGetSphereReach(attack, out var reach)
                ? reach
                : FallbackAttackRange;
        }

        /// <summary>
        /// Whether a swing from the current camera pose would connect: the native attack sphere
        /// ahead of the camera contains the aim point. Long-reach roles swing as soon as the target
        /// is inside their real reach instead of closing to a fixed short range.
        /// </summary>
        public bool CanHit(RoleTypeId role, CombatTarget target, Vector3 aimPoint)
        {
            var botPlayer = combat.BotPlayer;
            if (TryGetSphereAttack(role, out var attack) && ScpAttackReach.TryGetSphereReach(attack, out _))
            {
                return ScpAttackReach.SphereContains(attack, botPlayer.CameraPosition, botPlayer.CameraForward, aimPoint);
            }

            return target.Distance <= FallbackAttackRange && combat.IsAimedAt(aimPoint, FallbackAttackAimAngle);
        }

        private bool TryGetSphereAttack(RoleTypeId role, out SubroutineBase attack)
        {
            var abilities = combat.Abilities;
            attack = null;
            switch (role)
            {
                case RoleTypeId.Scp939 when abilities.TryGet<Scp939ClawAbility>(out var claw):
                    attack = claw;
                    break;
                case RoleTypeId.Scp3114 when abilities.TryGet<Scp3114Slap>(out var slap):
                    attack = slap;
                    break;
                case RoleTypeId.Scp0492 when abilities.TryGet<ZombieAttackAbility>(out var zombie):
                    attack = zombie;
                    break;
                case RoleTypeId.Scp096 when abilities.TryGet<Scp096AttackAbility>(out var slaps):
                    attack = slaps;
                    break;
            }

            return attack != null;
        }

        /// <summary>
        /// Runs the role's chase abilities. Returns true when the role spent this tick (focusing,
        /// disguised approach, strangling) and the regular attack step must be skipped.
        /// </summary>
        public bool TryUseChaseAbility(RoleTypeId role, CombatTarget target, ScpCombatStrategy coordinator)
        {
            switch (role)
            {
                case RoleTypeId.Scp049:
                    scp049.TickChase(target);
                    return false;
                case RoleTypeId.Scp096:
                    scp096Strategy.TryUseChargeAbility(target);
                    return false;
                case RoleTypeId.Scp106:
                    scp106.TickChase(target);
                    return false;
                case RoleTypeId.Scp939:
                    return scp939.Tick(target);
                case RoleTypeId.Scp3114:
                    return scp3114.Tick(target);
                default:
                    return false;
            }
        }

        public void ResetHeldAbilities(RoleTypeId role)
        {
            scp939.ResetIfNotRole(role);
        }

        public void ReleaseHeldAbilities()
        {
            scp939.ReleaseHeld();
        }

        /// <summary>
        /// Roles whose attack is a direct command with its own validation (SCP-049, SCP-106).
        /// Returns true when the role was handled here and the shared swing step must be skipped.
        /// </summary>
        public bool TryUseStrictAttack(RoleTypeId role, CombatTarget target, ScpCombatStrategy coordinator)
        {
            switch (role)
            {
                case RoleTypeId.Scp049:
                    return scp049.TryAttack(target);
                case RoleTypeId.Scp106:
                    if (Time.time >= coordinator.NextAttackTime && scp106.TryAttack(target.Hub))
                    {
                        // The native hit/miss cooldown paces real swings; this only spaces retries.
                        coordinator.NextAttackTime = Time.time + Scp106RetrySeconds;
                    }

                    return true;
                default:
                    return false;
            }
        }

        public bool TryUseAttack(RoleTypeId role, ReferenceHub target = null)
        {
            if (role == RoleTypeId.Scp096 && scp096Strategy.TryUseNativeAttack(target))
            {
                return true;
            }

            var actions = role switch
            {
                RoleTypeId.Scp0492 => Scp0492AttackActions,
                RoleTypeId.Scp096 => Scp096AttackActions,
                RoleTypeId.Scp3114 => Scp3114AttackActions,
                RoleTypeId.Scp939 => Scp939AttackActions,
                _ => Array.Empty<string>(),
            };

            return combat.TryClickFirstDummyAction(actions);
        }
    }
}
