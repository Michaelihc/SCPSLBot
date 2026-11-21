using System;

namespace ScpslPluginStarter.Core;

// Surveyed in SurfaceZone_ArtPass. The upstairs exterior door shares the west door's X/Z.
internal static class SecurityOfficeGeometry
{
    public const float WestDoorX = -43.72f;
    public const float EastDoorX = -38.64f;
    public const float DoorZ = 12.145f;
    public const float FloorY = 301.12f;
    public const float ThresholdInset = 0.24f;
    public const float DoorWidth = 1.42f;
    private const float WestThreshold = WestDoorX + ThresholdInset;
    private const float EastThreshold = EastDoorX - ThresholdInset;

    private static bool OnLowerFloor(float y) => y >= FloorY - 0.05f && y < 304.7f;

    public static bool Contains(float x, float y, float z) =>
        OnLowerFloor(y) && x >= WestThreshold && x <= EastThreshold && z >= 10.52f && z <= 13.88f;

    public static bool ContainsBlocker(float x, float y, float z, float configuredDepth)
    {
        // Cover each doorway only as far as a standing player can obstruct it.
        float depth = Math.Min(configuredDepth, 1f);
        if (depth <= 0f || !OnLowerFloor(y) || Math.Abs(z - DoorZ) > DoorWidth / 2f) return false;
        return (x >= WestThreshold - depth && x < WestThreshold)
            || (x > EastThreshold && x <= EastThreshold + depth);
    }
}
