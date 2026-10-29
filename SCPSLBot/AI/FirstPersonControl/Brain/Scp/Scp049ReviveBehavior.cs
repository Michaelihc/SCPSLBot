using PlayerRoles;
using PlayerRoles.PlayableScps.Scp049;
using PlayerRoles.Ragdolls;
using PlayerRoles.Subroutines;
using SCPSLBot.AI.FirstPersonControl.Perception;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Scp
{
    /// <summary>SCP-049 walks to a fresh human corpse and resurrects it with the native seven-second process.</summary>
    internal sealed class Scp049ReviveBehavior : RagdollAbilityBehavior
    {
        // Native corpse lifetimes: 12 s, or 18 s for a Sense-marked victim (Scp049ResurrectAbility).
        private const float HumanCorpseSeconds = 12f;
        private const float SenseCorpseSeconds = 18f;
        private const float AssumedSpeed = 4.5f;
        private const float TravelMarginSeconds = 0.5f;

        public Scp049ReviveBehavior(FpcBotPlayer bot)
            : base(bot)
        {
        }

        public override string Name => "Scp049Revive";

        protected override bool TryGetAbility(out SubroutineBase ability)
        {
            if (Bot.Combat.Abilities.TryGet<Scp049ResurrectAbility>(out var resurrect))
            {
                ability = resurrect;
                return true;
            }

            ability = null;
            return false;
        }

        protected override bool IsInProgress(SubroutineBase ability)
        {
            return ability is Scp049ResurrectAbility { IsInProgress: true };
        }

        protected override float Urgency()
        {
            // Closer corpses are scored through the nearest-first selection; reviving is always welcome.
            return 0.5f;
        }

        protected override bool IsCandidate(in TrackedRagdoll candidate)
        {
            return candidate.Role.IsHuman() || candidate.Role == RoleTypeId.Scp0492;
        }

        protected override bool IsEligible(SubroutineBase ability, DynamicRagdoll ragdoll, float distance)
        {
            if (ability is not Scp049ResurrectAbility resurrect || !resurrect.CheckRagdoll(ragdoll))
            {
                return false;
            }

            // A corpse that expires before the walk ends is not worth the trip.
            var owner = ragdoll.Info.OwnerHub;
            var lifetime = owner != null
                           && Bot.Combat.Abilities.TryGet<Scp049SenseAbility>(out var sense)
                           && sense.DeadTargets.Contains(owner)
                ? SenseCorpseSeconds
                : HumanCorpseSeconds;
            return ragdoll.Info.ExistenceTime + distance / AssumedSpeed + TravelMarginSeconds <= lifetime
                   || ragdoll.Info.RoleType == RoleTypeId.Scp0492;
        }
    }
}
