using System;
using System.Globalization;

namespace SCPSLBot.Navigation.Policy
{
    /// <summary>A bounded, map-owned region supplied through the native navigation command.</summary>
    internal readonly struct CustomNavigationRegion
    {
        public const float MaxCoordinate = 20000f;
        public const float MaxHorizontalSize = 1024f;
        public const float MaxVerticalSize = 256f;
        public const string Usage = "nav rebuild [clear | centerX centerY centerZ sizeX sizeY sizeZ]";

        public readonly float X, Y, Z, SizeX, SizeY, SizeZ;

        private CustomNavigationRegion(float[] values)
        {
            X = values[0]; Y = values[1]; Z = values[2];
            SizeX = values[3]; SizeY = values[4]; SizeZ = values[5];
        }

        public static bool TryParse(ArraySegment<string> arguments, out CustomNavigationRegion region, out string error)
        {
            region = default;
            error = Usage;
            if (arguments.Count != 6 || arguments.Array == null) return false;

            var values = new float[6];
            for (var index = 0; index < values.Length; index++)
            {
                if (!float.TryParse(arguments.Array[arguments.Offset + index], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out values[index])
                    || float.IsNaN(values[index]) || float.IsInfinity(values[index]))
                {
                    error = "Custom navigation coordinates and dimensions must be finite numbers (decimal point: '.').";
                    return false;
                }
            }

            if (values[3] < 1f || values[3] > MaxHorizontalSize
                || values[4] < 1f || values[4] > MaxVerticalSize
                || values[5] < 1f || values[5] > MaxHorizontalSize)
            {
                error = $"Custom navigation size must be 1..{MaxHorizontalSize}m on X/Z and 1..{MaxVerticalSize}m on Y.";
                return false;
            }

            for (var index = 0; index < 3; index++)
            {
                if (Math.Abs(values[index]) + values[index + 3] * 0.5f > MaxCoordinate)
                {
                    error = $"Custom navigation region must stay within +/-{MaxCoordinate}m on every world axis.";
                    return false;
                }
            }

            region = new CustomNavigationRegion(values);
            error = string.Empty;
            return true;
        }
    }
}
