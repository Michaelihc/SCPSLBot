using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using PlayerRoles;
using SCPSLBot.AI.FirstPersonControl.Movement;
using SCPSLBot.Navigation;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Scp
{
    /// <summary>
    /// SCP-939 lies in wait behind a closed door: on entering a room it sometimes picks one of the
    /// room's closed doors, stands a few metres back on its own side facing the doorway and holds
    /// focus (silent, lunge-ready) for a while. A human who opens the door walks into the lunge;
    /// combat takes over on sight and fires it (<see cref="Combat.ScpAbilities.Scp939CombatAbilities"/>).
    /// An ambush that sees nobody releases focus and goes back to exploring.
    /// </summary>
    internal sealed class Scp939AmbushBehavior : IBotBehavior, IDescribedBehavior
    {
        private const float AmbushChance = 0.3f;
        private const float CooldownSeconds = 75f;
        private const float MinWaitSeconds = 20f;
        private const float MaxWaitSeconds = 45f;
        private const float StandoffMeters = 2.75f;
        private const float MaxDoorDistance = 25f;
        private const float SampleHorizontalMeters = 1.25f;
        private const float SampleVerticalMeters = 1.5f;
        private const float ArriveMeters = 0.6f;
        private const float ApproachBaseSeconds = 6f;
        private const float ApproachSecondsPerMeter = 0.6f;
        private const float FocusAimAngle = 10f;
        private const float DoorLookHeight = 1f;

        private readonly FpcBotPlayer bot;

        private RoomIdentifier lastRolledRoom;
        private DoorVariant door;
        private Vector3 spot;
        private Vector3 lookPoint;
        private bool waiting;
        private float waitUntil;
        private float approachUntil;
        private float nextAllowedTime;

        public Scp939AmbushBehavior(FpcBotPlayer bot)
        {
            this.bot = bot;
        }

        public string Name => "Scp939Ambush";

        public string Detail => door == null
            ? null
            : waiting
                ? $"wait {Mathf.Max(0f, waitUntil - Time.time):F0}s aim={AimAngle():F0} focus={bot.Combat.Scp939.FocusState:F2}"
                : "approach";

        private static INavigationBackend Backend => NavigationSystem.Instance.Backend;

        public float Evaluate()
        {
            if (bot.FpcRole == null || bot.BotHub.PlayerHub.roleManager.CurrentRole.RoleTypeId != RoleTypeId.Scp939)
            {
                door = null;
                lastRolledRoom = null;
                return 0f;
            }

            if (door != null)
            {
                return BehaviorPriority.RoleAbility;
            }

            var now = Time.time;
            if (now < nextAllowedTime
                || !RoomUtils.TryGetRoom(bot.PlayerPosition, out var room)
                || room == lastRolledRoom)
            {
                return 0f;
            }

            // One roll per room entered keeps the ambushes occasional and spread over the map.
            lastRolledRoom = room;
            if (Random.value > AmbushChance || !TryPickDoor(room))
            {
                return 0f;
            }

            return BehaviorPriority.RoleAbility;
        }

        public void OnEnter()
        {
            waiting = false;
            approachUntil = Time.time + ApproachBaseSeconds + Vector3.Distance(bot.PlayerPosition, spot) * ApproachSecondsPerMeter;
        }

        public void OnExit()
        {
            // Combat pre-empting on sight keeps the focus held so it can lunge from the ambush.
            if (!bot.Combat.DiagnosticHasTarget)
            {
                bot.Combat.Scp939.ReleaseAmbushFocus();
            }

            Finish();
        }

        /// <summary>
        /// Ends the ambush. Also called from <see cref="Tick"/>: the brain re-selects right away, and a
        /// behavior that still held its door would win again without ever exiting.
        /// </summary>
        private void Finish()
        {
            if (door != null)
            {
                nextAllowedTime = Time.time + CooldownSeconds;
            }

            door = null;
            waiting = false;
        }

        private bool GiveUp()
        {
            bot.Combat.Scp939.ReleaseAmbushFocus();
            Finish();
            return false;
        }

        public bool Tick()
        {
            if (door == null)
            {
                return false;
            }

            var now = Time.time;
            if (!waiting)
            {
                if (now > approachUntil)
                {
                    return GiveUp();
                }

                if (HorizontalDistance(bot.PlayerPosition, spot) > ArriveMeters)
                {
                    bot.MoveToPosition(spot);
                    return true;
                }

                waiting = true;
                waitUntil = now + Random.Range(MinWaitSeconds, MaxWaitSeconds);
            }

            if (now >= waitUntil)
            {
                return GiveUp();
            }

            // The lunge flies along the facing at focus start: face the doorway, then focus.
            bot.Locomotion.Stop();
            bot.LookToPosition(lookPoint);
            if (bot.Combat.IsAimedAt(lookPoint, FocusAimAngle))
            {
                bot.Combat.Scp939.HoldAmbushFocus();
            }

            return true;
        }

        private bool TryPickDoor(RoomIdentifier room)
        {
            var backend = Backend;
            if (backend == null || !DoorVariant.DoorsByRoom.TryGetValue(room, out var doors))
            {
                return false;
            }

            var position = bot.PlayerPosition;
            var bestSqr = MaxDoorDistance * MaxDoorDistance;
            DoorVariant best = null;
            var bestSpot = Vector3.zero;
            foreach (var candidate in doors)
            {
                if (!DoorAccess.IsPlayerDoor(candidate) || candidate.TargetState || DoorAccess.IsPassable(candidate))
                {
                    continue;
                }

                var doorPosition = candidate.transform.position;
                var sqr = (doorPosition - position).sqrMagnitude;
                if (sqr >= bestSqr)
                {
                    continue;
                }

                // Stand back on the bot's own side of the doorway, inside this room.
                var normal = Vector3.ProjectOnPlane(candidate.transform.forward, Vector3.up).normalized;
                if (Vector3.Dot(position - doorPosition, normal) < 0f)
                {
                    normal = -normal;
                }

                if (!backend.TryGetNearestRoomSample(room, doorPosition + normal * StandoffMeters, SampleHorizontalMeters, SampleVerticalMeters, out var sample))
                {
                    continue;
                }

                best = candidate;
                bestSqr = sqr;
                bestSpot = sample;
            }

            if (best == null)
            {
                return false;
            }

            door = best;
            spot = bestSpot;
            lookPoint = best.transform.position + Vector3.up * DoorLookHeight;
            return true;
        }

        private float AimAngle() => Vector3.Angle(bot.CameraForward, lookPoint - bot.CameraPosition);

        private static float HorizontalDistance(Vector3 a, Vector3 b)
            => Vector3.Distance(Vector3.ProjectOnPlane(a, Vector3.up), Vector3.ProjectOnPlane(b, Vector3.up));
    }
}
