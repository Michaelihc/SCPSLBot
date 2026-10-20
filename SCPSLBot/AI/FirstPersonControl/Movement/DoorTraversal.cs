using Interactables;
using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using InventorySystem.Items;
using MapGeneration;
using PlayerRoles;
using Scp914;
using SCPSLBot.Infrastructure;
using SCPSLBot.Navigation;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Movement
{
    internal enum DoorTraversalResult
    {
        /// <summary>No closed door needs attention; keep following the path.</summary>
        Proceed,
        /// <summary>A door ahead is being opened (or is opening); stop forward input so the bot does not grind into it.</summary>
        Hold,
        /// <summary>The door ahead cannot be opened by this bot; the crossing should be avoided.</summary>
        Blocked,
    }

    /// <summary>
    /// Opens the doors on the bot's planned path. Door crossings are found once per plan by
    /// intersecting each path leg with the door planes of the rooms it passes through (state
    /// independent, unlike collider casts that miss open doors which close later), and cached for
    /// the navigator's two plan slots. Per frame only the few cached crossings are checked.
    /// A closed door the bot may open is interacted with natively, equipping a carried keycard
    /// under the bot's item lease when the held item does not satisfy the door; the previous item
    /// is restored once the door is passed or the lease runs out.
    /// </summary>
    internal sealed class DoorTraversal
    {
        private const float RoomProbeStepMeters = 4f;
        // Gates are about four meters wide; side doors never share a leg's crossing plane.
        private const float DoorHalfWidth = 2.2f;
        private const float DoorVerticalTolerance = 3f;
        private const float ActRange = 3f;
        private const float ActVerticalTolerance = 2.5f;
        private const float InteractRange = 2f;
        private const float HoldDistance = 1.1f;
        private const float PassedMeters = 0.35f;
        private const float FirstLegBacktrackMeters = 0.6f;
        private const float InteractRetrySeconds = 0.75f;
        private const int MaxInteractAttempts = 4;
        private const float WaitForSelfOpeningSeconds = 6f;
        private const float MaxClosedSeconds = 10f;
        // HCZ bulk gates take several seconds to open but their exact state advances every frame.
        private const float MaxOpeningStallSeconds = 3f;
        private const float OpeningProgressEpsilon = 0.01f;
        private const float KeycardLeaseSeconds = 6f;

        private readonly struct Crossing
        {
            public Crossing(DoorVariant door, Vector3 point, Vector3 normal, float side)
            {
                Door = door;
                Point = point;
                Normal = normal;
                Side = side;
            }

            public DoorVariant Door { get; }
            /// <summary>Where the path crosses the door plane, at the door's floor height.</summary>
            public Vector3 Point { get; }
            public Vector3 Normal { get; }
            /// <summary>Sign of the approach side relative to <see cref="Normal"/>.</summary>
            public float Side { get; }
        }

        private sealed class ScanCache
        {
            public readonly List<Crossing> Crossings = new();
            public List<Vector3> Points;
            public int PlanVersion = -1;
            public int Generation = -1;
            public float LastUsedAt;

            public bool Matches(List<Vector3> points, int planVersion, int generation)
            {
                return ReferenceEquals(points, Points) && planVersion == PlanVersion && generation == Generation;
            }
        }

        private static readonly Dictionary<DoorVariant, CheckpointDoor> checkpointParents = new(ManagedReferenceComparer<DoorVariant>.Instance);
        private static int subDoorsGeneration = -1;
        private static int subDoorsDoorCount = -1;

        private readonly FpcBotPlayer botPlayer;
        private readonly ScanCache[] caches = { new(), new() };
        private readonly List<RoomIdentifier> segmentRooms = new(8);

        private DoorVariant activeDoor;
        private int attempts;
        private float nextInteractAt;
        private float closedSince;
        private float openingProgress = -1f;
        private float openingProgressAt;

        private DoorVariant keycardDoor;
        private ushort keycardSerial;
        private ushort previousSerial;

        public DoorTraversal(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        /// <summary>The door currently being handled (diagnostics, blocked-crossing memory).</summary>
        public DoorVariant ActiveDoor => activeDoor;

        public string DiagnosticState { get; private set; }

        public bool HoldsKeycard => keycardDoor != null;

        /// <summary>True while the last bot update stopped in front of a door that is being opened.</summary>
        public bool IsHolding { get; private set; }

        private bool doorSeenThisFrame;
        private bool holdingThisFrame;

        public DoorTraversalResult Tick(IBotNavigator navigator)
        {
            var result = Evaluate(navigator);
            if (result == DoorTraversalResult.Hold)
            {
                holdingThisFrame = true;
            }

            return result;
        }

        /// <summary>
        /// Once per bot update. Callers may move several times per frame toward different goals
        /// (combat steers at its target and at a door on the way); only when none of those calls
        /// had a door in range is the door forgotten and the previous item put back.
        /// </summary>
        public void EndFrame()
        {
            IsHolding = holdingThisFrame;
            holdingThisFrame = false;
            if (!doorSeenThisFrame)
            {
                Idle();
            }

            doorSeenThisFrame = false;
        }

        private DoorTraversalResult Evaluate(IBotNavigator navigator)
        {
            var crossings = GetCrossings(navigator.PointsPath, navigator.PlanVersion);
            var position = botPlayer.PlayerPosition;
            var feetY = position.y - NavigationAgentProfile.RootHeight;

            var found = false;
            var nearest = default(Crossing);
            var nearestDistance = float.PositiveInfinity;
            for (var index = 0; index < crossings.Count; index++)
            {
                var crossing = crossings[index];
                if (crossing.Door == null || Mathf.Abs(crossing.Point.y - feetY) > ActVerticalTolerance)
                {
                    continue;
                }

                var distance = HorizontalDistance(position, crossing.Point);
                if (distance > ActRange || distance >= nearestDistance)
                {
                    continue;
                }

                // Already through: on the far side of the door plane.
                if (Vector3.Dot(position - crossing.Point, crossing.Normal) * crossing.Side < -PassedMeters)
                {
                    continue;
                }

                found = true;
                nearest = crossing;
                nearestDistance = distance;
            }

            if (!found)
            {
                return DoorTraversalResult.Proceed;
            }

            if (doorSeenThisFrame && nearest.Door != activeDoor)
            {
                // A second move this frame (combat's helper goal) sees another door: leave the
                // active door's attempt counters alone so its limits still apply.
                return nearestDistance < HoldDistance && !nearest.Door.TargetState ? DoorTraversalResult.Hold : DoorTraversalResult.Proceed;
            }

            doorSeenThisFrame = true;
            return HandleDoor(nearest.Door, nearest.Point, nearestDistance);
        }

        /// <summary>No door to handle: forget the active door and put the previous item back.</summary>
        public void Idle()
        {
            IsHolding = false;
            holdingThisFrame = false;
            doorSeenThisFrame = false;
            activeDoor = null;
            DiagnosticState = null;
            RestoreItem();
        }

        public void Reset()
        {
            Idle();
            botPlayer.ItemLease.Release(this);
        }

        /// <summary>
        /// One-shot open attempt for a door found by other means (the stuck ladder's camera ray).
        /// Applies the same permission rules and keycard swap; returns true when an interaction was sent.
        /// </summary>
        public bool TryOpenNow(DoorVariant door)
        {
            if (door == null || door.TargetState)
            {
                return false;
            }

            var access = DoorAccess.Evaluate(botPlayer.BotHub.PlayerHub, door, out var keycard);
            if (access == DoorAccessKind.NeedsKeycard && !EquipKeycard(door, keycard))
            {
                return false;
            }

            if (access is not (DoorAccessKind.Open or DoorAccessKind.NeedsKeycard))
            {
                return false;
            }

            return Interact(door);
        }

        private DoorTraversalResult HandleDoor(DoorVariant door, Vector3 crossingPoint, float distance)
        {
            var now = Time.time;
            if (door != activeDoor)
            {
                activeDoor = door;
                attempts = 0;
                nextInteractAt = 0f;
                closedSince = now;
                openingProgress = -1f;
                openingProgressAt = now;
            }

            var hub = botPlayer.BotHub.PlayerHub;
            if (hub.roleManager.CurrentRole.RoleTypeId == RoleTypeId.Scp106
                && door is IScp106PassableDoor { IsScp106Passable: true })
            {
                // SCP-106 walks through it (Scp106MovementModule); nothing to open.
                DiagnosticState = null;
                return DoorTraversalResult.Proceed;
            }

            if (door.TargetState)
            {
                closedSince = now;
                if (DoorAccess.IsPassable(door))
                {
                    DiagnosticState = null;
                    return DoorTraversalResult.Proceed;
                }

                // Opening doors keep moving; one that stops short of open (a refused animation,
                // a jammed leaf) is reported instead of holding the bot until its goal times out.
                var exactState = door.GetExactState();
                if (exactState > openingProgress + OpeningProgressEpsilon)
                {
                    openingProgress = exactState;
                    openingProgressAt = now;
                }

                if (distance < HoldDistance && now - openingProgressAt > MaxOpeningStallSeconds)
                {
                    DiagnosticState = "door:stalled";
                    return DoorTraversalResult.Blocked;
                }

                DiagnosticState = "door:opening";
                return distance < HoldDistance ? DoorTraversalResult.Hold : DoorTraversalResult.Proceed;
            }

            openingProgress = -1f;
            openingProgressAt = now;

            if (distance <= InteractRange && now - closedSince > MaxClosedSeconds)
            {
                // Hands never freed, or the door never accepted an interaction.
                DiagnosticState = "door:refused";
                return DoorTraversalResult.Blocked;
            }

            var access = DoorAccess.Evaluate(hub, door, out var keycard);
            switch (access)
            {
                case DoorAccessKind.Denied:
                    DiagnosticState = "door:denied";
                    return DoorTraversalResult.Blocked;

                case DoorAccessKind.Wait:
                    if (IsScp914Running())
                    {
                        // SCP-914 chamber doors reopen when the refine ends.
                        closedSince = now;
                    }

                    if (now - closedSince > WaitForSelfOpeningSeconds)
                    {
                        DiagnosticState = "door:denied";
                        return DoorTraversalResult.Blocked;
                    }

                    DiagnosticState = "door:wait";
                    return distance < HoldDistance ? DoorTraversalResult.Hold : DoorTraversalResult.Proceed;
            }

            if (distance > InteractRange)
            {
                DiagnosticState = "door:approach";
                return DoorTraversalResult.Proceed;
            }

            if (access == DoorAccessKind.NeedsKeycard)
            {
                DiagnosticState = "door:keycard";
                if (!EquipKeycard(door, keycard))
                {
                    // Hands are claimed by something else, or the held item refuses to holster
                    // (mid-reload); wait at the door instead of walking into it.
                    return distance < HoldDistance ? DoorTraversalResult.Hold : DoorTraversalResult.Proceed;
                }
            }
            else
            {
                DiagnosticState = "door:open";
            }

            var target = GetInteractionPoint(door, crossingPoint, out var colliderId);
            botPlayer.LookToPosition(target);
            // Animation cooldowns and a checkpoint finishing its closing sequence refuse input for
            // a moment; wait those out (bounded by MaxClosedSeconds) instead of counting them.
            if (now >= nextInteractAt && door.AllowInteracting(hub, colliderId))
            {
                if (attempts >= MaxInteractAttempts)
                {
                    // Accepted input never opened it: a plugin or native rule keeps refusing.
                    DiagnosticState = "door:refused";
                    return DoorTraversalResult.Blocked;
                }

                attempts++;
                nextInteractAt = now + InteractRetrySeconds;
                door.ServerInteract(hub, colliderId);
            }

            return distance < HoldDistance ? DoorTraversalResult.Hold : DoorTraversalResult.Proceed;
        }

        private bool EquipKeycard(DoorVariant door, ItemBase keycard)
        {
            var hub = botPlayer.BotHub.PlayerHub;
            var inventory = hub.inventory;
            if (keycard == null || inventory == null)
            {
                return false;
            }

            var lease = botPlayer.ItemLease;
            if (!lease.Acquire(this, KeycardLeaseSeconds))
            {
                return false;
            }

            if (keycardDoor == null)
            {
                previousSerial = inventory.CurItem.SerialNumber;
            }

            var swapStarted = keycardDoor == null;
            keycardDoor = door;
            keycardSerial = keycard.ItemSerial;
            if (inventory.CurItem.SerialNumber != keycardSerial)
            {
                inventory.ServerSelectItem(keycardSerial);
            }

            if (inventory.CurItem.SerialNumber == keycardSerial && inventory.CurInstance == keycard)
            {
                return true;
            }

            if (swapStarted)
            {
                // Refused (the held item cannot holster right now): nothing to restore, so do not
                // keep the hands claimed while waiting to retry.
                keycardDoor = null;
                lease.Release(this);
            }

            return false;
        }

        private static bool IsScp914Running()
        {
            var controller = Scp914Controller.Singleton;
            return controller != null && (controller.IsUpgrading || controller.RemainingCooldown > 0f);
        }

        private void RestoreItem()
        {
            if (keycardDoor == null)
            {
                return;
            }

            // Only put the previous item back while the keycard is still in hand: once the lease
            // expired, combat (or anything else) may already have chosen what to hold.
            var hub = botPlayer.BotHub?.PlayerHub;
            var inventory = hub != null ? hub.inventory : null;
            if (inventory != null
                && inventory.CurItem.SerialNumber == keycardSerial
                && previousSerial != keycardSerial
                && (previousSerial == 0 || inventory.UserInventory.Items.ContainsKey(previousSerial)))
            {
                inventory.ServerSelectItem(previousSerial);
            }

            keycardDoor = null;
            botPlayer.ItemLease.Release(this);
        }

        private bool Interact(DoorVariant door)
        {
            GetInteractionPoint(door, door.transform.position, out var colliderId);
            door.ServerInteract(botPlayer.BotHub.PlayerHub, colliderId);
            return true;
        }

        // Buttons for keycard doors sit beside the opening; use (and look at) the nearest one so
        // the bot visibly reaches for the panel. Falls back to the doorway at chest height.
        private Vector3 GetInteractionPoint(DoorVariant door, Vector3 crossingPoint, out byte colliderId)
        {
            var camera = botPlayer.CameraPosition;
            var point = crossingPoint + Vector3.up;
            colliderId = 0;
            if (InteractableCollider.AllInstances.TryGetValue(door, out var colliders))
            {
                var best = float.PositiveInfinity;
                foreach (var collider in colliders.Values)
                {
                    if (collider == null)
                    {
                        continue;
                    }

                    var position = collider.transform.position;
                    var distance = (position - camera).sqrMagnitude;
                    if (distance < best)
                    {
                        best = distance;
                        point = position;
                        colliderId = collider.ColliderId;
                    }
                }
            }

            return point;
        }

        // Keyed by the plan's own version: a replan toward the same goal from the same spot can
        // keep the list, its count and its endpoints yet route through another door.
        private List<Crossing> GetCrossings(List<Vector3> points, int planVersion)
        {
            var generation = NavigationSystem.Instance.MapGeneration;
            ScanCache oldest = caches[0];
            foreach (var cache in caches)
            {
                if (points != null && cache.Matches(points, planVersion, generation))
                {
                    cache.LastUsedAt = Time.time;
                    return cache.Crossings;
                }

                if (cache.LastUsedAt < oldest.LastUsedAt)
                {
                    oldest = cache;
                }
            }

            oldest.Points = points;
            oldest.PlanVersion = planVersion;
            oldest.Generation = generation;
            oldest.LastUsedAt = Time.time;
            Scan(points, oldest.Crossings);
            return oldest.Crossings;
        }

        private void Scan(List<Vector3> points, List<Crossing> output)
        {
            output.Clear();
            if (points == null || points.Count < 2)
            {
                return;
            }

            RefreshCheckpointSubDoors();
            for (var index = 0; index + 1 < points.Count; index++)
            {
                var a = points[index];
                var b = points[index + 1];
                if (index == 0)
                {
                    // A plan made while standing in a doorway starts on or just past the door
                    // plane; without reaching back the leg never crosses it and a door that
                    // closed in the bot's face is walked into instead of opened.
                    var leg = b - a;
                    leg.y = 0f;
                    if (leg.sqrMagnitude > 1e-4f)
                    {
                        a -= leg.normalized * FirstLegBacktrackMeters;
                    }
                }

                CollectRooms(a, b);
                foreach (var room in segmentRooms)
                {
                    if (!DoorVariant.DoorsByRoom.TryGetValue(room, out var doors))
                    {
                        continue;
                    }

                    foreach (var door in doors)
                    {
                        TestDoor(door, a, b, output);
                    }
                }
            }
        }

        // Rooms are ~15 m cells; probing every few meters catches every room a long straight leg
        // passes through, not just the rooms of its endpoints.
        private void CollectRooms(Vector3 a, Vector3 b)
        {
            segmentRooms.Clear();
            var steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / RoomProbeStepMeters));
            for (var step = 0; step <= steps; step++)
            {
                var probe = Vector3.Lerp(a, b, step / (float)steps);
                if (RoomUtils.TryGetRoom(probe, out var room) && room != null && !segmentRooms.Contains(room))
                {
                    segmentRooms.Add(room);
                }
            }
        }

        private static void TestDoor(DoorVariant door, Vector3 a, Vector3 b, List<Crossing> output)
        {
            if (door == null || door is ElevatorDoor || door is DummyDoor || checkpointParents.ContainsKey(door))
            {
                return;
            }

            foreach (var existing in output)
            {
                if (existing.Door == door)
                {
                    return;
                }
            }

            if (door is CheckpointDoor { SubDoors: { Length: > 0 } subDoors })
            {
                // The leaves define the doorway: a checkpoint's own transform can face across
                // the passage (the HCZ-EZ checkpoints), so its plane is never crossed.
                foreach (var subDoor in subDoors)
                {
                    if (subDoor != null && TryCross(subDoor.transform, a, b, out var leafCrossing))
                    {
                        output.Add(new Crossing(door, leafCrossing.Point, leafCrossing.Normal, leafCrossing.Side));
                        return;
                    }
                }

                return;
            }

            if (TryCross(door.transform, a, b, out var crossing))
            {
                output.Add(new Crossing(door, crossing.Point, crossing.Normal, crossing.Side));
            }
        }

        private static bool TryCross(Transform doorTransform, Vector3 a, Vector3 b, out Crossing crossing)
        {
            crossing = default;
            var normal = Vector3.ProjectOnPlane(doorTransform.forward, Vector3.up);
            if (normal.sqrMagnitude < 1e-4f)
            {
                return false;
            }

            normal.Normalize();
            var origin = doorTransform.position;
            var da = Vector3.Dot(a - origin, normal);
            var db = Vector3.Dot(b - origin, normal);
            if ((da > 0f) == (db > 0f) || Mathf.Abs(da - db) < 1e-4f)
            {
                return false;
            }

            var point = a + (b - a) * (da / (da - db));
            if (HorizontalDistance(point, origin) > DoorHalfWidth || Mathf.Abs(point.y - origin.y) > DoorVerticalTolerance)
            {
                return false;
            }

            crossing = new Crossing(null, new Vector3(point.x, origin.y, point.z), normal, da >= 0f ? 1f : -1f);
            return true;
        }

        /// <summary>
        /// The door a player interacts with for <paramref name="door"/>: its checkpoint for a
        /// checkpoint leaf (the leaf's own permissions are empty), otherwise the door itself.
        /// </summary>
        public static DoorVariant ResolveInteractable(DoorVariant door)
        {
            if (door == null)
            {
                return null;
            }

            RefreshCheckpointSubDoors();
            return checkpointParents.TryGetValue(door, out var checkpoint) && checkpoint != null ? checkpoint : door;
        }

        // Checkpoint leaves are doors of their own sharing the checkpoint's doorway; only the
        // checkpoint itself takes interactions.
        private static void RefreshCheckpointSubDoors()
        {
            var generation = NavigationSystem.Instance.MapGeneration;
            var doorCount = DoorVariant.AllDoors.Count;
            if (generation == subDoorsGeneration && doorCount == subDoorsDoorCount)
            {
                return;
            }

            subDoorsGeneration = generation;
            subDoorsDoorCount = doorCount;
            checkpointParents.Clear();
            foreach (var door in DoorVariant.AllDoors)
            {
                if (door is CheckpointDoor checkpoint && checkpoint.SubDoors != null)
                {
                    foreach (var subDoor in checkpoint.SubDoors)
                    {
                        if (subDoor != null)
                        {
                            checkpointParents[subDoor] = checkpoint;
                        }
                    }
                }
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
