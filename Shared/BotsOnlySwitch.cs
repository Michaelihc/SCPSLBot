using System;
using System.Reflection;

namespace WarmupShared;

/// <summary>
/// Reads SCPSLBot's bots_only master switch. Late-bound so a companion still loads without SCPSLBot,
/// or beside a build that predates the switch; in both cases the companion stays active.
/// </summary>
internal static class BotsOnlySwitch
{
    public static bool IsEnabled()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name != "SCPSLBot")
            {
                continue;
            }

            PropertyInfo? property = assembly.GetType("SCPSLBot.Api.BotsOnlyMode", false)
                ?.GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Static);
            return property?.GetValue(null) is true;
        }

        return false;
    }
}
