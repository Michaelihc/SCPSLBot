using PlayerRoles.Ragdolls;
using PlayerRoles.Subroutines;
using SCPSLBot.AI.FirstPersonControl.Combat.ScpAbilities;
using SCPSLBot.AI.FirstPersonControl.Perception;
using SCPSLBot.Infrastructure;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Scp
{
    /// <summary>
    /// Shared flow for the native ragdoll abilities (SCP-049 resurrect, SCP-049-2 consume, SCP-3114
    /// disguise): pick a nearby eligible corpse, walk to within native reach of one of its bones,
    /// stand still and aim at it, start the ability and hold still until the native process ends.
    /// A corpse the bot cannot reach or whose start the server keeps refusing is set aside.
    /// </summary>
    internal abstract class RagdollAbilityBehavior : IBotBehavior
    {
        private const float SearchRadius = 25f;
        private const float SearchRadiusSqr = SearchRadius * SearchRadius;

        // Native reach is sqrt(3.3..3.5) = 1.8..1.9 m to any bone; start a little inside it.
        private const float StartDistanceSqr = 1.4f * 1.4f;
        private const float StartRetrySeconds = 0.5f;
        private const int MaxStartFailures = 2;
        private const float AttemptBlacklistSeconds = 60f;
        private const float PathGraceSeconds = 1.5f;
        private const float EligibilityRecheckSeconds = 0.5f;
        private const float ApproachBaseSeconds = 8f;
        private const float ApproachSecondsPerMeter = 1.2f;
        private const int AttemptPruneThreshold = 16;

        private readonly Dictionary<DynamicRagdoll, Attempt> attempts = new(ManagedReferenceComparer<DynamicRagdoll>.Instance);

        private readonly List<DynamicRagdoll> staleKeys = new();

        private DynamicRagdoll current;
        private bool started;
        private float approachStartTime;
        private float nextStartTry;
        private float nextEligibilityCheck;

        private sealed class Attempt
        {
            public int Failures;
            public float BlockedUntil;
        }

        protected RagdollAbilityBehavior(FpcBotPlayer bot)
        {
            Bot = bot;
        }

        public abstract string Name { get; }

        protected FpcBotPlayer Bot { get; }

        /// <summary>Whether a started process can be cancelled by sending a null ragdoll.</summary>
        protected virtual bool CanCancel => true;

        /// <summary>Resolves the ability for the bot's current role; false for any other role.</summary>
        protected abstract bool TryGetAbility(out SubroutineBase ability);

        protected abstract bool IsInProgress(SubroutineBase ability);

        /// <summary>0 when the bot has no need for the ability right now, up to 1 when it is urgent.</summary>
        protected abstract float Urgency();

        /// <summary>Cheap role/ownership filter applied to every tracked corpse before <see cref="IsEligible"/>.</summary>
        protected abstract bool IsCandidate(in TrackedRagdoll candidate);

        /// <summary>Native-backed eligibility of one corpse, given the straight-line distance to it.</summary>
        protected abstract bool IsEligible(SubroutineBase ability, DynamicRagdoll ragdoll, float distance);

        public float Evaluate()
        {
            if (!TryGetAbility(out var ability))
            {
                return 0f;
            }

            if (current != null && IsInProgress(ability))
            {
                return Score(1f);
            }

            var urgency = Urgency();
            if (urgency <= 0f)
            {
                return 0f;
            }

            var now = Time.time;
            if (current != null && !IsBlocked(current, now))
            {
                var distance = Vector3.Distance(Bot.PlayerPosition, current.transform.position);
                if (IsEligible(ability, current, distance))
                {
                    return Score(urgency);
                }
            }

            current = FindBest(ability, now);
            return current != null ? Score(urgency) : 0f;
        }

        public void OnEnter()
        {
            started = false;
            approachStartTime = Time.time;
            nextStartTry = 0f;
            nextEligibilityCheck = 0f;
        }

        public void OnExit()
        {
            // Leaving mid-process (combat or a more urgent behavior) abandons the corpse.
            if (started && CanCancel && TryGetAbility(out var ability) && IsInProgress(ability))
            {
                ScpAbilityCommands.SendRagdoll(ability, null);
            }

            started = false;
        }

        public bool Tick()
        {
            if (current == null || !TryGetAbility(out var ability))
            {
                return false;
            }

            var bot = Bot;
            var now = Time.time;
            if (IsInProgress(ability))
            {
                started = true;
                bot.Locomotion.Stop();
                if (RagdollTracker.TryGetNearestBone(current, bot.PlayerPosition, out var heldBone, out _))
                {
                    bot.LookToPosition(heldBone);
                }

                return true;
            }

            if (started)
            {
                // The process ended without the corpse being consumed (cancelled or invalidated): count it.
                started = false;
                RegisterFailure(current, now);
                return false;
            }

            if (now >= nextEligibilityCheck)
            {
                nextEligibilityCheck = now + EligibilityRecheckSeconds;
                var distance = Vector3.Distance(bot.PlayerPosition, current.transform.position);
                if (!IsEligible(ability, current, distance))
                {
                    Block(current, now);
                    return false;
                }
            }

            if (!RagdollTracker.TryGetNearestBone(current, bot.PlayerPosition, out var bone, out var sqrDistance))
            {
                return false;
            }

            if (sqrDistance <= StartDistanceSqr)
            {
                return TryStart(ability, bone, now);
            }

            return Approach(bone, Mathf.Sqrt(sqrDistance), now);
        }

        private bool TryStart(SubroutineBase ability, Vector3 bone, float now)
        {
            var bot = Bot;
            bot.Locomotion.Stop();
            bot.LookToPosition(bone);
            if (now < nextStartTry)
            {
                return true;
            }

            nextStartTry = now + StartRetrySeconds;
            ScpAbilityCommands.SendRagdoll(ability, current);
            if (IsInProgress(ability))
            {
                started = true;
                return true;
            }

            // The server refused to start (validation failed): stop trying this corpse after a repeat.
            return !RegisterFailure(current, now);
        }

        private bool Approach(Vector3 bone, float distance, float now)
        {
            var bot = Bot;
            var elapsed = now - approachStartTime;
            if (elapsed > ApproachBaseSeconds + distance * ApproachSecondsPerMeter)
            {
                Block(current, now);
                return false;
            }

            bot.MoveToPosition(bone);
            if (elapsed > PathGraceSeconds && !bot.Navigator.HasPath && !bot.Navigator.HasPartialPath)
            {
                Block(current, now);
                return false;
            }

            return true;
        }

        private float Score(float urgency)
        {
            return BehaviorPriority.RoleAbility + 0.09f * Mathf.Clamp01(urgency);
        }

        private DynamicRagdoll FindBest(SubroutineBase ability, float now)
        {
            PruneAttempts();
            var position = Bot.PlayerPosition;
            DynamicRagdoll best = null;
            var bestSqr = SearchRadiusSqr;
            foreach (var candidate in RagdollTracker.Get(now))
            {
                var ragdoll = candidate.Ragdoll;
                if (ragdoll == null || !IsCandidate(candidate) || IsBlocked(ragdoll, now))
                {
                    continue;
                }

                var sqr = (candidate.Position - position).sqrMagnitude;
                if (sqr >= bestSqr || !IsEligible(ability, ragdoll, Mathf.Sqrt(sqr)))
                {
                    continue;
                }

                best = ragdoll;
                bestSqr = sqr;
            }

            return best;
        }

        private bool IsBlocked(DynamicRagdoll ragdoll, float now)
        {
            return attempts.TryGetValue(ragdoll, out var attempt) && now < attempt.BlockedUntil;
        }

        private void Block(DynamicRagdoll ragdoll, float now)
        {
            if (ragdoll == null)
            {
                return;
            }

            var attempt = GetAttempt(ragdoll);
            attempt.BlockedUntil = now + AttemptBlacklistSeconds;
        }

        /// <summary>Counts a failed start; returns true (and blocks the corpse) once it failed too often.</summary>
        private bool RegisterFailure(DynamicRagdoll ragdoll, float now)
        {
            if (ragdoll == null)
            {
                return false;
            }

            var attempt = GetAttempt(ragdoll);
            attempt.Failures++;
            if (attempt.Failures < MaxStartFailures)
            {
                return false;
            }

            attempt.BlockedUntil = now + AttemptBlacklistSeconds;
            return true;
        }

        private Attempt GetAttempt(DynamicRagdoll ragdoll)
        {
            if (!attempts.TryGetValue(ragdoll, out var attempt))
            {
                attempt = new Attempt();
                attempts[ragdoll] = attempt;
            }

            return attempt;
        }

        private void PruneAttempts()
        {
            if (attempts.Count < AttemptPruneThreshold)
            {
                return;
            }

            staleKeys.Clear();
            foreach (var pair in attempts)
            {
                if (pair.Key == null)
                {
                    staleKeys.Add(pair.Key);
                }
            }

            foreach (var key in staleKeys)
            {
                attempts.Remove(key);
            }
        }
    }
}
