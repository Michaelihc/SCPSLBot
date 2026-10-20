using PlayerRoles.FirstPersonControl;
using PlayerRoles.PlayableScps.Scp3114;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat.ScpAbilities
{
    /// <summary>
    /// SCP-3114 combat flow: a disguised bot walks up to its target, reveals itself, then strangles
    /// during the native post-reveal window and slaps otherwise. Slap is refused natively while disguised.
    /// </summary>
    internal sealed class Scp3114CombatAbilities
    {
        private const float RevealDistance = 3f;
        private const float StrangleReach = 2.4f;
        private const float RevealRetrySeconds = 0.6f;
        private const float StrangleRetrySeconds = 0.4f;
        private const float CancelDisguiseDistance = 14f;

        private readonly FpcBotCombat combat;

        private float nextRevealTry;
        private float nextStrangleTry;
        private float nextCancelTry;

        public Scp3114CombatAbilities(FpcBotCombat combat)
        {
            this.combat = combat;
        }

        /// <summary>Returns true when this tick is spent (disguised approach, reveal, strangling) and the slap must be skipped.</summary>
        public bool Tick(CombatTarget target)
        {
            var abilities = combat.Abilities;
            if (abilities.Role is not Scp3114Role role)
            {
                return false;
            }

            var now = Time.time;
            var botPlayer = combat.BotPlayer;

            // Equipping a stolen skin locks the motor for five seconds: abandon it once a hostile is close.
            if (now >= nextCancelTry
                && target.Distance <= CancelDisguiseDistance
                && abilities.TryGet<Scp3114Disguise>(out var disguise)
                && disguise.IsInProgress)
            {
                nextCancelTry = now + RevealRetrySeconds;
                ScpAbilityCommands.SendRagdoll(disguise, null);
            }

            if (role.Disguised)
            {
                if (target.HasLineOfSight
                    && target.Distance <= RevealDistance
                    && now >= nextRevealTry
                    && abilities.TryGet<Scp3114Reveal>(out var reveal))
                {
                    nextRevealTry = now + RevealRetrySeconds;
                    ScpAbilityCommands.Send(reveal);
                }

                return true;
            }

            if (!abilities.TryGet<Scp3114Strangle>(out var strangle))
            {
                return false;
            }

            if (strangle.SyncTarget.HasValue)
            {
                // Mid-strangle: the motor is locked by the ability, just keep facing the victim.
                botPlayer.Move.DesiredLocalDirection = Vector3.zero;
                botPlayer.LookToPosition(ScpCombatStrategy.GetAimPoint(target));
                return true;
            }

            if (now >= nextStrangleTry
                && target.HasLineOfSight
                && target.Distance <= StrangleReach
                && !strangle.StrangleTimer.IsReady
                && strangle.Cooldown.IsReady
                && abilities.TryGet<Scp3114Slap>(out var slap)
                && slap.Cooldown.IsReady
                && target.Hub.roleManager.CurrentRole is IFpcRole targetRole)
            {
                nextStrangleTry = now + StrangleRetrySeconds;
                ScpAbilityCommands.SendStrangle(
                    strangle,
                    target.Hub,
                    targetRole.FpcModule.Position,
                    role.FpcModule.Position);
                return strangle.SyncTarget.HasValue;
            }

            return false;
        }
    }
}
