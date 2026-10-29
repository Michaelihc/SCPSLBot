using PlayerRoles.PlayableScps.Scp049;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat.ScpAbilities
{
    /// <summary>
    /// SCP-049 chase and attack abilities, submitted as direct commands. Native validation runs on
    /// every command, so the retry timers below only keep rejected attempts cheap.
    /// </summary>
    internal sealed class Scp049CombatAbilities
    {
        // Native reach is 1.728 m from the nearest point of the target's bounds, which is roughly
        // 2.2 m between the two player origins.
        private const float AttackReach = 2.2f;
        private const float AttackAimAngle = 25f;
        private const float AttackRetrySeconds = 0.15f;
        private const float CallMinDistance = 8f;
        private const float CallMaxDistance = 40f;
        private const float SenseMinDistance = 5f;
        private const float SenseAimAngle = 12f;
        private const float AbilityRetrySeconds = 0.5f;
        private const float CancelReviveDistance = 14f;

        private readonly FpcBotCombat combat;

        private float nextAttackTry;
        private float nextCallTry;
        private float nextSenseTry;
        private float nextCancelTry;

        public Scp049CombatAbilities(FpcBotCombat combat)
        {
            this.combat = combat;
        }

        public void TickChase(CombatTarget target)
        {
            var abilities = combat.Abilities;
            var now = Time.time;

            // A running resurrection locks the motor for seven seconds: abandon it once a hostile is close.
            if (now >= nextCancelTry
                && target.Distance <= CancelReviveDistance
                && abilities.TryGet<Scp049ResurrectAbility>(out var resurrect)
                && resurrect.IsInProgress)
            {
                nextCancelTry = now + AbilityRetrySeconds;
                ScpAbilityCommands.SendRagdoll(resurrect, null);
            }

            if (now >= nextCallTry
                && target.Distance >= CallMinDistance
                && target.Distance <= CallMaxDistance
                && abilities.TryGet<Scp049CallAbility>(out var call)
                && call.Cooldown.IsReady
                && call.Duration.IsReady)
            {
                nextCallTry = now + AbilityRetrySeconds;
                ScpAbilityCommands.Send(call);
            }

            if (now >= nextSenseTry
                && target.HasLineOfSight
                && target.Distance >= SenseMinDistance
                && abilities.TryGet<Scp049SenseAbility>(out var sense)
                && sense.Cooldown.IsReady
                && sense.Duration.IsReady
                && combat.IsAimedAt(target.AimPoint, SenseAimAngle))
            {
                nextSenseTry = now + AbilityRetrySeconds;
                ScpAbilityCommands.SendHub(sense, target.Hub);
            }
        }

        /// <summary>Always reports the attack phase as handled; the native cooldown (1.5 s) paces the swings.</summary>
        public bool TryAttack(CombatTarget target)
        {
            var now = Time.time;
            if (now < nextAttackTry
                || target.Distance > AttackReach
                || !combat.Abilities.TryGet<Scp049AttackAbility>(out var attack)
                || !attack.Cooldown.IsReady
                || !combat.IsAimedAt(ScpCombatStrategy.GetAimPoint(target), AttackAimAngle))
            {
                return true;
            }

            nextAttackTry = now + AttackRetrySeconds;
            ScpAbilityCommands.SendHub(attack, target.Hub);
            return true;
        }
    }
}
