using System;

namespace SCPSLBot.Navigation.Policy
{
    /// <summary>
    /// Maps a native room prefab instance name to the navigation-mesh form key. Seasonal prefab
    /// variants ("HCZ_049 Christmas", "LCZ_173 Halloween") share the walkable layout of their base
    /// room, so they resolve to the base form instead of silently getting no cells.
    /// </summary>
    internal static class NavigationForms
    {
        private const string CloneSuffix = "(Clone)";

        private static readonly string[] SeasonalSuffixes =
        {
            " Christmas",
            " Halloween",
        };

        public static string Normalize(string gameObjectName)
        {
            if (string.IsNullOrEmpty(gameObjectName))
            {
                return gameObjectName;
            }

            var form = gameObjectName;
            if (form.EndsWith(CloneSuffix, StringComparison.Ordinal))
            {
                form = form.Substring(0, form.Length - CloneSuffix.Length);
            }

            form = form.TrimEnd();
            foreach (var suffix in SeasonalSuffixes)
            {
                if (form.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    form = form.Substring(0, form.Length - suffix.Length).TrimEnd();
                    break;
                }
            }

            return form;
        }

        public static bool IsSeasonalVariant(string gameObjectName)
        {
            if (string.IsNullOrEmpty(gameObjectName))
            {
                return false;
            }

            var form = gameObjectName.EndsWith(CloneSuffix, StringComparison.Ordinal)
                ? gameObjectName.Substring(0, gameObjectName.Length - CloneSuffix.Length)
                : gameObjectName;
            form = form.TrimEnd();
            foreach (var suffix in SeasonalSuffixes)
            {
                if (form.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
