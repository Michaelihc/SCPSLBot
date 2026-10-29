using System;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain
{
    /// <summary>
    /// Utility arbiter over <see cref="IBotBehavior"/>s. Scores are recomputed at a fixed low rate
    /// (staggered per bot by the caller's first tick time) so N bots cost N×behaviors cheap
    /// evaluations a few times a second; only the active behavior ticks per frame.
    /// </summary>
    internal sealed class BotBrain
    {
        public const float DecisionIntervalSeconds = 0.25f;

        /// <summary>Added to the active behavior's score so near-equal utilities do not flap.</summary>
        public const float Hysteresis = 0.05f;

        private readonly List<IBotBehavior> behaviors = new();
        private float nextDecisionTime;

        public IBotBehavior Active { get; private set; }

        public IReadOnlyList<IBotBehavior> Behaviors => behaviors;

        public BotBrain Add(IBotBehavior behavior)
        {
            behaviors.Add(behavior ?? throw new ArgumentNullException(nameof(behavior)));
            return this;
        }

        public T Get<T>() where T : class, IBotBehavior
        {
            foreach (var behavior in behaviors)
            {
                if (behavior is T typed)
                {
                    return typed;
                }
            }

            return null;
        }

        /// <summary>Forces a decision on the next <see cref="Tick"/> (role change, order released).</summary>
        public void Invalidate()
        {
            nextDecisionTime = 0f;
        }

        /// <summary>Drops the active behavior without ticking it (combat or an order took over).</summary>
        public void Suspend()
        {
            SwitchTo(null);
            nextDecisionTime = 0f;
        }

        /// <summary>Returns false when no behavior applies, so the caller can idle.</summary>
        public bool Tick()
        {
            var now = Time.time;
            if (Active == null || now >= nextDecisionTime)
            {
                nextDecisionTime = now + DecisionIntervalSeconds;
                SwitchTo(Select());
            }

            if (Active == null)
            {
                return false;
            }

            if (RunActive())
            {
                return true;
            }

            // The behavior finished or gave up: pick a successor immediately, excluding nothing —
            // a behavior that cannot continue must report a zero score on its next Evaluate.
            nextDecisionTime = now + DecisionIntervalSeconds;
            SwitchTo(Select());
            return Active != null && RunActive();
        }

        private IBotBehavior Select()
        {
            IBotBehavior best = null;
            var bestScore = 0f;
            foreach (var behavior in behaviors)
            {
                float score;
                try
                {
                    score = behavior.Evaluate();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    continue;
                }

                if (behavior == Active)
                {
                    score += Hysteresis;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = behavior;
                }
            }

            return best;
        }

        private bool RunActive()
        {
            try
            {
                return Active.Tick();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return false;
            }
        }

        private void SwitchTo(IBotBehavior next)
        {
            if (next == Active)
            {
                return;
            }

            var previous = Active;
            Active = next;
            try
            {
                previous?.OnExit();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }

            try
            {
                next?.OnEnter();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }
    }
}
