using System;
using System.Linq;
using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles.FirstPersonControl;
using ScpslPluginStarter.Core;
using UnityEngine;

namespace ScpslPluginStarter.Services;

[Flags]
internal enum SafezoneMembership
{
    None = 0,
    SurfaceEscape = 1,
    Scp914 = 2,
}

internal sealed class SafezoneVolumeService
{
    private readonly WarmupSafezoneConfig _config;

    public SafezoneVolumeService(WarmupSafezoneConfig config) => _config = config;

    public SafezoneMembership Resolve(Player player)
    {
        if (!IsEligible(player))
        {
            return SafezoneMembership.None;
        }

        SafezoneMembership membership = SafezoneMembership.None;
        if (ContainsSurface(player))
        {
            membership |= SafezoneMembership.SurfaceEscape;
        }

        if (_config.Scp914SafezoneEnabled && ContainsScp914(player))
        {
            membership |= SafezoneMembership.Scp914;
        }

        return membership;
    }

    public bool ContainsSurface(Player player)
    {
        if (!IsEligible(player)
            || player.ReferenceHub.roleManager.CurrentRole is not IFpcRole)
        {
            return false;
        }

        Vector3 position = player.Position;
        if (IsClosestRoomSurface(position)
            && SurfaceSafezoneGeometry.Contains(
                position.x,
                position.y,
                position.z,
                _config.SurfaceEscapeSafezoneAxis,
                _config.SurfaceEscapeSafezoneMaxZ,
                _config.SurfaceEscapeSafezoneLessThan,
                _config.SurfaceEscapeSafezoneMinX))
        {
            return true;
        }

        foreach (Bounds bounds in GetSurfaceBounds())
        {
            if (bounds.Contains(position))
            {
                return true;
            }
        }

        return false;
    }

    public bool ContainsScp914(Player player)
    {
        if (!IsEligible(player))
        {
            return false;
        }

        LabApi.Features.Wrappers.Scp914? room = LabApi.Features.Wrappers.Scp914.Instance;
        if (room == null || room.IsDestroyed)
        {
            return false;
        }

        Vector3 position = player.Position;
        Bounds verifiedRoomBounds = room.Base.WorldspaceBounds;
        return verifiedRoomBounds.size.sqrMagnitude > 0.01f
            && verifiedRoomBounds.Contains(position)
            && Room.GetRoomAtPosition(position)?.Base == room.Base;
    }

    public bool ContainsSurfaceBlocker(Player player)
    {
        if (!IsEligible(player) || player.ReferenceHub.roleManager.CurrentRole is not IFpcRole)
        {
            return false;
        }

        Vector3 position = player.Position;
        float depth = Math.Max(0f, _config.SurfaceEscapeBlockerDepth);
        if (depth <= 0f)
        {
            return false;
        }

        if (ContainsSurface(player) || !IsClosestRoomSurface(position))
        {
            return false;
        }

        return SurfaceSafezoneGeometry.ContainsBlocker(
            position.x,
            position.y,
            position.z,
            _config.SurfaceEscapeSafezoneAxis,
            _config.SurfaceEscapeSafezoneMaxZ,
            _config.SurfaceEscapeSafezoneLessThan,
            _config.SurfaceEscapeSafezoneMinX,
            depth);
    }

    public Bounds[] GetSurfaceBounds() => Map.EscapeZones?.ToArray() ?? Array.Empty<Bounds>();

    private static bool IsClosestRoomSurface(Vector3 position)
    {
        bool found = false;
        float bestScore = float.PositiveInfinity;
        FacilityZone closestZone = FacilityZone.None;
        foreach (Room room in Room.List ?? Enumerable.Empty<Room>())
        {
            if (room == null || room.IsDestroyed)
            {
                continue;
            }

            float verticalDelta = Mathf.Abs(room.Position.y - position.y);
            if (verticalDelta > 30f)
            {
                continue;
            }

            float horizontalDelta = Vector2.Distance(
                new Vector2(room.Position.x, room.Position.z),
                new Vector2(position.x, position.z));
            float score = horizontalDelta + (verticalDelta * 4f);
            if (score >= bestScore)
            {
                continue;
            }

            found = true;
            bestScore = score;
            closestZone = room.Zone;
        }

        return found && closestZone == FacilityZone.Surface;
    }

    public static bool IsEligible(Player? player) => player != null
        && !player.IsDestroyed
        && !player.IsHost
        && player.IsAlive
        && player.ReferenceHub != null;
}
