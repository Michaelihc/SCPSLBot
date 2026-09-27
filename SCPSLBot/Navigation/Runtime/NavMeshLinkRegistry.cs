using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using MapGeneration.RoomConnectors;
using SCPSLBot.Navigation.Policy;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using LabLogger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Navigation.Runtime
{
    /// <summary>
    /// One bidirectional off-mesh link per elevator group between the landings in front of its two
    /// doors. Endpoints sit outside the shaft (the chamber itself is never part of the walkable
    /// surface), and the elevator behaviors take over when the next path corner is a link endpoint.
    /// </summary>
    internal sealed class NavMeshLinkRegistry
    {
        private const float LandingDistance = 1.6f;
        private const float EndpointMatchDistance = 0.45f;
        private const float LinkWidth = 1.2f;

        public readonly struct Link
        {
            public Link(ElevatorGroup group, ElevatorDoor doorA, ElevatorDoor doorB, Vector3 landingA, Vector3 landingB, NavMeshLinkInstance instance)
            {
                Group = group;
                DoorA = doorA;
                DoorB = doorB;
                LandingA = landingA;
                LandingB = landingB;
                Instance = instance;
            }

            public ElevatorGroup Group { get; }
            public ElevatorDoor DoorA { get; }
            public ElevatorDoor DoorB { get; }
            public Vector3 LandingA { get; }
            public Vector3 LandingB { get; }
            public NavMeshLinkInstance Instance { get; }
        }

        private readonly List<Link> links = new();
        private readonly List<NavMeshLinkInstance> passageLinks = new();

        public IReadOnlyList<Link> Links => links;

        public int Count => links.Count;

        /// <summary>Door-less connectors bridged with a jump-tier link because the baked surface leaves them sealed.</summary>
        public int PassageLinks => passageLinks.Count;

        /// <summary>Door-less connectors the capsule cannot pass at any tier (bots route around them).</summary>
        public int SealedConnectors { get; private set; }

        public void Clear()
        {
            foreach (var link in links)
            {
                if (NavMesh.IsLinkValid(link.Instance))
                {
                    NavMesh.RemoveLink(link.Instance);
                }
            }

            links.Clear();
            foreach (var instance in passageLinks)
            {
                if (NavMesh.IsLinkValid(instance))
                {
                    NavMesh.RemoveLink(instance);
                }
            }

            passageLinks.Clear();
            SealedConnectors = 0;
        }

        /// <summary>
        /// The builder cannot climb clutter a bot can jump (pipes, low crates). For every door-less
        /// connector without a complete path between its two sides, probe the passage with the
        /// capsule (walk tier, then jumpable tier) and bridge it with a Jump-area link through the
        /// probed band; the stuck ladder's native jump carries the bot over.
        /// </summary>
        public void RebuildPassages(int agentTypeId)
        {
            foreach (var instance in passageLinks)
            {
                if (NavMesh.IsLinkValid(instance))
                {
                    NavMesh.RemoveLink(instance);
                }
            }

            passageLinks.Clear();
            SealedConnectors = 0;
            var path = new NavMeshPath();
            foreach (var connector in UnityEngine.Object.FindObjectsByType<SpawnableRoomConnector>(FindObjectsSortMode.None))
            {
                if (connector == null || connector.GetComponentInChildren<DoorVariant>() != null)
                {
                    continue;
                }

                var transform = connector.transform;
                var center = transform.position + Vector3.up;
                var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
                if (forward.sqrMagnitude < 0.5f)
                {
                    continue;
                }

                var right = Vector3.Cross(Vector3.up, forward).normalized;
                if (!NavMesh.SamplePosition(center + forward * 3.5f, out var a, 3f, NavMesh.AllAreas)
                    || !NavMesh.SamplePosition(center - forward * 3.5f, out var b, 3f, NavMesh.AllAreas))
                {
                    continue;
                }

                if (NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path)
                    && path.status == NavMeshPathStatus.PathComplete
                    && PathLength(path) <= Vector3.Distance(a.position, b.position) * 1.5f + 2f)
                {
                    continue;
                }

                var type = connector.SpawnData.ConnectorType;
                var passage = ConnectorPassageProbe.Probe(center, forward, right);
                if (passage.Gap is not { } gap)
                {
                    SealedConnectors++;
                    LabLogger.Warn($"[SCPSLBot] NAV_CONNECTOR_SEALED type={type} pos={Format(transform.position)} portalGaps={passage.GapCount} floor={passage.FloorFound}");
                    continue;
                }

                var lateral = right * gap.Center;
                var floor = new Vector3(center.x, passage.FloorY, center.z);
                if (!NavMesh.SamplePosition(floor + lateral + forward * 1.4f, out var start, 2f, NavMesh.AllAreas)
                    || !NavMesh.SamplePosition(floor + lateral - forward * 1.4f, out var end, 2f, NavMesh.AllAreas))
                {
                    SealedConnectors++;
                    LabLogger.Warn($"[SCPSLBot] NAV_CONNECTOR_SEALED type={type} pos={Format(transform.position)} reason=link-endpoint-off-mesh tier={passage.Tier}");
                    continue;
                }

                var instance = NavMesh.AddLink(new NavMeshLinkData
                {
                    startPosition = start.position,
                    endPosition = end.position,
                    width = Mathf.Max(0.6f, gap.Width),
                    costModifier = -1f,
                    bidirectional = true,
                    area = DoorAreaRegistry.JumpArea,
                    agentTypeID = agentTypeId,
                });
                if (!NavMesh.IsLinkValid(instance))
                {
                    SealedConnectors++;
                    continue;
                }

                passageLinks.Add(instance);
                LabLogger.Info($"[SCPSLBot] NAV_CONNECTOR_LINK type={type} pos={Format(transform.position)} tier={passage.Tier} band={gap} start={Format(start.position)} end={Format(end.position)}");
            }
        }

        public void Rebuild(int agentTypeId)
        {
            Clear();
            foreach (ElevatorGroup group in Enum.GetValues(typeof(ElevatorGroup)))
            {
                var doors = ElevatorDoor.GetDoorsForGroup(group);
                if (doors == null || doors.Count != 2 || doors[0] == null || doors[1] == null)
                {
                    continue;
                }

                if (!TryGetLanding(doors[0], out var landingA) || !TryGetLanding(doors[1], out var landingB))
                {
                    LabLogger.Warn($"[SCPSLBot] NAV_LINK_SKIPPED group={group} reason=landing-off-mesh a={Format(doors[0].transform.position)} b={Format(doors[1].transform.position)}");
                    continue;
                }

                var instance = NavMesh.AddLink(new NavMeshLinkData
                {
                    startPosition = landingA,
                    endPosition = landingB,
                    width = LinkWidth,
                    costModifier = -1f,
                    bidirectional = true,
                    area = DoorAreaRegistry.ElevatorArea,
                    agentTypeID = agentTypeId,
                });
                if (!NavMesh.IsLinkValid(instance))
                {
                    LabLogger.Warn($"[SCPSLBot] NAV_LINK_SKIPPED group={group} reason=add-link-failed");
                    continue;
                }

                links.Add(new Link(group, doors[0], doors[1], landingA, landingB, instance));
                LabLogger.Info($"[SCPSLBot] NAV_LINK group={group} a={Format(landingA)} b={Format(landingB)}");
            }
        }

        /// <summary>Resolves the elevator link that starts or ends at <paramref name="corner"/>.</summary>
        public bool TryGetLinkAt(Vector3 corner, out Link link, out bool startsAtA)
        {
            foreach (var candidate in links)
            {
                if (Vector3.Distance(candidate.LandingA, corner) <= EndpointMatchDistance)
                {
                    link = candidate;
                    startsAtA = true;
                    return true;
                }

                if (Vector3.Distance(candidate.LandingB, corner) <= EndpointMatchDistance)
                {
                    link = candidate;
                    startsAtA = false;
                    return true;
                }
            }

            link = default;
            startsAtA = false;
            return false;
        }

        // The landing is the door side that is not the shaft: the candidate farther from the
        // chamber's docking position for that floor, snapped to the navmesh.
        private static bool TryGetLanding(ElevatorDoor door, out Vector3 landing)
        {
            landing = default;
            var doorTransform = door.transform;
            var forward = Vector3.ProjectOnPlane(doorTransform.forward, Vector3.up).normalized;
            var center = doorTransform.position + Vector3.up * 0.5f;
            var dock = door.TargetPosition;
            var front = center + forward * LandingDistance;
            var back = center - forward * LandingDistance;
            var frontDistance = Vector3.Distance(Vector3.ProjectOnPlane(front, Vector3.up), Vector3.ProjectOnPlane(dock, Vector3.up));
            var backDistance = Vector3.Distance(Vector3.ProjectOnPlane(back, Vector3.up), Vector3.ProjectOnPlane(dock, Vector3.up));
            var first = frontDistance >= backDistance ? front : back;
            var second = frontDistance >= backDistance ? back : front;
            return TrySnap(first, out landing) || TrySnap(second, out landing);
        }

        private static bool TrySnap(Vector3 candidate, out Vector3 snapped)
        {
            if (NavMesh.SamplePosition(candidate, out var hit, 2f, NavMesh.AllAreas)
                && RoomUtils.TryGetRoom(hit.position + Vector3.up * 0.5f, out var room)
                && room != null)
            {
                snapped = hit.position;
                return true;
            }

            snapped = default;
            return false;
        }

        private static float PathLength(NavMeshPath path)
        {
            var length = 0f;
            var corners = path.corners;
            for (var index = 1; index < corners.Length; index++)
            {
                length += Vector3.Distance(corners[index - 1], corners[index]);
            }

            return length;
        }

        private static string Format(Vector3 value) => $"({value.x:F1},{value.y:F1},{value.z:F1})";
    }
}
