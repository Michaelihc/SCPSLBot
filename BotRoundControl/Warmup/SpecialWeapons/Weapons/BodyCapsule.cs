using UnityEngine;

namespace SCPSLBot.Warmup.SpecialWeapons.Weapons;

/// <summary>The feet-to-head capsule a projectile is scored against.</summary>
internal static class BodyCapsule
{
    public const float BottomUp = 0.15f;
    public const float TopUp = 1.85f;

    /// <summary>True when the segment passes within <paramref name="radius"/> of the capsule axis.</summary>
    public static bool SegmentHits(Vector3 from, Vector3 to, Vector3 capsuleBottom, Vector3 capsuleTop, float radius) =>
        ClosestSquaredDistance(from, to, capsuleBottom, capsuleTop) <= radius * radius;

    /// <summary>Squared closest distance between two segments (Ericson, RTCD 5.1.9).</summary>
    private static float ClosestSquaredDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        const float epsilon = 1e-8f;
        Vector3 d1 = q1 - p1;
        Vector3 d2 = q2 - p2;
        Vector3 r = p1 - p2;
        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);
        float s;
        float t;
        if (a <= epsilon && e <= epsilon)
        {
            return r.sqrMagnitude;
        }

        if (a <= epsilon)
        {
            s = 0f;
            t = Mathf.Clamp01(f / e);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= epsilon)
            {
                t = 0f;
                s = Mathf.Clamp01(-c / a);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denominator = (a * e) - (b * b);
                s = denominator > epsilon ? Mathf.Clamp01(((b * f) - (c * e)) / denominator) : 0f;
                t = ((b * s) + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Mathf.Clamp01((b - c) / a);
                }
            }
        }

        return ((p1 + (d1 * s)) - (p2 + (d2 * t))).sqrMagnitude;
    }
}
