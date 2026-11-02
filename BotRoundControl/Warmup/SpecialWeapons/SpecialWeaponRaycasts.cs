using UnityEngine;

namespace SCPSLBot.Warmup.SpecialWeapons;

/// <summary>Layer masks shared by projectile and line-of-sight casts.</summary>
internal static class SpecialWeaponRaycasts
{
    private static int cachedMask;

    /// <summary>The default world mask without the Hitbox layer, so casts ignore player hit colliders.</summary>
    public static int WorldWithoutHitboxes
    {
        get
        {
            if (cachedMask != 0)
            {
                return cachedMask;
            }

            int hitboxLayer = LayerMask.NameToLayer("Hitbox");
            if (hitboxLayer < 0)
            {
                // Not resolvable yet; a later call retries.
                return Physics.DefaultRaycastLayers;
            }

            cachedMask = Physics.DefaultRaycastLayers & ~(1 << hitboxLayer);
            return cachedMask;
        }
    }
}
