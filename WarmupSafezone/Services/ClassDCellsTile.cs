using LabApi.Features.Wrappers;
using MapGeneration;
using UnityEngine;

namespace ScpslPluginStarter.Services;

internal static class ClassDCellsTile
{
    private static RoomIdentifier? _cached;

    public static bool TryGet(out Vector3Int coords, out Room? room)
    {
        if (_cached == null)
        {
            foreach (Room candidate in Room.List)
            {
                if (candidate != null && !candidate.IsDestroyed && candidate.Name == RoomName.LczClassDSpawn)
                {
                    _cached = candidate.Base;
                    break;
                }
            }
        }

        if (_cached == null)
        {
            coords = default;
            room = null;
            return false;
        }

        coords = _cached.MainCoords;
        room = Room.Get(_cached);
        return true;
    }

    // The Class-D spawn room extends past its main grid tile, so membership uses the native room lookup
    // (grid coords, then the room's own floor/ceiling raycast) rather than the main tile alone.
    public static bool Contains(Vector3 position)
        => TryGet(out _, out _) && RoomUtils.TryGetRoom(position, out RoomIdentifier found) && found == _cached;

    public static Bounds RoomBounds() => _cached != null ? _cached.WorldspaceBounds : default;

    public static Bounds FloorBounds(Vector3Int coords, float floorY)
    {
        Vector3 center = RoomUtils.CoordsToCenterPos(coords);
        Vector3 scale = RoomIdentifier.GridScale;
        return new Bounds(new Vector3(center.x, floorY, center.z), new Vector3(scale.x, 0f, scale.z));
    }
}
