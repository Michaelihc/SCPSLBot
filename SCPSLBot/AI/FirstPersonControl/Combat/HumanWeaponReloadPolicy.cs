namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    /// <summary>
    /// Decides when a human bot should request a firearm reload.
    /// </summary>
    internal static class HumanWeaponReloadPolicy
    {
        public static bool ShouldAttemptReload(int loadedAmmo, int reserveAmmo)
        {
            if (loadedAmmo > 1)
            {
                return false;
            }

            // A dry reload request lets server-side ammo plugins observe the native reload event
            // and replenish reserve ammo. Preserve the last loaded round when no plugin is present.
            return reserveAmmo > 0 || loadedAmmo <= 0;
        }
    }
}
