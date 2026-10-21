using MapGeneration;
using PlayerRoles.FirstPersonControl;
using PlayerStatsSystem;
using SCPSLBot.Api;
using LabLogger = LabApi.Features.Console.Logger;
using SCPSLBot.Navigation;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Human
{
    /// <summary>
    /// Civilians escape from noticed SCPs even while armed; other humans flee when unable to fight.
    /// Route probes are bounded independently of movement. Local floor samples keep the bot moving
    /// between probes and around a failed route while normal locomotion owns collision and doors.
    /// </summary>
    internal sealed class FleeBehavior : IBotBehavior, IDescribedBehavior
    {
        private const float ThreatMeters = 25f;
        private const float LocalStepMeters = 2f;
        private const float LocalRefreshSeconds = 0.25f;
        private static readonly float[] LocalAngles = { 0f, 55f, -55f, 100f, -100f, 140f, -140f };
        private static readonly int LocalBlockers = LayerMask.GetMask("Default", "Door", "Glass", "InteractableNoPlayerCollision");
        private const float RepickSeconds = 0.5f;
        private const float ArrivalMeters = 1.5f;
        private const float MinTripMeters = 4f;
        private const float FallbackMeters = 8f;
        private const float FallbackSnapMeters = 4f;
        private const float TowardThreatDot = -0.2f;
        private const float ThreatGainMeters = 2f;
        private const float TripTimeoutSeconds = 12f;
        private const int MaxRoomPathChecks = 1;
        private const int RankedCandidates = 3;

        private readonly HumanBotContext context;
        private readonly FpcBotPlayer bot;
        private readonly List<RoomEntry> firstHop = new();
        private readonly List<RoomEntry> secondHop = new();
        private readonly List<(Vector3 Position, float Score)> ranked = new(RankedCandidates + 1);

        private bool hasDestination;
        private bool repickRequested;
        private Vector3 destination;
        private float nextPickAt;
        private float deadline;
        private int candidateCursor;
        private Vector3 localDestination;
        private bool hasLocalDestination;
        private float nextLocalPickAt;
        private bool sprintHeld;
        private string state = "search";
        private float nextLogAt;

        public FleeBehavior(HumanBotContext context)
        {
            this.context = context;
            bot = context.Bot;
        }

        public string Name => "flee";
        public string Detail => state;

        public bool ShouldPreemptCombat => HumanInventory.IsCivilian(bot.BotHub.PlayerHub.roleManager.CurrentRole.RoleTypeId)
            && Evaluate() > 0f;

        public float Evaluate()
        {
            if (!context.IsHuman)
            {
                return 0f;
            }

            var threats = context.Threats;
            threats.Refresh();
            if (!threats.HasRecentThreat)
            {
                return 0f;
            }

            var civilianScpEscape = !BotServerPolicy.IsStandardWarmup && threats.ThreatIsScp
                && HumanInventory.IsCivilian(bot.BotHub.PlayerHub.roleManager.CurrentRole.RoleTypeId);
            if (!civilianScpEscape && context.Inventory.CanFight)
            {
                return 0f;
            }

            var distance = Vector3.Distance(bot.PlayerPosition, threats.ThreatPosition);
            if (distance > ThreatMeters)
            {
                return 0f;
            }

            var urgency = 1f - distance / ThreatMeters + (threats.SeesThreat ? 0.2f : 0f);
            return BehaviorPriority.Survival + 0.1f + 0.1f * Mathf.Clamp01(urgency);
        }

        public void OnEnter()
        {
            bot.Locomotion.DiscardAbandonRequest();
            hasDestination = false;
            repickRequested = false;
            nextPickAt = 0f;
            nextLocalPickAt = 0f;
            hasLocalDestination = false;
        }

        public void OnExit()
        {
            hasDestination = false;
            hasLocalDestination = false;
            if (sprintHeld && HumanInventory.IsHumanBot(bot.BotHub.PlayerHub.roleManager.CurrentRole))
            {
                bot.FpcRole.FpcModule.CurrentMovementState = PlayerMovementState.Walking;
            }

            sprintHeld = false;
        }

        public bool Tick()
        {
            var threats = context.Threats;
            threats.Refresh();
            if (!threats.HasRecentThreat)
            {
                return false;
            }

            var now = Time.time;
            var position = bot.PlayerPosition;
            var threat = threats.ThreatPosition;
            repickRequested |= bot.Locomotion.ConsumeAbandonRequest();
            var arrived = hasDestination && HorizontalDistance(position, destination) <= ArrivalMeters;
            var overtaken = hasDestination
                            && HorizontalDistance(destination, threat) + ThreatGainMeters < HorizontalDistance(position, threat);

            var partialEnd = hasDestination && bot.Navigator.HasPartialPath
                && HorizontalDistance(position, bot.Navigator.CurrentWaypoint) < 0.6f;
            if (arrived || overtaken || repickRequested || partialEnd || now > deadline)
            {
                hasDestination = false;
            }

            if (!hasDestination && now >= nextPickAt)
            {
                repickRequested = false;
                nextPickAt = now + RepickSeconds;
                hasDestination = TryPick(position, threat, out destination);
                deadline = now + TripTimeoutSeconds;
                SetState(hasDestination ? "route" : "local:route unavailable");
            }

            var stamina = bot.BotHub.PlayerHub.playerStats.GetModule<StaminaStat>().CurValue;
            sprintHeld = sprintHeld ? stamina > 0f : stamina >= 0.1f;
            bot.FpcRole.FpcModule.CurrentMovementState = sprintHeld
                ? PlayerMovementState.Sprinting : PlayerMovementState.Walking;

            if (hasDestination)
            {
                bot.MoveToPosition(destination);
            }
            else if (!MoveLocally(position, threat, now))
            {
                // Only a physically blocked local floor may stop escape, never the route retry timer.
                SetState("blocked:no local floor");
                bot.Locomotion.Stop();
            }

            return true;
        }

        private bool MoveLocally(Vector3 position, Vector3 threat, float now)
        {
            if (hasLocalDestination && HorizontalDistance(position, localDestination) > 0.4f && now < nextLocalPickAt)
            {
                bot.MoveToPosition(localDestination);
                return true;
            }

            if (!hasLocalDestination && now < nextLocalPickAt)
            {
                return false;
            }

            var backend = NavigationSystem.Instance.Backend;
            var away = Vector3.ProjectOnPlane(position - threat, Vector3.up);
            away = away.sqrMagnitude > 0.01f ? away.normalized : -bot.PlayerForward;
            var feet = position - Vector3.up * NavigationAgentProfile.RootHeight;
            hasLocalDestination = false;
            nextLocalPickAt = now + LocalRefreshSeconds;
            if (backend == null)
            {
                return false;
            }

            foreach (var angle in LocalAngles)
            {
                var direction = Quaternion.AngleAxis(angle, Vector3.up) * away;
                if (!backend.TryGetNearestPoint(feet + direction * LocalStepMeters, 0.75f, out var sample, out _)
                    || Mathf.Abs(sample.y - feet.y) > 1f
                    || HorizontalDistance(position, sample) < 0.5f
                    || Physics.Linecast(position, sample + Vector3.up * NavigationAgentProfile.RootHeight,
                        LocalBlockers, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                localDestination = sample;
                hasLocalDestination = true;
                bot.MoveToPosition(localDestination);
                return true;
            }

            return false;
        }

        private void SetState(string value)
        {
            if (state == value)
            {
                return;
            }

            state = value;
            if (Time.time >= nextLogAt)
            {
                nextLogAt = Time.time + 1f;
                var p = bot.PlayerPosition;
                LabLogger.Info($"[BotFlee] bot={bot.BotHub.PlayerHub.nicknameSync.MyNick} state={state} pos=({p.x:F1},{p.y:F1},{p.z:F1})");
            }
        }

        private bool TryPick(Vector3 position, Vector3 threat, out Vector3 picked)
        {
            picked = default;
            var backend = NavigationSystem.Instance.Backend;
            if (backend == null)
            {
                return false;
            }

            var away = Vector3.ProjectOnPlane(position - threat, Vector3.up);
            away = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.ProjectOnPlane(-bot.PlayerForward, Vector3.up).normalized;

            ranked.Clear();
            if (RoomUtils.TryGetRoom(position, out var room) && room != null)
            {
                backend.GetForeignRoomEntries(room, firstHop);
                foreach (var entry in firstHop)
                {
                    if (entry.Room == null || entry.Room.Zone != room.Zone)
                    {
                        continue;
                    }

                    Consider(entry.Position, position, threat, away);
                    backend.GetForeignRoomEntries(entry.Room, secondHop);
                    foreach (var next in secondHop)
                    {
                        if (next.Room != null && next.Room != room && next.Room.Zone == room.Zone)
                        {
                            Consider(next.Position, position, threat, away);
                        }
                    }
                }
            }

            // Reserve a separate check for the fallback, and rotate failed room candidates.
            var checks = Mathf.Min(MaxRoomPathChecks, ranked.Count);
            for (var index = 0; index < checks; index++)
            {
                var candidate = ranked[(candidateCursor + index) % ranked.Count].Position;
                if (bot.Navigator.CanReach(candidate))
                {
                    picked = candidate;
                    candidateCursor++;
                    return true;
                }
            }

            candidateCursor++;
            if (backend.TryGetNearestPoint(position - Vector3.up * NavigationAgentProfile.RootHeight + away * FallbackMeters,
                    FallbackSnapMeters, out var nearest, out _)
                && Mathf.Abs(nearest.y + NavigationAgentProfile.RootHeight - position.y) <= 2f
                && HorizontalDistance(nearest, position) >= MinTripMeters
                && bot.Navigator.CanReach(nearest))
            {
                picked = nearest;
                return true;
            }

            return false;
        }

        private void Consider(Vector3 candidate, Vector3 position, Vector3 threat, Vector3 away)
        {
            var trip = Vector3.ProjectOnPlane(candidate - position, Vector3.up);
            var travel = trip.magnitude;
            if (travel < MinTripMeters)
            {
                return;
            }

            var heading = Vector3.Dot(trip / travel, away);
            if (heading < TowardThreatDot)
            {
                return;
            }

            var score = HorizontalDistance(candidate, threat) - 0.3f * travel + 5f * heading;
            var index = ranked.Count;
            while (index > 0 && ranked[index - 1].Score < score)
            {
                index--;
            }

            if (index >= RankedCandidates)
            {
                return;
            }

            ranked.Insert(index, (candidate, score));
            if (ranked.Count > RankedCandidates)
            {
                ranked.RemoveAt(ranked.Count - 1);
            }
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
