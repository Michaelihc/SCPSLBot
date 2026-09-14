namespace ScpslPluginStarter.Core;

internal enum DamageActorKind
{
    None,
    Self,
    Other,
}

internal static class DamagePolicy
{
    public static bool ShouldBlock(
        DamageActorKind attackerKind,
        bool attackerInSafezone,
        bool attackerExitProtected,
        bool victimInSafezone,
        bool victimExitProtected,
        bool pluginOwnedDamage)
    {
        if (pluginOwnedDamage)
        {
            return false;
        }

        if (victimInSafezone || victimExitProtected)
        {
            return true;
        }

        return attackerKind == DamageActorKind.Other
            && (attackerInSafezone || attackerExitProtected);
    }
}
