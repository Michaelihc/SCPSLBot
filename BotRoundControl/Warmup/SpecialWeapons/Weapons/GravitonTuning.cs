using UnityEngine;

namespace SCPSLBot.Warmup.SpecialWeapons.Weapons;

/// <summary>Visual geometry, ricochet and carry calibration. Balance lives in GravitonDisruptorConfig.</summary>
internal static class GravitonTuning
{
    // Wall ricochet hunting for ground.
    internal const int MaxBounces = 3;
    internal const float BounceDownwardBias = 0.45f;
    internal const float BounceRangeMeters = 14f;
    internal const float FirstStrikeRangeMeters = 60f;
    internal const float GroundNormalMinY = 0.65f;
    internal const float BounceSurfaceOffset = 0.05f;

    // Muzzle-to-impact beam.
    internal const float BeamSeconds = 0.35f;
    internal const float BeamThickness = 0.06f;
    internal const float BeamMuzzleForward = 0.35f;
    internal const float BeamMuzzleDrop = 0.15f;
    internal static readonly Color BeamColor = new(2f, 1f, 2.8f, 0.8f);

    // Expanding shockwave ring.
    internal const int RingSegments = 8;
    internal const float RingStartRadius = 0.5f;
    internal const float RingSeconds = 0.9f;
    internal const float RingHeight = 0.5f;
    internal const float RingThickness = 0.07f;
    internal const float RingTiltDegrees = 18f;
    internal static readonly Color RingColor = new(1.6f, 0.6f, 2.4f, 0.55f);

    // Ground-crack starburst.
    internal const int CrackCount = 6;
    internal const float CrackLength = 2.6f;
    internal const float CrackWidth = 0.12f;
    internal const float CrackHeight = 0.04f;
    internal const float CrackSeconds = 1.2f;
    internal const float CrackHoldSeconds = 0.35f;
    internal static readonly Color CrackColor = new(1.3f, 0.5f, 2f, 0.5f);

    // Dome flash.
    internal const float DomeSeconds = 0.35f;
    internal const float DomePeakDiameter = 2.6f;
    internal const float DomeHeightFactor = 0.45f;
    internal static readonly Color DomeColor = new(2.2f, 1.2f, 2.8f, 0.7f);

    /// <summary>Quantized alpha steps, so fades do not write a new colour every tick.</summary>
    internal const float AlphaSteps = 12f;
    internal const float AnimationStepSeconds = 0.04f;

    // Waypoint carry. WaypointId is a byte shared with elevators and rooms, so carries stay bounded.
    internal const float SelectionBeatSeconds = 1.2f;
    internal const float CarryPriorityBias = 30f;
    internal const float PushDistanceMeters = 11.5f;
    internal const float PushSeconds = 1.35f;
    internal const float PushArcHeightMeters = 2.6f;
    internal const float CarryClearanceMarginMeters = 0.75f;
    internal const float CarryProbeHeightMeters = 1f;
    internal const float CarryStepSeconds = 0.033f;
    internal const int MaxCarriesPerShockwave = 6;
    internal const int MaxLiveCarries = 12;
    internal static readonly Vector3 CarryBoundsSize = new(1.2f, 2.6f, 1.2f);
}
