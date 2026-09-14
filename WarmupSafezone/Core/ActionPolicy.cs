namespace ScpslPluginStarter.Core;

internal enum SafezoneActionKind
{
    Firearm,
    DryFire,
    Throwable,
    ChargedDangerousItem,
    ScpTargetedOffense,
    ScpAreaOffense,
    IndirectHazardDamage,
    UtilityOrMovement,
}

internal static class ActionPolicy
{
    public static bool ShouldCancel(SafezoneActionKind action, bool actorProtected, bool targetProtected)
    {
        if (action is SafezoneActionKind.IndirectHazardDamage or SafezoneActionKind.UtilityOrMovement)
        {
            // These have no generic pre-action cancellation. Indirect damage is still handled by
            // DamagePolicy; utility/movement is intentionally allowed.
            return false;
        }

        return actorProtected || targetProtected;
    }
}
