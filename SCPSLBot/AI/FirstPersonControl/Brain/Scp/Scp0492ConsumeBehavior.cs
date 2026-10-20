using PlayerRoles;
using PlayerRoles.PlayableScps.Scp049.Zombies;
using PlayerRoles.Ragdolls;
using PlayerRoles.Subroutines;
using PlayerStatsSystem;
using SCPSLBot.AI.FirstPersonControl.Perception;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Scp
{
    /// <summary>A wounded SCP-049-2 eats a nearby human corpse to heal; the native process cannot be cancelled.</summary>
    internal sealed class Scp0492ConsumeBehavior : RagdollAbilityBehavior
    {
        // Eat once clearly hurt: a full-health zombie is refused natively, and a scratch is not worth 7 s.
        private const float HurtHealthFraction = 0.8f;

        public Scp0492ConsumeBehavior(FpcBotPlayer bot)
            : base(bot)
        {
        }

        public override string Name => "Scp0492Consume";

        protected override bool CanCancel => false;

        protected override bool TryGetAbility(out SubroutineBase ability)
        {
            if (Bot.Combat.Abilities.TryGet<ZombieConsumeAbility>(out var consume))
            {
                ability = consume;
                return true;
            }

            ability = null;
            return false;
        }

        protected override bool IsInProgress(SubroutineBase ability)
        {
            return ability is ZombieConsumeAbility { IsInProgress: true };
        }

        protected override float Urgency()
        {
            var health = Bot.BotHub.PlayerHub.playerStats.GetModule<HealthStat>().NormalizedValue;
            return health < HurtHealthFraction ? 1f - health : 0f;
        }

        protected override bool IsCandidate(in TrackedRagdoll candidate)
        {
            return candidate.Role.IsHuman() && !ZombieConsumeAbility.ConsumedRagdolls.Contains(candidate.Ragdoll);
        }

        protected override bool IsEligible(SubroutineBase ability, DynamicRagdoll ragdoll, float distance)
        {
            return ragdoll.Info.RoleType.IsHuman() && !ZombieConsumeAbility.ConsumedRagdolls.Contains(ragdoll);
        }
    }
}
