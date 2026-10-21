using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Brain.Human;
using SCPSLBot.AI.FirstPersonControl.Combat;
using SCPSLBot.Navigation;
using UnityEngine;
using LabLogger = LabApi.Features.Console.Logger;

namespace SCPSLBot.AI.FirstPersonControl.Movement
{
    /// <summary>Close a recently crossed door only after observing an SCP following our retreat.</summary>
    internal sealed class PursuitDoorDefense
    {
        private const float SampleSeconds = 0.1f;
        private const float PursuitMemorySeconds = 1.25f;
        private const float PursuitRange = 18f;
        private const float ClearDoorMeters = 0.7f;
        private const float CloseRange = 2f;
        private const float DoorRetrySeconds = 8f;
        private readonly FpcBotPlayer bot;
        private Vector3 previousPosition;
        private bool hasPosition;
        private float nextSample;
        private float observationAt;
        private ReferenceHub observedScp;
        private Vector3 observedScpPosition;
        private Vector3 observedHumanPosition;
        private float pursuedUntil;
        private DoorVariant crossedDoor;
        private Vector3 exitNormal;
        private float crossingExpires;
        private DoorVariant lastClosedDoor;
        private float nextCloseAt;
        private int generation = -1;

        public PursuitDoorDefense(FpcBotPlayer bot) => this.bot = bot;

        public void Reset()
        {
            hasPosition = false;
            observedScp = null;
            observationAt = 0f;
            pursuedUntil = 0f;
            crossedDoor = null;
            lastClosedDoor = null;
            nextSample = nextCloseAt = 0f;
        }

        public void Tick()
        {
            var now = Time.time;
            if (now < nextSample) return;
            nextSample = now + SampleSeconds;
            var hub = bot.BotHub.PlayerHub;
            if (!HumanInventory.IsHumanBot(hub.roleManager.CurrentRole))
            {
                Reset();
                return;
            }

            var map = NavigationSystem.Instance.MapGeneration;
            if (generation != map)
            {
                Reset();
                generation = map;
            }

            var position = bot.PlayerPosition;
            var threats = bot.HumanThreats;
            threats.Refresh();
            ObservePursuit(now, position);
            if (hasPosition && (position - previousPosition).sqrMagnitude < 9f
                && now <= pursuedUntil && RoomUtils.TryGetRoom(position, out var room)
                && DoorVariant.DoorsByRoom.TryGetValue(room, out var doors))
            {
                foreach (var door in doors)
                {
                    // Bulk gates/checkpoints and automatic doors cannot be shut safely while sprinting.
                    if (door is not BasicDoor || DoorTraversal.ResolveInteractable(door) != door
                        || !door.TargetState || !DoorAccess.IsPlayerDoor(door)) continue;
                    var origin = door.transform.position;
                    var normal = Vector3.ProjectOnPlane(door.transform.forward, Vector3.up).normalized;
                    var before = Vector3.Dot(previousPosition - origin, normal);
                    var after = Vector3.Dot(position - origin, normal);
                    if (before * after >= 0f || Mathf.Abs(after - before) < 0.001f) continue;
                    var crossing = Vector3.Lerp(previousPosition, position, -before / (after - before));
                    if (Vector3.ProjectOnPlane(crossing - origin, Vector3.up).sqrMagnitude > 2.2f * 2.2f
                        || Mathf.Abs(crossing.y - NavigationAgentProfile.RootHeight - origin.y) > 1.5f) continue;
                    crossedDoor = door;
                    exitNormal = normal * Mathf.Sign(after);
                    crossingExpires = now + 0.8f;
                    break;
                }
            }
            previousPosition = position;
            hasPosition = true;

            var candidate = crossedDoor;
            if (candidate == null) return;
            if (now > crossingExpires || now > pursuedUntil || !candidate.TargetState
                || !threats.ThreatIsScp || !ReferenceEquals(observedScp, threats.ThreatHub)
                || observedScp == null || !CombatTargetSelector.AreHostile(hub, observedScp))
            {
                crossedDoor = null;
                return;
            }

            var doorPosition = candidate.transform.position;
            var clearance = Vector3.Dot(position - doorPosition, exitNormal);
            // Last noticed position, not an unseen SCP's live transform: no knowledge through walls.
            if (clearance < ClearDoorMeters || Vector3.Dot(threats.ThreatPosition - doorPosition, exitNormal) > -0.5f
                || Vector3.ProjectOnPlane(position - doorPosition, Vector3.up).sqrMagnitude > CloseRange * CloseRange
                || (candidate == lastClosedDoor && now < nextCloseAt)
                || DoorAccess.IsPhasedThrough(observedScp, candidate) || !DoorAccess.IsPassable(candidate)
                || DoorAccess.Evaluate(hub, candidate, out _) != DoorAccessKind.Open) return;

            // Do not slam a door on another human using this crossing, including someone still behind us.
            foreach (var player in CombatWorldSnapshot.Get(now))
            {
                if (player.Hub == hub || !HumanInventory.IsHumanBot(player.Hub.roleManager.CurrentRole)) continue;
                var offset = player.Position - doorPosition;
                if (Mathf.Abs(offset.y) < 3f && Vector3.ProjectOnPlane(offset, Vector3.up).sqrMagnitude < 2.5f * 2.5f)
                    return;
            }

            if (!bot.Locomotion.TryCloseDoor(candidate, CloseRange)) return;
            crossedDoor = null;
            lastClosedDoor = candidate;
            nextCloseAt = now + DoorRetrySeconds;
            LabLogger.Info($"[BotDoorDefense] bot={hub.nicknameSync.MyNick} door={candidate.name} closed=True pursuit={observedScp.nicknameSync.MyNick}");
        }

        private void ObservePursuit(float now, Vector3 position)
        {
            var threats = bot.HumanThreats;
            if (!threats.ThreatIsScp) { pursuedUntil = 0f; return; }
            if (!threats.ThreatIsScp || !threats.SeesThreat || threats.LastSeenAt <= observationAt) return;
            var seenAt = threats.LastSeenAt;
            var elapsed = seenAt - observationAt;
            var away = Vector3.ProjectOnPlane(position - threats.ThreatPosition, Vector3.up);
            if (ReferenceEquals(observedScp, threats.ThreatHub) && elapsed >= 0.15f && elapsed <= 0.75f
                && away.sqrMagnitude <= PursuitRange * PursuitRange && away.sqrMagnitude > 0.01f)
            {
                var direction = away.normalized;
                var scpVelocity = (threats.ThreatPosition - observedScpPosition) / elapsed;
                var humanVelocity = (position - observedHumanPosition) / elapsed;
                if (Vector3.Dot(scpVelocity, direction) >= 1f && Vector3.Dot(humanVelocity, direction) >= 1f)
                    pursuedUntil = now + PursuitMemorySeconds;
            }
            else if (!ReferenceEquals(observedScp, threats.ThreatHub)) pursuedUntil = 0f;
            observedScp = threats.ThreatHub;
            observationAt = seenAt;
            observedScpPosition = threats.ThreatPosition;
            observedHumanPosition = position;
        }
    }
}
