using PlayerRoles;
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
        private const float AttackRange = 3.2f;
        private const float Scp0492AttackRange = 2.4f;
        private const float Scp106AttackRange = 4f;
        private const float StrictAttackAimAngle = 14f;

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

        public StandardScpCombatStrategy(FpcBotCombat combat, Scp096CombatStrategy scp096Strategy)
        {
            this.combat = combat;
            this.scp096Strategy = scp096Strategy;
            scp049 = new Scp049CombatAbilities(combat);
            scp106 = new Scp106CombatAbilities(combat);
            scp939 = new Scp939CombatAbilities(combat);
            scp3114 = new Scp3114CombatAbilities(combat);
        }

        public float GetAttackRange(RoleTypeId role)
        {
            return role switch
            {
                RoleTypeId.Scp0492 => Scp0492AttackRange,
                RoleTypeId.Scp106 => Scp106AttackRange,
                _ => AttackRange,
            };
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
                    return scp939.Tick(target, coordinator);
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

        public bool TryUseStrictAttack(RoleTypeId role, CombatTarget target, ScpCombatStrategy coordinator)
        {
            if (role is not (RoleTypeId.Scp049 or RoleTypeId.Scp0492 or RoleTypeId.Scp106))
            {
                return false;
            }

            if (role == RoleTypeId.Scp049)
            {
                return scp049.TryAttack(target);
            }

            if (target.Distance > GetAttackRange(role) || Time.time < coordinator.NextAttackTime)
            {
                return true;
            }

            if (!combat.IsAimedAt(ScpCombatStrategy.GetAimPoint(target), StrictAttackAimAngle))
            {
                return true;
            }

            coordinator.NextAttackTime = Time.time + coordinator.AttackInterval;
            TryUseAttack(role, target.Hub);
            return true;
        }

        public bool TryUseAttack(RoleTypeId role, ReferenceHub target = null)
        {
            if (role == RoleTypeId.Scp096 && scp096Strategy.TryUseNativeAttack(target))
            {
                return true;
            }

            if (role == RoleTypeId.Scp106)
            {
                return target != null && scp106.TryAttack(target);
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
