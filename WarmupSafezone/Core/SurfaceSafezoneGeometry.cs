using System;

namespace ScpslPluginStarter.Core;

internal static class SurfaceSafezoneGeometry
{
    public static bool Contains(
        float x,
        float y,
        float z,
        string? axis,
        float threshold,
        bool lessThan,
        float minimumX)
    {
        if (x <= minimumX)
        {
            return false;
        }

        float coordinate = Coordinate(x, y, z, axis);
        return lessThan ? coordinate <= threshold : coordinate >= threshold;
    }

    public static bool ContainsBlocker(
        float x,
        float y,
        float z,
        string? axis,
        float threshold,
        bool lessThan,
        float minimumX,
        float depth)
    {
        if (depth <= 0f || x <= minimumX || Contains(x, y, z, axis, threshold, lessThan, minimumX))
        {
            return false;
        }

        float coordinate = Coordinate(x, y, z, axis);
        return lessThan
            ? coordinate > threshold && coordinate <= threshold + depth
            : coordinate < threshold && coordinate >= threshold - depth;
    }

    public static string NormalizeAxis(string? axis) => axis?.Trim().ToLowerInvariant() switch
    {
        "x" => "x",
        "y" => "y",
        _ => "z",
    };

    private static float Coordinate(float x, float y, float z, string? axis) => NormalizeAxis(axis) switch
    {
        "x" => x,
        "y" => y,
        _ => z,
    };
}
