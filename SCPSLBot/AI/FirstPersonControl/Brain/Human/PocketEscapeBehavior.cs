using CustomPlayerEffects;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Human
{
    /// <summary>
    /// Runs for a teleporter while SCP-106's pocket dimension corrodes the bot. Neither navigation
    /// backend covers the pocket, so the bot steers straight across its open floor. Like a player
    /// it cannot tell the exits from the killers: it picks at random and takes the risk, because
    /// staying is fatal anyway. Nothing to do after detonation, when every teleporter kills.
    /// </summary>
    internal sealed class PocketEscapeBehavior : IBotBehavior, IDescribedBehavior
    {
        // The walk across is a few seconds; a bot still on its way after this is caught on something.
        private const float RetargetSeconds = 8f;
        private const float OvershootMeters = 1f;

        private readonly HumanBotContext context;
        private readonly List<PocketDimensionTeleport> untried = new();
        private PocketDimensionTeleport target;
        private float retargetAt;

        public PocketEscapeBehavior(HumanBotContext context)
        {
            this.context = context;
        }

        public string Name => "pocketEscape";

        public string Detail => target != null ? $"teleporter@({target.transform.position.x:F1},{target.transform.position.z:F1})" : null;

        public float Evaluate()
        {
            if (!context.IsHuman || AlphaWarheadController.Detonated)
            {
                return 0f;
            }

            var effects = context.Bot.BotHub.PlayerHub.playerEffectsController;
            return effects.TryGetEffect<PocketCorroding>(out var corroding) && corroding.IsEnabled ? BehaviorPriority.Emergency : 0f;
        }

        public void OnEnter()
        {
            target = null;
            untried.Clear();
            foreach (var teleporter in PocketDimensionTeleport.AllInstances)
            {
                if (teleporter != null)
                {
                    untried.Add(teleporter);
                }
            }
        }

        public void OnExit()
        {
            target = null;
            untried.Clear();
        }

        public bool Tick()
        {
            if ((target == null || Time.time >= retargetAt) && !TryPickTarget())
            {
                return false;
            }

            var position = context.Bot.PlayerPosition;
            var teleporter = target.transform.position;
            var across = Vector3.ProjectOnPlane(teleporter - position, Vector3.up).normalized;
            // Aiming past the centre keeps the capsule moving until it is well inside the trigger.
            context.Bot.Locomotion.SteerDirect(teleporter + across * OvershootMeters);
            return true;
        }

        private bool TryPickTarget()
        {
            target = null;
            while (untried.Count > 0)
            {
                var index = Random.Range(0, untried.Count);
                var candidate = untried[index];
                untried.RemoveAt(index);
                if (candidate != null)
                {
                    target = candidate;
                    retargetAt = Time.time + RetargetSeconds;
                    return true;
                }
            }

            return false;
        }
    }
}
