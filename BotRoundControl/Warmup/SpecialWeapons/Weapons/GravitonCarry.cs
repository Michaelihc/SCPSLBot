#nullable enable

using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using MEC;
using PlayerRoles.FirstPersonControl;
using RelativePositioning;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Warmup.SpecialWeapons.Weapons;

/// <summary>
/// Throws real players away from a slam by pinning them to a moving WaypointToy, so native relative
/// positioning carries them with client prediction. Managed bots and dummies keep their ground: their
/// position is simulated on the server, and writing it would fight SCPSLBot navigation each frame.
/// </summary>
internal sealed class GravitonCarry
{
    private const byte RawMovementSmoothing = 60;

    private readonly List<WaypointToy> waypoints = new();
    private readonly List<CoroutineHandle> carries = new();

    public void Clear()
    {
        foreach (CoroutineHandle carry in carries)
        {
            Timing.KillCoroutines(carry);
        }

        carries.Clear();
        foreach (WaypointToy waypoint in waypoints.ToArray())
        {
            Release(waypoint);
        }
    }

    public void Throw(Player victim, Vector3 slamPoint)
    {
        if (victim.IsDummy || waypoints.Count >= GravitonTuning.MaxLiveCarries)
        {
            return;
        }

        carries.RemoveAll(static handle => !handle.IsRunning);
        carries.Add(Timing.RunCoroutine(Carry(victim, slamPoint)));
    }

    private IEnumerator<float> Carry(Player victim, Vector3 slamPoint)
    {
        WaypointToy? waypoint = SpawnWaypoint(victim);
        if (waypoint == null)
        {
            yield break;
        }

        AdminToyWaypoint? native = waypoint.Base.GetComponent<AdminToyWaypoint>()
            ?? waypoint.Base.GetComponentInChildren<AdminToyWaypoint>(includeInactive: true);
        if (native == null)
        {
            Release(waypoint);
            yield break;
        }

        // Selection lands frames after the spawn; moving earlier slides the world under an unboarded player.
        float deadline = Time.timeSinceLevelLoad + GravitonTuning.SelectionBeatSeconds;
        while (Time.timeSinceLevelLoad < deadline && !IsOnWaypoint(victim, native))
        {
            yield return Timing.WaitForOneFrame;
        }

        if (!IsOnWaypoint(victim, native) || HasOtherPassenger(victim, native))
        {
            Release(waypoint);
            yield break;
        }

        Vector3 flat = victim.Position - slamPoint;
        flat.y = 0f;
        Vector3 direction = flat.sqrMagnitude > 0.01f
            ? flat.normalized
            : Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) * Vector3.forward;
        Vector3 origin = waypoint.Position;
        ResolveEnvelope(origin, direction, out float distance, out float arcHeight);
        float startedAt = Time.timeSinceLevelLoad;
        while (waypoint is { IsDestroyed: false } && victim is { IsDestroyed: false, IsAlive: true })
        {
            float t = Mathf.Clamp01((Time.timeSinceLevelLoad - startedAt) / GravitonTuning.PushSeconds);
            float eased = 1f - ((1f - t) * (1f - t));
            waypoint.Position = origin + (direction * (distance * eased)) + (Vector3.up * (arcHeight * 4f * eased * (1f - eased)));
            if (victim.RoleBase is IFpcRole fpc)
            {
                fpc.FpcModule.Motor.ResetFallDamageCooldown();
            }

            if (t >= 1f)
            {
                break;
            }

            yield return Timing.WaitForSeconds(GravitonTuning.CarryStepSeconds);
        }

        Release(waypoint);
    }

    private WaypointToy? SpawnWaypoint(Player victim)
    {
        try
        {
            // Position is the capsule centre, about a metre above the victim's feet.
            Vector3 feet = victim.Position + (Vector3.down * 0.9f);
            WaypointToy waypoint = WaypointToy.Create(feet, Quaternion.identity, Vector3.one, parent: null, networkSpawn: false);
            waypoint.IsStatic = false;
            waypoint.BoundsSize = GravitonTuning.CarryBoundsSize;
            waypoint.PriorityBias = GravitonTuning.CarryPriorityBias;
            // The wrapper stores 256-v, so the raw value is written on the base.
            waypoint.Base.NetworkMovementSmoothing = RawMovementSmoothing;
            waypoint.SyncInterval = GravitonTuning.CarryStepSeconds;
            waypoint.Spawn();
            waypoints.Add(waypoint);
            return waypoint;
        }
        catch (Exception exception)
        {
            Logger.Warn($"[BotRoundControl:SpecialWeapons] Graviton carry failed: {exception.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>Clamps the throw to the room, so it can never deposit anyone through geometry.</summary>
    private static void ResolveEnvelope(Vector3 anchor, Vector3 direction, out float distance, out float arcHeight)
    {
        distance = GravitonTuning.PushDistanceMeters;
        arcHeight = GravitonTuning.PushArcHeightMeters;
        float margin = GravitonTuning.CarryClearanceMarginMeters;
        Vector3 chest = anchor + (Vector3.up * GravitonTuning.CarryProbeHeightMeters);
        if (GravitonDisruptorWeapon.TryRaycastWorld(chest, direction, distance + margin, out RaycastHit forward))
        {
            distance = Mathf.Clamp(forward.distance - margin, 0f, distance);
        }

        if (GravitonDisruptorWeapon.TryRaycastWorld(chest, Vector3.up, arcHeight + margin, out RaycastHit above))
        {
            arcHeight = Mathf.Clamp(above.distance - margin, 0f, arcHeight);
        }
    }

    private static bool HasOtherPassenger(Player victim, AdminToyWaypoint waypoint)
    {
        foreach (Player other in Player.List)
        {
            if (other is { IsDestroyed: false, IsAlive: true } && other != victim && IsOnWaypoint(other, waypoint))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOnWaypoint(Player player, AdminToyWaypoint waypoint)
    {
        if (player.IsDestroyed || player.RoleBase is not IFpcRole fpc)
        {
            return false;
        }

        byte waypointId = fpc.FpcModule.RelativePosition.WaypointId;
        return waypointId != 0
            && WaypointBase.TryGetWaypoint(waypointId, out AdminToyWaypoint selected)
            && selected == waypoint;
    }

    private void Release(WaypointToy waypoint)
    {
        waypoints.Remove(waypoint);
        try
        {
            if (waypoint is { IsDestroyed: false })
            {
                waypoint.Destroy();
            }
        }
        catch (Exception exception)
        {
            Logger.Warn($"[BotRoundControl:SpecialWeapons] Failed to release a carry waypoint: {exception.GetBaseException().Message}");
        }
    }
}
