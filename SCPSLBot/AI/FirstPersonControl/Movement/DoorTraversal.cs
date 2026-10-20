using Interactables;
using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using InventorySystem.Items;
using MapGeneration;
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
        private const float DoorHalfWidth = 1.8f;
        private const float DoorVerticalTolerance = 3f;
        private const float ActRange = 3f;
        private const float ActVerticalTolerance = 2.5f;
        private const float InteractRange = 2f;
        private const float HoldDistance = 1.1f;
        private const float PassedMeters = 0.35f;
        private const float InteractRetrySeconds = 0.75f;
        private const int MaxInteractAttempts = 4;
        private const float WaitForSelfOpeningSeconds = 6f;
        private const float MaxClosedSeconds = 10f;
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
            public int Count = -1;
            public Vector3 First;
            public Vector3 Last;
            public int Generation = -1;
            public float LastUsedAt;

            public bool Matches(List<Vector3> points, int generation)
            {
                return ReferenceEquals(points, Points)
                       && points.Count == Count
                       && generation == Generation
                       && (Count == 0 || (points[0] == First && points[Count - 1] == Last));
            }
        }

        private static readonly HashSet<DoorVariant> checkpointSubDoors = new(ManagedReferenceComparer<DoorVariant>.Instance);
        private static int subDoorsGeneration = -1;
        private static int subDoorsDoorCount = -1;

        private readonly FpcBotPlayer botPlayer;
        private readonly ScanCache[] caches = { new(), new() };
        private readonly List<RoomIdentifier> segmentRooms = new(8);

        private DoorVariant activeDoor;
        private int attempts;
        private float nextInteractAt;
        private float closedSince;

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
            var crossings = GetCrossings(navigator.PointsPath);
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
            }

            if (door.TargetState)
            {
                closedSince = now;
                if (door.IsConsideredOpen())
                {
                    DiagnosticState = null;
                    return DoorTraversalResult.Proceed;
                }

                DiagnosticState = "door:opening";
                return distance < HoldDistance ? DoorTraversalResult.Hold : DoorTraversalResult.Proceed;
            }

            if (distance <= InteractRange && now - closedSince > MaxClosedSeconds)
            {
                // Hands never freed, or the door never accepted an interaction.
                DiagnosticState = "door:refused";
                return DoorTraversalResult.Blocked;
            }

            var hub = botPlayer.BotHub.PlayerHub;
            var access = DoorAccess.Evaluate(hub, door, out var keycard);
            switch (access)
            {
                case DoorAccessKind.Denied:
                    DiagnosticState = "door:denied";
                    return DoorTraversalResult.Blocked;

                case DoorAccessKind.Wait:
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

            keycardDoor = door;
            keycardSerial = keycard.ItemSerial;
            if (inventory.CurItem.SerialNumber != keycardSerial)
            {
                inventory.ServerSelectItem(keycardSerial);
            }

            return inventory.CurItem.SerialNumber == keycardSerial && inventory.CurInstance == keycard;
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

        private List<Crossing> GetCrossings(List<Vector3> points)
        {
            var generation = NavigationSystem.Instance.MapGeneration;
            ScanCache oldest = caches[0];
            foreach (var cache in caches)
            {
                if (points != null && cache.Matches(points, generation))
                {
                    cache.LastUsedAt = Time.time;
                    return cache.Crossings;
                }

                if (cache.LastUsedAt < oldest.LastUsedAt)
                {
                    oldest = cache;
                }
            }

            var count = points?.Count ?? 0;
            oldest.Points = points;
            oldest.Count = count;
            oldest.First = count > 0 ? points![0] : default;
            oldest.Last = count > 0 ? points![count - 1] : default;
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
            if (door == null || door is ElevatorDoor || door is DummyDoor || checkpointSubDoors.Contains(door))
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

            var doorTransform = door.transform;
            var normal = Vector3.ProjectOnPlane(doorTransform.forward, Vector3.up);
            if (normal.sqrMagnitude < 1e-4f)
            {
                return;
            }

            normal.Normalize();
            var origin = doorTransform.position;
            var da = Vector3.Dot(a - origin, normal);
            var db = Vector3.Dot(b - origin, normal);
            if ((da > 0f) == (db > 0f) || Mathf.Abs(da - db) < 1e-4f)
            {
                return;
            }

            var crossing = a + (b - a) * (da / (da - db));
            if (HorizontalDistance(crossing, origin) > DoorHalfWidth || Mathf.Abs(crossing.y - origin.y) > DoorVerticalTolerance)
            {
                return;
            }

            output.Add(new Crossing(door, new Vector3(crossing.x, origin.y, crossing.z), normal, da >= 0f ? 1f : -1f));
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
            checkpointSubDoors.Clear();
            foreach (var door in DoorVariant.AllDoors)
            {
                if (door is CheckpointDoor checkpoint && checkpoint.SubDoors != null)
                {
                    foreach (var subDoor in checkpoint.SubDoors)
                    {
                        if (subDoor != null)
                        {
                            checkpointSubDoors.Add(subDoor);
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
