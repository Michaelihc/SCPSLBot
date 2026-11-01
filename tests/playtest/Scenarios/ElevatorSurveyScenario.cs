using Interactables.Interobjects;
using MapGeneration;
using PlayerRoles.FirstPersonControl;
using PlaytestHarness.Core;
using PlaytestHarness.Probes;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>
/// Read-only survey of the native elevator doors, their rooms and the floor around them, so a map
/// spot can be compared against the navigation data. It asserts nothing about behaviour.
/// </summary>
public sealed class ElevatorSurveyScenario : Scenario
{
    public override string Name => "scpslbot-elevator-survey";
    public override string Description => "Logs every elevator door, its rooms and the floor heights around it.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 60f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Issue2World.RequireReady(ctx);
        yield return ctx.EnsureRoundStarted();
        int mask = FpcStateProcessor.Mask & ~LayerMask.GetMask("Player", "Hitbox", "Door");
        foreach (ElevatorGroup group in Enum.GetValues(typeof(ElevatorGroup)))
        {
            foreach (ElevatorDoor door in ElevatorDoor.GetDoorsForGroup(group))
            {
                Transform transform = door.transform;
                string rooms = door.Rooms == null ? "null" : string.Join("|", door.Rooms.Select(Describe));
                Vector3 p = transform.position;
                Vector3 f = transform.forward;
                ctx.Info($"SURVEY_DOOR group={group} pos={PlacementProbes.Format(p)} forward={PlacementProbes.Format(f)} target={PlacementProbes.Format(door.TargetPosition)} top={PlacementProbes.Format(door.TopPosition)} bottom={PlacementProbes.Format(door.BottomPosition)} rooms={rooms} roomAtFront={RoomAt(p + Vector3.up + f)} roomAtBack={RoomAt(p + Vector3.up - f)}");
                StringBuilder heights = new();
                for (int along = -7; along <= 7; along++)
                {
                    heights.Append(along.ToString(CultureInfo.InvariantCulture)).Append(':');
                    for (int across = -3; across <= 3; across++)
                    {
                        Vector3 top = p + f * along + transform.right * across + Vector3.up * 5f;
                        heights.Append(Physics.Raycast(top, Vector3.down, out RaycastHit hit, 14f, mask, QueryTriggerInteraction.Ignore)
                            ? (hit.point.y - p.y).ToString("F1", CultureInfo.InvariantCulture)
                            : "--").Append(' ');
                    }

                    heights.Append("/ ");
                }

                ctx.Info($"SURVEY_FLOOR group={group} door={PlacementProbes.Format(p)} rows(along:across-3..3,relative to door y)={heights}");
            }

            yield return ctx.Wait(0.1f);
        }

        foreach (RoomIdentifier room in RoomIdentifier.AllRoomIdentifiers)
        {
            if (room != null && room.Name is RoomName.Hcz049 or RoomName.EzGateA or RoomName.EzGateB or RoomName.Outside)
            {
                ctx.Info($"SURVEY_ROOM {Describe(room)} pos={PlacementProbes.Format(room.transform.position)} euler={PlacementProbes.Format(room.transform.eulerAngles)} coords={room.MainCoords}");
            }
        }
    }

    private static string Describe(RoomIdentifier? room) => room == null ? "none" : $"{room.Name}/{room.gameObject.name}";

    private static string RoomAt(Vector3 position) => RoomUtils.TryGetRoom(position, out RoomIdentifier room) ? Describe(room) : "none";
}
