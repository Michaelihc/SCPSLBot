using AdminToys;
using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using SCPSLBot.Navigation.Policy;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using LabLogger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Navigation.Runtime
{
    /// <summary>
    /// Gathers what the runtime navmesh is built from: the live physics colliders inside the
    /// facility (minus players, doors, pickups, elevator chambers and other non-blocking objects),
    /// one area-modifier box per keycard door, and probe-defined floors for rooms whose collider
    /// meshes the builder cannot read. The result is hashed so a reconciliation can tell whether
    /// anything changed before asking Unity to rebuild tiles.
    /// </summary>
    internal sealed class NavMeshSourceCollector
    {
        // Exactly the layers the human CharacterController collides with (walls, floors, glass,
        // invisible walls, clutter), minus players, hitboxes and door leaves. Camera-only (CCTV),
        // interaction-only and trigger layers never reach the builder this way.
        public static int SourceLayerMask => PlayerRoles.FirstPersonControl.FpcStateProcessor.Mask & ~LayerMask.GetMask("Player", "Hitbox", "Door");

        private readonly Dictionary<int, bool> meshUsableById = new();
        private readonly HashSet<string> loggedUnusableMeshes = new();
        private readonly List<NavMeshBuildMarkup> markups = new();
        private readonly Dictionary<RoomIdentifier, UnityEngine.Mesh> fallbackFloors = new();
        private static readonly string[] KnownMovingRoots = { "Chopper", "Capybara" };
        private readonly Dictionary<DoorVariant, (Vector3 Center, Vector3 Size)> doorBoxCache = new();
        private const float MinimumDoorWidth = 3f;
        private const float CheckpointGateWidth = 10f;
        private readonly List<Collider> colliderScratch = new();
        private readonly HashSet<Collider> doorLeafScratch = new();
        private readonly List<NavMeshBuildSource> customSourceScratch = new();
        private readonly HashSet<int> sourceIds = new();
        private readonly HashSet<int> customSourceIds = new();
        private static readonly int GlassLayer = LayerMask.NameToLayer("Glass");

        public DoorAreaRegistry Areas { get; } = new();

        /// <summary>Mesh sources dropped because Unity cannot read their triangles (unreadable or empty).</summary>
        public int UnusableMeshSources { get; private set; }

        public int ModifierBoxes { get; private set; }

        /// <summary>Trigger colliders dropped from the last collection.</summary>
        public int TriggerSources { get; private set; }

        public int IgnoredRoots { get; private set; }
        public int CustomSources { get; private set; }

        public int FallbackFloorRooms => fallbackFloors.Count;

        /// <summary>Rooms with at least one unusable collider mesh, keyed for the fallback floor probe.</summary>
        public HashSet<RoomIdentifier> RoomsWithUnusableMeshes { get; } = new();

        public void Clear()
        {
            meshUsableById.Clear();
            loggedUnusableMeshes.Clear();
            markups.Clear();
            foreach (var mesh in fallbackFloors.Values)
            {
                if (mesh != null)
                {
                    UnityEngine.Object.Destroy(mesh);
                }
            }

            fallbackFloors.Clear();
            RoomsWithUnusableMeshes.Clear();
            previousComponents.Clear();
            currentComponents.Clear();
            previousPositions.Clear();
            currentPositions.Clear();
            previousBasis.Clear();
            currentBasis.Clear();
            loggedModifierBoxes = false;
            doorBoxCache.Clear();
            LastChangeSummary = string.Empty;
            Areas.Clear();
            UnusableMeshSources = 0;
            ModifierBoxes = 0;
            IgnoredRoots = 0;
            CustomSources = 0;
            customSourceScratch.Clear();
            sourceIds.Clear();
            customSourceIds.Clear();
        }

        public void SetFallbackFloor(RoomIdentifier room, UnityEngine.Mesh floor)
        {
            if (fallbackFloors.TryGetValue(room, out var previous) && previous != null && previous != floor)
            {
                UnityEngine.Object.Destroy(previous);
            }

            fallbackFloors[room] = floor;
        }

        /// <summary>
        /// Fills <paramref name="sources"/> for <paramref name="bounds"/>. Returns a hash of the
        /// collected geometry so callers can skip unchanged rebuilds.
        /// </summary>
        public ulong Collect(Bounds bounds, bool keycardAreaRouting, List<NavMeshBuildSource> sources, Bounds? customBounds = null)
        {
            sources.Clear();
            RoomsWithUnusableMeshes.Clear();
            UnusableMeshSources = 0;
            ModifierBoxes = 0;

            CollectMarkups();
            NavMeshBuilder.CollectSources(bounds, SourceLayerMask, NavMeshCollectGeometry.PhysicsColliders, DoorAreaRegistry.WalkableArea, markups, sources);
            CustomSources = 0;
            customSourceIds.Clear();
            if (customBounds.HasValue)
            {
                // Collect the two volumes separately: the empty span between an off-map level and
                // the facility must not bring unrelated geometry into the bake. The same actor and
                // moving-object exclusions apply to both, and overlapping colliders are included once.
                sourceIds.Clear();
                foreach (var source in sources)
                    if (source.component != null) sourceIds.Add(source.component.GetInstanceID());
                customSourceScratch.Clear();
                NavMeshBuilder.CollectSources(customBounds.Value, SourceLayerMask, NavMeshCollectGeometry.PhysicsColliders,
                    DoorAreaRegistry.WalkableArea, markups, customSourceScratch);
                foreach (var source in customSourceScratch)
                {
                    if (source.component != null && !sourceIds.Add(source.component.GetInstanceID())) continue;
                    sources.Add(source);
                    if (source.component != null) customSourceIds.Add(source.component.GetInstanceID());
                }
            }

            // Drop trigger volumes (the builder collects them like solid colliders) and mesh sources
            // the builder cannot read; remember the latter's rooms for the floor probe.
            TriggerSources = 0;
            for (var index = sources.Count - 1; index >= 0; index--)
            {
                var source = sources[index];
                if (source.component is Collider trigger && trigger.isTrigger)
                {
                    TriggerSources++;
                    sources.RemoveAt(index);
                    continue;
                }

                if (source.shape != NavMeshBuildSourceShape.Mesh)
                {
                    continue;
                }

                if (source.sourceObject is UnityEngine.Mesh mesh && IsUsable(mesh))
                {
                    continue;
                }

                UnusableMeshSources++;
                var room = source.component != null ? source.component.GetComponentInParent<RoomIdentifier>() : null;
                if (room != null)
                {
                    RoomsWithUnusableMeshes.Add(room);
                }

                sources.RemoveAt(index);
            }

            // Rooms defined by floor probes: every remaining source under that room is replaced by
            // the probed floor so the builder erodes only from the probed boundary (the probe already
            // applied the capsule), not twice.
            if (fallbackFloors.Count > 0)
            {
                for (var index = sources.Count - 1; index >= 0; index--)
                {
                    var source = sources[index];
                    var room = source.component != null ? source.component.GetComponentInParent<RoomIdentifier>() : null;
                    if (room != null && fallbackFloors.ContainsKey(room))
                    {
                        sources.RemoveAt(index);
                    }
                }

                foreach (var pair in fallbackFloors)
                {
                    if (pair.Value == null)
                    {
                        continue;
                    }

                    sources.Add(new NavMeshBuildSource
                    {
                        shape = NavMeshBuildSourceShape.Mesh,
                        sourceObject = pair.Value,
                        transform = Matrix4x4.identity,
                        area = DoorAreaRegistry.WalkableArea,
                    });
                }
            }

            if (keycardAreaRouting)
            {
                AddKeycardModifierBoxes(sources);
            }

            RecordComponents(sources);
            foreach (var source in sources)
                if (source.component != null && customSourceIds.Contains(source.component.GetInstanceID())) CustomSources++;
            return Hash(sources);
        }

        private readonly Dictionary<int, string> previousComponents = new();
        private readonly Dictionary<int, string> currentComponents = new();
        private readonly Dictionary<int, Vector3> previousPositions = new();
        private readonly Dictionary<int, Vector3> currentPositions = new();
        private readonly Dictionary<int, Component> sourceComponents = new();
        private readonly Dictionary<int, Vector3> previousBasis = new();
        private readonly Dictionary<int, Vector3> currentBasis = new();

        private static string HierarchyPath(Transform transform)
        {
            var parts = new List<string>();
            var depth = 0;
            while (transform != null && depth++ < 6)
            {
                parts.Add(transform.name + (transform.GetComponent<ReferenceHub>() != null ? "(hub)" : string.Empty));
                transform = transform.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>Describes the components that appeared/disappeared since the previous collection (diagnostics for reconcile churn).</summary>
        public string LastChangeSummary { get; private set; } = string.Empty;

        private void RecordComponents(List<NavMeshBuildSource> sources)
        {
            currentComponents.Clear();
            currentPositions.Clear();
            currentBasis.Clear();
            sourceComponents.Clear();
            foreach (var source in sources)
            {
                if (source.component == null)
                {
                    continue;
                }

                var id = source.component.GetInstanceID();
                if (!currentComponents.ContainsKey(id))
                {
                    currentComponents[id] = $"{source.component.transform.root.name}:{source.component.name}/{LayerMask.LayerToName(source.component.gameObject.layer)}/{source.shape}";
                    currentPositions[id] = new Vector3(source.transform.m03, source.transform.m13, source.transform.m23);
                    currentBasis[id] = new Vector3(source.transform.m00, source.transform.m02, source.transform.m11) + source.size * 0.001f;
                    sourceComponents[id] = source.component;
                }
            }

            if (previousComponents.Count > 0)
            {
                var added = new List<string>();
                var removed = new List<string>();
                foreach (var pair in currentComponents)
                {
                    if (!previousComponents.ContainsKey(pair.Key) && added.Count < 6)
                    {
                        added.Add(pair.Value);
                    }
                }

                foreach (var pair in previousComponents)
                {
                    if (!currentComponents.ContainsKey(pair.Key) && removed.Count < 6)
                    {
                        removed.Add(pair.Value);
                    }
                }

                var moved = new List<string>();
                var movedPaths = new List<string>();
                foreach (var pair in currentBasis)
                {
                    if (previousBasis.TryGetValue(pair.Key, out var previousB) && (previousB - pair.Value).sqrMagnitude > 1e-6f && moved.Count < 6)
                    {
                        moved.Add($"{currentComponents[pair.Key]}~rot/scale");
                        if (movedPaths.Count < 2 && sourceComponents.TryGetValue(pair.Key, out var rotated) && rotated != null)
                        {
                            movedPaths.Add(HierarchyPath(rotated.transform));
                        }
                    }
                }

                foreach (var pair in currentPositions)
                {
                    if (previousPositions.TryGetValue(pair.Key, out var previous) && (previous - pair.Value).sqrMagnitude > 0.0004f && moved.Count < 6)
                    {
                        moved.Add($"{currentComponents[pair.Key]}@{(previous - pair.Value).magnitude:F2}m");
                        if (movedPaths.Count < 2 && sourceComponents.TryGetValue(pair.Key, out var component) && component != null)
                        {
                            movedPaths.Add(HierarchyPath(component.transform));
                        }
                    }
                }

                LastChangeSummary = added.Count == 0 && removed.Count == 0 && moved.Count == 0
                    ? "resized-or-rotated"
                    : $"added=[{string.Join(",", added)}] removed=[{string.Join(",", removed)}] moved=[{string.Join(",", moved)}] movedPaths=[{string.Join(" | ", movedPaths)}]";
            }

            previousComponents.Clear();
            previousPositions.Clear();
            previousBasis.Clear();
            foreach (var pair in currentComponents)
            {
                previousComponents[pair.Key] = pair.Value;
                previousPositions[pair.Key] = currentPositions[pair.Key];
                previousBasis[pair.Key] = currentBasis[pair.Key];
            }
        }

        private void CollectMarkups()
        {
            markups.Clear();
            IgnoredRoots = 0;

            // Elevator chambers move; their floor is never part of the static walkable surface.
            foreach (var chamber in ElevatorChamber.AllChambers)
            {
                if (chamber != null)
                {
                    Ignore(chamber.transform);
                }
            }

            // Players and dummies carry colliders beyond the Player/Hitbox layers; none of them is
            // walkable geometry and their movement must never trigger a tile rebuild.
            foreach (var hub in ReferenceHub.AllHubs)
            {
                if (hub != null)
                {
                    Ignore(hub.transform);
                }
            }

            // Door leaves are not geometry (bots open them): ignore the door's own toggling
            // colliders and its glass panes, which move with the leaf. Everything else in the
            // prefab stays: several room doors carry the floor sill that joins the two rooms, and
            // dropping it leaves a gap in the surface exactly at the door plane.
            foreach (var door in DoorVariant.AllDoors)
            {
                if (door == null || door is ElevatorDoor)
                {
                    continue;
                }

                doorLeafScratch.Clear();
                if (door.AllColliders != null)
                {
                    foreach (var leaf in door.AllColliders)
                    {
                        if (leaf != null)
                        {
                            doorLeafScratch.Add(leaf);
                        }
                    }
                }

                colliderScratch.Clear();
                door.GetComponentsInChildren(true, colliderScratch);
                foreach (var collider in colliderScratch)
                {
                    if (collider == null || collider.transform == door.transform)
                    {
                        continue;
                    }

                    if (doorLeafScratch.Contains(collider) || collider.gameObject.layer == GlassLayer)
                    {
                        Ignore(collider.transform);
                    }
                }
            }

            // Invisible colliders that overlap a doorway (anti-exploit blockers the game toggles
            // around SCP chambers and special rooms) are not walls; players walk through the door.
            var invisibleLayer = LayerMask.NameToLayer("InvisibleCollider");
            if (invisibleLayer >= 0)
            {
                foreach (var door in DoorVariant.AllDoors)
                {
                    if (door == null || door is ElevatorDoor)
                    {
                        continue;
                    }

                    var doorway = new Bounds(door.transform.position + Vector3.up * 1.2f, new Vector3(2.4f, 2.6f, 2.4f));
                    foreach (var hit in Physics.OverlapBox(doorway.center, doorway.extents, Quaternion.identity, 1 << invisibleLayer, QueryTriggerInteraction.Ignore))
                    {
                        if (hit != null && !hit.transform.IsChildOf(door.transform))
                        {
                            Ignore(hit.transform);
                        }
                    }
                }
            }

            // Ragdolls settle for a long time and carry colliders on several layers.
            foreach (var ragdoll in PlayerRoles.Ragdolls.RagdollManager.AllRagdolls)
            {
                if (ragdoll != null)
                {
                    Ignore(ragdoll.transform);
                }
            }

            // Pickups are small dynamic objects bots walk over or around locally.
            foreach (var pickup in LabApi.Features.Wrappers.Pickup.List)
            {
                if (pickup?.Base != null)
                {
                    Ignore(pickup.Base.transform);
                }
            }

            // Known movers under the dynamic child container: the Surface helicopter (OH-58D) and
            // the wandering capybara. Neither is walkable geometry, and their motion would force a
            // tile rebuild on every reconciliation.
            var dynamicContainer = GameObject.Find("DynamicChild Container");
            if (dynamicContainer != null)
            {
                foreach (Transform child in dynamicContainer.transform)
                {
                    if (child != null && Array.IndexOf(KnownMovingRoots, child.name) >= 0)
                    {
                        Ignore(child);
                    }
                }
            }

            // A non-static waypoint can carry static child primitives (aircraft or boats). Exclude
            // the entire moving hierarchy so those colliders never become floors or churn tiles.
            // Stationary platforms must mark their complete toy hierarchy static.
            foreach (var toy in UnityEngine.Object.FindObjectsByType<AdminToyBase>(FindObjectsSortMode.None))
            {
                if (toy != null && (!toy.IsStatic
                    || toy is PrimitiveObjectToy primitive && (primitive.PrimitiveFlags & PrimitiveFlags.Collidable) == 0))
                {
                    Ignore(toy.transform);
                }
            }
        }

        private void Ignore(Transform root)
        {
            markups.Add(new NavMeshBuildMarkup { root = root, ignoreFromBuild = true });
            IgnoredRoots++;
        }

        private bool IsUsable(UnityEngine.Mesh mesh)
        {
            var id = mesh.GetInstanceID();
            if (meshUsableById.TryGetValue(id, out var usable))
            {
                return usable;
            }

            usable = false;
            try
            {
                if (mesh.isReadable && mesh.vertexCount > 0)
                {
                    usable = mesh.triangles.Length >= 3;
                }
            }
            catch (Exception)
            {
                usable = false;
            }

            meshUsableById[id] = usable;
            if (!usable && loggedUnusableMeshes.Add(mesh.name))
            {
                LabLogger.Warn($"[SCPSLBot] NAV_UNREADABLE_MESH name={mesh.name} readable={mesh.isReadable} vertices={mesh.vertexCount}");
            }

            return usable;
        }

        private bool loggedModifierBoxes;

        private void AddKeycardModifierBoxes(List<NavMeshBuildSource> sources)
        {
            var radius = NavigationAgentProfile.Radius;
            var log = !loggedModifierBoxes;
            loggedModifierBoxes = true;
            foreach (var door in DoorVariant.AllDoors)
            {
                if (door == null || door is ElevatorDoor)
                {
                    continue;
                }

                var policy = door.RequiredPermissions;
                if (policy.RequiredPermissions == DoorPermissionFlags.None)
                {
                    continue;
                }

                var area = Areas.GetOrAddArea((ushort)policy.RequiredPermissions, policy.RequireAll);
                if (area == DoorAreaRegistry.WalkableArea)
                {
                    continue;
                }

                // The door frame never moves but the leaf colliders swing with the door; measuring
                // once per door keeps the box (and the source hash) stable across door states.
                if (!doorBoxCache.TryGetValue(door, out var extents))
                {
                    if (!TryGetLocalExtents(door.transform, out var measuredCenter, out var measuredSize))
                    {
                        continue;
                    }

                    extents = (measuredCenter, measuredSize);
                    doorBoxCache[door] = extents;
                }

                var (localCenter, localSize) = extents;

                // Depth is bounded to the doorway itself: checkpoint gate frames stretch metres into
                // the rooms and would otherwise mark ordinary floor with the door's area.
                // Checkpoint gates retract into frames narrower than the opening they guard; the
                // box spans the whole wall (solid wall voxels are never walkable anyway).
                var minimumWidth = door is CheckpointDoor ? CheckpointGateWidth : MinimumDoorWidth;
                var size = new Vector3(
                    Mathf.Max(minimumWidth, localSize.x) + radius * 2f,
                    Mathf.Max(3f, localSize.y + 1f),
                    Mathf.Clamp(localSize.z, 0.6f, 1.6f) + radius * 2f + 0.4f);
                var center = door.transform.TransformPoint(localCenter);
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.ModifierBox,
                    transform = Matrix4x4.TRS(center, door.transform.rotation, Vector3.one),
                    size = size,
                    area = area,
                });
                ModifierBoxes++;
                if (log)
                {
                    LabLogger.Info($"[SCPSLBot] NAV_DOOR_AREA door={door.name} type={door.GetType().Name} perms={policy.RequiredPermissions}{(policy.RequireAll ? "/all" : "/any")} area={area} center=({center.x:F1},{center.y:F1},{center.z:F1}) size=({size.x:F1},{size.y:F1},{size.z:F1}) open={door.TargetState}");
                }
            }
        }

        // Union of the door's colliders expressed in the door's own axes (doors are rotated in
        // 90 degree steps, so a local box hugs the leaf and frame).
        private bool TryGetLocalExtents(Transform door, out Vector3 localCenter, out Vector3 localSize)
        {
            localCenter = Vector3.zero;
            localSize = Vector3.zero;
            colliderScratch.Clear();
            door.GetComponentsInChildren(true, colliderScratch);
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            var any = false;
            foreach (var collider in colliderScratch)
            {
                if (collider == null || collider.isTrigger)
                {
                    continue;
                }

                var bounds = collider.bounds;
                for (var corner = 0; corner < 8; corner++)
                {
                    var world = new Vector3(
                        (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                        (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                        (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                    var local = door.InverseTransformPoint(world);
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                    any = true;
                }
            }

            if (!any)
            {
                return false;
            }

            // Centre laterally and in depth on the door pivot; the union may be asymmetric (gate
            // frames), so the half extents are the larger side. Height keeps the union centre.
            var halfWidth = Mathf.Max(Mathf.Abs(min.x), Mathf.Abs(max.x));
            var halfDepth = Mathf.Max(Mathf.Abs(min.z), Mathf.Abs(max.z));
            localCenter = new Vector3(0f, (min.y + max.y) * 0.5f, 0f);
            localSize = new Vector3(halfWidth * 2f, max.y - min.y, halfDepth * 2f);
            // Door transforms carry the prefab scale; TRS above uses unit scale, so express the box in world units.
            var scale = door.lossyScale;
            localSize = new Vector3(localSize.x * Mathf.Abs(scale.x), localSize.y * Mathf.Abs(scale.y), localSize.z * Mathf.Abs(scale.z));
            return true;
        }

        // Order-independent: the builder returns the same set in a different order from one
        // collection to the next (physics iteration order follows moving objects), and an
        // order-sensitive digest would rebuild tiles every cycle for nothing.
        private static ulong Hash(List<NavMeshBuildSource> sources)
        {
            const ulong prime = 1099511628211UL;
            var total = unchecked((ulong)sources.Count * 0x9E3779B97F4A7C15UL);
            foreach (var source in sources)
            {
                var hash = 14695981039346656037UL;
                hash = Mix(hash, (ulong)source.shape, prime);
                hash = Mix(hash, (ulong)source.area, prime);
                hash = Mix(hash, (ulong)(source.sourceObject != null ? source.sourceObject.GetInstanceID() : 0), prime);
                hash = Mix(hash, (ulong)(source.component != null ? source.component.GetInstanceID() : 0), prime);
                hash = Mix(hash, Quantize(source.size.x), prime);
                hash = Mix(hash, Quantize(source.size.y), prime);
                hash = Mix(hash, Quantize(source.size.z), prime);
                var m = source.transform;
                hash = Mix(hash, Quantize(m.m03), prime);
                hash = Mix(hash, Quantize(m.m13), prime);
                hash = Mix(hash, Quantize(m.m23), prime);
                hash = Mix(hash, Quantize(m.m00), prime);
                hash = Mix(hash, Quantize(m.m02), prime);
                hash = Mix(hash, Quantize(m.m11), prime);
                unchecked
                {
                    // Final avalanche so that adding per-source digests stays collision-resistant.
                    hash ^= hash >> 31;
                    hash *= 0xBF58476D1CE4E5B9UL;
                    hash ^= hash >> 29;
                    total += hash;
                }
            }

            return total;
        }

        private static ulong Quantize(float value) => unchecked((ulong)(long)Mathf.Round(value * 100f));

        private static ulong Mix(ulong hash, ulong value, ulong prime)
        {
            unchecked
            {
                hash ^= value;
                return hash * prime;
            }
        }
    }
}
