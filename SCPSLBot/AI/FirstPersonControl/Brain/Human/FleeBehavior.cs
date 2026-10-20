using MapGeneration;
using SCPSLBot.Navigation;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Human
{
    /// <summary>
    /// Humans that cannot fight (no firearm that can still fire, outside Standard warmup) move
    /// away from an SCP or armed enemy they noticed. The destination is an entry point one or two
    /// rooms away, scored by distance from the threat, travel distance and heading away from it,
    /// confirmed with at most two path queries; it is kept until reached, until the threat gets
    /// closer to it than the bot is, or until locomotion gives up, and re-picked at most every
    /// two seconds. Highest band: running beats healing or looting.
    /// </summary>
    internal sealed class FleeBehavior : IBotBehavior
    {
        private const float ThreatMeters = 25f;
        private const float RepickSeconds = 2f;
        private const float ArrivalMeters = 1.5f;
        private const float MinTripMeters = 4f;
        private const float FallbackMeters = 8f;
        private const float FallbackSnapMeters = 4f;
        private const float TowardThreatDot = -0.2f;
        private const float ThreatGainMeters = 2f;
        private const float TripTimeoutSeconds = 12f;
        private const int MaxPathChecks = 2;
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

        public FleeBehavior(HumanBotContext context)
        {
            this.context = context;
            bot = context.Bot;
        }

        public string Name => "flee";

        public float Evaluate()
        {
            if (!context.IsHuman || context.Inventory.CanFight)
            {
                return 0f;
            }

            var threats = context.Threats;
            threats.Refresh();
            if (!threats.HasRecentThreat)
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
        }

        public void OnExit()
        {
            hasDestination = false;
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

            // Every pick (path queries included) waits for the repick timer, even without a destination.
            if (now >= nextPickAt && (!hasDestination || repickRequested || now > deadline || arrived || overtaken))
            {
                repickRequested = false;
                nextPickAt = now + RepickSeconds;
                hasDestination = TryPick(position, threat, out destination);
                deadline = now + TripTimeoutSeconds;
                arrived = false;
            }

            if (!hasDestination || arrived)
            {
                bot.Locomotion.Stop();
                bot.LookToPosition(threat + Vector3.up);
                return true;
            }

            bot.MoveToPosition(destination);
            return true;
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

            var checks = 0;
            foreach (var (candidate, _) in ranked)
            {
                if (checks >= MaxPathChecks)
                {
                    break;
                }

                checks++;
                if (bot.Navigator.CanReach(candidate))
                {
                    picked = candidate;
                    return true;
                }
            }

            // Nowhere better known: a reachable spot straight away from the threat.
            if (checks < MaxPathChecks
                && backend.TryGetNearestPoint(position + away * FallbackMeters, FallbackSnapMeters, out var nearest, out _)
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
