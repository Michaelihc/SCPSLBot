using System;

namespace SCPSLBot.Warmup.Policy;

/// <summary>Pure blast math, kept free of scene and network work so it can be tested without a server.</summary>
internal static class SpecialWeaponBallistics
{
    /// <summary>Lethal at the centre, quickly weaker through a room and exactly zero at the radius.</summary>
    public static float BlastDamage(float centerDamage, float distance, float radius)
    {
        if (centerDamage <= 0f || radius <= 0f || distance >= radius)
        {
            return 0f;
        }

        float remaining = 1f - (Math.Max(0f, distance) / radius);
        return centerDamage * remaining * remaining;
    }

    /// <summary>Northwood's armor formula, applied to the victim's vest by the caller.</summary>
    public static float ApplyVictimArmor(float damage, int vestEfficacy, int penetrationPercent)
    {
        if (damage <= 0f || float.IsNaN(damage) || float.IsInfinity(damage))
        {
            return 0f;
        }

        float efficacy = Math.Max(0, Math.Min(100, vestEfficacy)) / 100f;
        float penetration = Math.Max(0, Math.Min(100, penetrationPercent)) / 100f;
        return damage * (1f - (efficacy * (1f - penetration)));
    }

    /// <summary>A share of maximum health, capped, plus a flat bonus inside the inner core.</summary>
    public static float ShockwaveDamage(
        float maxHealth,
        float maxHealthFraction,
        float damageCap,
        bool insideCore,
        float coreBonusDamage)
    {
        float fraction = Math.Max(0f, Math.Min(1f, maxHealthFraction));
        float damage = Math.Min(Math.Max(0f, maxHealth) * fraction, Math.Max(0f, damageCap));
        return insideCore ? damage + Math.Max(0f, coreBonusDamage) : damage;
    }
}
