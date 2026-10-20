using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Misc;
using System;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Spacial
{
    /// <summary>
    /// An action that travels to one of the positions a <see cref="Location"/> belief knows. The
    /// target is the best-scoring position (nearest by default) that is not remembered as failed
    /// or already handled; it is committed while the action runs so replans do not make the bot
    /// turn around. Doors and elevators on the way are crossed by locomotion; a route locomotion
    /// gives up on fails the action and the target is skipped for a while.
    /// </summary>
    internal abstract class GoTo<TLocation> : IAction
        where TLocation : Location
    {
        private const float BaseTimeoutSeconds = 20f;
        private const float TimeoutMetersPerSecond = 1.5f;
        private const float OtherZoneAllowanceSeconds = 60f;
        // Estimated trip length while no target is known yet. Must stay finite: the planner only
        // expands the preconditions of actions it can price, and "find a target first" is
        // exactly such a precondition.
        protected const float UnknownDistanceMeters = 10f;

        protected GoTo(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        protected readonly FpcBotPlayer botPlayer;
        protected TLocation location;

        /// <summary>Targets this action failed on or already handled.</summary>
        protected readonly TargetMemory Memory = new();

        private Vector3 target;
        private bool hasTarget;
        private bool isRunning;
        private int targetFrame = -1;
        private int memoryGeneration = int.MinValue;

        /// <summary>The position this action travels to; valid once <see cref="HasTarget"/> returned true.</summary>
        protected Vector3 Target => target;

        protected virtual TLocation SetEnabledByLocation(FpcMind fpcMind, Func<TLocation, bool> currentGetter)
        {
            return fpcMind.ActionEnabledBy<TLocation>(this, currentGetter);
        }

        public virtual void SetEnabledByBeliefs(FpcMind fpcMind)
        {
            SetEnabledByLocationAndGlass(fpcMind);
        }

        /// <summary>Preconditions shared by every trip: a usable target exists and no window nothing can open blocks it.</summary>
        protected void SetEnabledByLocationAndGlass(FpcMind fpcMind)
        {
            this.location = SetEnabledByLocation(fpcMind, _ => HasTarget());

            // Doors and elevators on the way are execution detail of BotLocomotion; only a window
            // nothing can open disables the trip.
            fpcMind.ActionEnabledBy<GlassObstacle>(this, b => !hasTarget || !b.Is(target));
        }

        public abstract void SetImpactsBeliefs(FpcMind fpcMind);

        public abstract float Weight { get; }

        public virtual float Cost => (HasTarget() ? Vector3.Distance(target, botPlayer.CameraPosition) : UnknownDistanceMeters) * Weight;

        public virtual float TimeoutSeconds
        {
            get
            {
                if (!HasTarget())
                {
                    return BaseTimeoutSeconds;
                }

                var budget = BaseTimeoutSeconds + Vector3.Distance(target, botPlayer.PlayerPosition) / TimeoutMetersPerSecond;
                if (RoomUtils.TryGetRoom(target, out var targetRoom)
                    && RoomUtils.TryGetRoom(botPlayer.PlayerPosition, out var botRoom)
                    && targetRoom.Zone != botRoom.Zone)
                {
                    budget += OtherZoneAllowanceSeconds;
                }

                return budget;
            }
        }

        /// <summary>
        /// Lower is better; <see cref="float.PositiveInfinity"/> excludes the position. Default:
        /// straight-line distance from the camera.
        /// </summary>
        protected virtual float ScoreTarget(Vector3 position, int index)
        {
            return Vector3.Distance(position, botPlayer.CameraPosition);
        }

        /// <summary>
        /// Picks (or keeps, while running) the target. Evaluated at most once per frame, so the
        /// planner's cost and precondition queries share one selection.
        /// </summary>
        protected bool HasTarget()
        {
            var frame = Time.frameCount;
            if (frame == targetFrame)
            {
                return hasTarget;
            }

            targetFrame = frame;
            ResetMemoryOnNewMap();
            if (location == null)
            {
                hasTarget = false;
                return false;
            }

            var positions = location.Positions;
            var now = Time.time;
            if (isRunning && hasTarget && ContainsTarget(positions) && !Memory.IsBlocked(target, now))
            {
                return true;
            }

            var bestScore = float.PositiveInfinity;
            var found = false;
            for (var index = 0; index < positions.Count; index++)
            {
                var position = positions[index];
                if (Memory.IsBlocked(position, now))
                {
                    continue;
                }

                var score = ScoreTarget(position, index);
                if (score < bestScore)
                {
                    bestScore = score;
                    target = position;
                    found = true;
                }
            }

            hasTarget = found;
            return found;
        }

        private bool ContainsTarget(System.Collections.Generic.List<Vector3> positions)
        {
            for (var index = 0; index < positions.Count; index++)
            {
                if ((positions[index] - target).sqrMagnitude < 0.01f)
                {
                    target = positions[index];
                    return true;
                }
            }

            return false;
        }

        private void ResetMemoryOnNewMap()
        {
            var generation = Navigation.NavigationSystem.Instance.MapGeneration;
            if (generation != memoryGeneration)
            {
                memoryGeneration = generation;
                Memory.Clear();
                hasTarget = false;
            }
        }

        /// <summary>Walks toward the target; true once within <paramref name="arrivalMeters"/> of the camera.</summary>
        protected bool MoveToTarget(float arrivalMeters)
        {
            if (Vector3.Distance(target, botPlayer.CameraPosition) <= arrivalMeters)
            {
                return true;
            }

            botPlayer.MoveToPosition(target);
            return false;
        }

        /// <summary>Turns the camera toward <paramref name="position"/>; true once it looks straight at it.</summary>
        protected bool LookAt(Vector3 position, float maxAngleDegrees = 3f)
        {
            var direction = position - botPlayer.CameraPosition;
            if (direction.sqrMagnitude < 0.0001f || Vector3.Angle(botPlayer.CameraForward, direction) <= maxAngleDegrees)
            {
                return true;
            }

            botPlayer.LookToPosition(position);
            return false;
        }

        /// <summary>Remembers the current target as handled so the next plan picks another one.</summary>
        protected void MarkTargetDone(float seconds)
        {
            if (hasTarget)
            {
                Memory.NoteDone(target, Time.time, seconds);
                targetFrame = -1;
            }
        }

        public virtual void Reset()
        {
            isRunning = true;
            targetFrame = -1;
            HasTarget();
        }

        public virtual void Stop()
        {
            isRunning = false;
            targetFrame = -1;
        }

        public virtual bool OnFailed()
        {
            if (hasTarget)
            {
                Memory.NoteFailure(target, Time.time);
                targetFrame = -1;
            }

            return true;
        }

        public ActionStatus Tick()
        {
            if (!HasTarget())
            {
                // The belief dropped every usable position (seen absent, taken by someone else).
                return ActionStatus.Completed;
            }

            return TickTowardsTarget();
        }

        protected abstract ActionStatus TickTowardsTarget();
    }
}
