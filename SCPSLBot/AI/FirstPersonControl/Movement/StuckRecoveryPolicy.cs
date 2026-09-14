using System;

namespace SCPSLBot.AI.FirstPersonControl.Movement
{
    [Flags]
    internal enum StuckRecoveryActions
    {
        None = 0,
        /// <summary>Try to open a closed door directly ahead.</summary>
        OpenDoor = 1 << 0,
        /// <summary>Blend a sideways component into the move direction (sign from <see cref="StuckRecoveryPolicy.NudgeSign"/>).</summary>
        Nudge = 1 << 1,
        /// <summary>Force a native jump.</summary>
        Jump = 1 << 2,
        /// <summary>Walk backwards briefly to leave a wedge before retrying.</summary>
        BackOff = 1 << 3,
        /// <summary>Re-plan the current path.</summary>
        Replan = 1 << 4,
        /// <summary>Penalize the crossing being attempted so the next plan avoids it, then re-plan.</summary>
        AvoidCrossing = 1 << 5,
        /// <summary>The bot has not made progress for a long time; the owner should pick a new goal.</summary>
        AbandonGoal = 1 << 6,
    }

    /// <summary>
    /// Time-based escalation ladder for a bot that wants to move but is not making progress. Pure
    /// (no engine types) so the ladder is unit-testable. Progress means either the bot displaced
    /// horizontally from its anchor or its distance to the current waypoint improved.
    /// </summary>
    internal sealed class StuckRecoveryPolicy
    {
        public const float WaypointProgressMeters = 0.25f;
        public const float RecoveryProgressMeters = 0.6f;
        public const float NudgeAfterSeconds = 0.7f;
        public const float NudgeFlipSeconds = 0.8f;
        public const float JumpAfterSeconds = 1.5f;
        public const float JumpIntervalSeconds = 1f;
        public const float BackOffAfterSeconds = 2.2f;
        public const float BackOffDurationSeconds = 0.4f;
        public const float ReplanAfterSeconds = 2.5f;
        public const float AvoidCrossingAfterReplans = 2;
        public const float AbandonAfterReplans = 4;
        public const float DoorRetrySeconds = 0.6f;
        public const float SameSpotRadiusMeters = 1.5f;

        private float anchorX;
        private float anchorZ;
        private float anchorTime;
        private bool anchored;
        private float bestWaypointDistance = float.PositiveInfinity;
        private float nextNudgeFlipAt;
        private float nextJumpAt;
        private float nextDoorAt;
        private float backOffUntil;
        private bool backOffIssued;
        private float replanSpotX;
        private float replanSpotZ;
        private int replansAtSpot;
        private int lastProgressStamp;

        public int NudgeSign { get; private set; } = 1;
        public float StuckSeconds { get; private set; }
        public int ReplansAtSpot => replansAtSpot;
        public bool IsBackingOff(float now) => now < backOffUntil;

        public void Reset(float x, float z)
        {
            anchorX = x;
            anchorZ = z;
            anchored = false;
            anchorTime = 0f;
            bestWaypointDistance = float.PositiveInfinity;
            StuckSeconds = 0f;
            backOffUntil = 0f;
            backOffIssued = false;
        }

        /// <summary>Forget the replan counter (a new goal or an accepted crossing change).</summary>
        public void ResetEscalation()
        {
            replansAtSpot = 0;
        }

        /// <summary>
        /// Advances the ladder for one tick. <paramref name="hasMoveIntent"/> false means the bot is
        /// deliberately standing still; that never counts as stuck.
        /// </summary>
        public StuckRecoveryActions Tick(float now, bool hasMoveIntent, float x, float z, float waypointDistance, bool doorAhead, int progressStamp = 0)
        {
            if (!hasMoveIntent)
            {
                Reset(x, z);
                lastProgressStamp = progressStamp;
                return StuckRecoveryActions.None;
            }

            if (!anchored)
            {
                anchored = true;
                anchorX = x;
                anchorZ = z;
                anchorTime = now;
                bestWaypointDistance = waypointDistance;
                lastProgressStamp = progressStamp;
                StuckSeconds = 0f;
                backOffIssued = false;
                return StuckRecoveryActions.None;
            }

            // Progress is strictly "closer to the waypoint" or "a portal was crossed / a new goal
            // was planned" (the navigator's stamp). Plain displacement is deliberately ignored:
            // creeping along a wall, sideways nudges, back-offs and a plan that keeps swapping
            // between two waypoints all move the bot without getting it anywhere. A waypoint that
            // jumped to a farther point never raises the approach baseline for the same reason.
            // Once the ladder is active a forced jump or a nudge can land a few centimeters nearer;
            // demand a real stride before calling that progress so back-off and replan still fire.
            var inRecovery = now - anchorTime >= NudgeAfterSeconds;
            var approachThreshold = inRecovery ? RecoveryProgressMeters : WaypointProgressMeters;
            var approached = waypointDistance < bestWaypointDistance - approachThreshold;
            var advanced = progressStamp != lastProgressStamp;
            lastProgressStamp = progressStamp;
            if (approached || advanced)
            {
                anchorX = x;
                anchorZ = z;
                anchorTime = now;
                bestWaypointDistance = waypointDistance;
                StuckSeconds = 0f;
                backOffUntil = 0f;
                backOffIssued = false;
                return StuckRecoveryActions.None;
            }

            StuckSeconds = now - anchorTime;
            var actions = StuckRecoveryActions.None;
            if (StuckSeconds < NudgeAfterSeconds)
            {
                return actions;
            }

            if (doorAhead && now >= nextDoorAt)
            {
                nextDoorAt = now + DoorRetrySeconds;
                actions |= StuckRecoveryActions.OpenDoor;
            }

            if (now >= nextNudgeFlipAt)
            {
                NudgeSign = -NudgeSign;
                nextNudgeFlipAt = now + NudgeFlipSeconds;
            }

            actions |= StuckRecoveryActions.Nudge;

            if (StuckSeconds >= JumpAfterSeconds && now >= nextJumpAt)
            {
                nextJumpAt = now + JumpIntervalSeconds;
                actions |= StuckRecoveryActions.Jump;
            }

            if (StuckSeconds >= BackOffAfterSeconds && !backOffIssued)
            {
                backOffIssued = true;
                backOffUntil = now + BackOffDurationSeconds;
            }

            if (now < backOffUntil)
            {
                actions |= StuckRecoveryActions.BackOff;
            }

            if (StuckSeconds >= ReplanAfterSeconds)
            {
                var sdx = x - replanSpotX;
                var sdz = z - replanSpotZ;
                if (replansAtSpot > 0 && sdx * sdx + sdz * sdz <= SameSpotRadiusMeters * SameSpotRadiusMeters)
                {
                    replansAtSpot++;
                }
                else
                {
                    replansAtSpot = 1;
                    replanSpotX = x;
                    replanSpotZ = z;
                }

                actions |= StuckRecoveryActions.Replan;
                if (replansAtSpot >= AvoidCrossingAfterReplans)
                {
                    actions |= StuckRecoveryActions.AvoidCrossing;
                }

                if (replansAtSpot >= AbandonAfterReplans)
                {
                    actions |= StuckRecoveryActions.AbandonGoal;
                    replansAtSpot = 0;
                }

                // Restart the ladder from a fresh anchor after every replan.
                anchorX = x;
                anchorZ = z;
                anchorTime = now;
                bestWaypointDistance = float.PositiveInfinity;
                StuckSeconds = 0f;
                backOffUntil = 0f;
                backOffIssued = false;
            }

            return actions;
        }
    }
}
