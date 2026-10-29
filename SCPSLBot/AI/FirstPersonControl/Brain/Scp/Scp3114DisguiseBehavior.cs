using PlayerRoles;
using PlayerRoles.PlayableScps.Scp3114;
using PlayerRoles.Ragdolls;
using PlayerRoles.Subroutines;
using SCPSLBot.AI.FirstPersonControl.Perception;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Scp
{
    /// <summary>An undisguised SCP-3114 steals the skin of a nearby human corpse (native five-second process).</summary>
    internal sealed class Scp3114DisguiseBehavior : RagdollAbilityBehavior
    {
        public Scp3114DisguiseBehavior(FpcBotPlayer bot)
            : base(bot)
        {
        }

        public override string Name => "Scp3114Disguise";

        protected override bool TryGetAbility(out SubroutineBase ability)
        {
            if (Bot.Combat.Abilities.TryGet<Scp3114Disguise>(out var disguise))
            {
                ability = disguise;
                return true;
            }

            ability = null;
            return false;
        }

        protected override bool IsInProgress(SubroutineBase ability)
        {
            return ability is Scp3114Disguise { IsInProgress: true };
        }

        protected override float Urgency()
        {
            if (Bot.Combat.Abilities.Role is not Scp3114Role role || role.Disguised)
            {
                return 0f;
            }

            // The native cooldown is only checked client side; honour it so the bot does not re-disguise instantly.
            return Bot.Combat.Abilities.TryGet<Scp3114Disguise>(out var disguise) && disguise.Cooldown.IsReady ? 0.6f : 0f;
        }

        protected override bool IsCandidate(in TrackedRagdoll candidate)
        {
            return candidate.Role.IsHuman();
        }

        protected override bool IsEligible(SubroutineBase ability, DynamicRagdoll ragdoll, float distance)
        {
            // Mirrors Scp3114Disguise.AnyValidateBegin: a human corpse that was not already skin-stolen.
            return ragdoll.Info.RoleType.IsHuman()
                   && ragdoll.Info.Handler is not Scp3114DamageHandler { Subtype: Scp3114DamageHandler.HandlerType.SkinSteal };
        }
    }
}
