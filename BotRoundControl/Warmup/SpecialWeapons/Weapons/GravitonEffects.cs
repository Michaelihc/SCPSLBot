#nullable enable

using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using MEC;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;
using PrimitiveFlags = AdminToys.PrimitiveFlags;

namespace SCPSLBot.Warmup.SpecialWeapons.Weapons;

/// <summary>The graviton beam and slam: presentation only, and every toy dies with its animation.</summary>
internal sealed class GravitonEffects
{
    private const byte RawMovementSmoothing = 60;

    private readonly List<AdminToy> toys = new();
    private readonly List<CoroutineHandle> animations = new();

    public void Clear()
    {
        foreach (CoroutineHandle animation in animations)
        {
            Timing.KillCoroutines(animation);
        }

        animations.Clear();
        foreach (AdminToy toy in toys.ToArray())
        {
            Destroy(toy);
        }
    }

    public void Beam(Vector3 from, Vector3 to)
    {
        Vector3 direction = to - from;
        float length = direction.magnitude;
        if (length < 0.1f)
        {
            return;
        }

        try
        {
            PrimitiveObjectToy beam = Spawn(
                PrimitiveType.Cylinder,
                (from + to) * 0.5f,
                Quaternion.FromToRotation(Vector3.up, direction / length),
                new Vector3(GravitonTuning.BeamThickness, length * 0.5f, GravitonTuning.BeamThickness),
                GravitonTuning.BeamColor);
            Run(FadeBeam(beam));
        }
        catch (Exception exception)
        {
            Logger.Warn($"[BotRoundControl:SpecialWeapons] Graviton beam failed: {exception.GetBaseException().Message}");
        }
    }

    public void Slam(Vector3 point, float radius) => Run(AnimateSlam(point, radius));

    private void Run(IEnumerator<float> animation)
    {
        animations.RemoveAll(static handle => !handle.IsRunning);
        animations.Add(Timing.RunCoroutine(animation));
    }

    private IEnumerator<float> FadeBeam(PrimitiveObjectToy beam)
    {
        Color color = GravitonTuning.BeamColor;
        float startedAt = Time.timeSinceLevelLoad;
        while (beam is { IsDestroyed: false })
        {
            float t = Mathf.Clamp01((Time.timeSinceLevelLoad - startedAt) / GravitonTuning.BeamSeconds);
            beam.Color = WithAlpha(color, Quantize(Mathf.Lerp(color.a, 0f, t)));
            if (t >= 1f)
            {
                break;
            }

            yield return Timing.WaitForSeconds(GravitonTuning.AnimationStepSeconds);
        }

        Destroy(beam);
    }

    private IEnumerator<float> AnimateSlam(Vector3 point, float maxRadius)
    {
        var parts = new List<PrimitiveObjectToy>(GravitonTuning.CrackCount + GravitonTuning.RingSegments + 1);
        var cracks = new List<PrimitiveObjectToy>(GravitonTuning.CrackCount);
        var ring = new List<(PrimitiveObjectToy Bar, Vector3 Radial)>(GravitonTuning.RingSegments);
        PrimitiveObjectToy? dome = null;
        Vector3 ringCenter = point + (Vector3.up * ((GravitonTuning.RingHeight * 0.5f) + 0.05f));
        try
        {
            for (int i = 0; i < GravitonTuning.CrackCount; i++)
            {
                float yaw = (360f / GravitonTuning.CrackCount * i) + UnityEngine.Random.Range(-9f, 9f);
                Vector3 radial = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                cracks.Add(Spawn(
                    PrimitiveType.Cube,
                    point + (radial * (GravitonTuning.CrackLength * 0.5f)) + (Vector3.up * 0.03f),
                    Quaternion.LookRotation(radial, Vector3.up),
                    new Vector3(GravitonTuning.CrackWidth, GravitonTuning.CrackHeight, GravitonTuning.CrackLength),
                    GravitonTuning.CrackColor));
            }

            parts.AddRange(cracks);
            dome = Spawn(PrimitiveType.Sphere, point, Quaternion.identity, Vector3.one * 0.2f, GravitonTuning.DomeColor);
            parts.Add(dome);
            for (int s = 0; s < GravitonTuning.RingSegments; s++)
            {
                Vector3 radial = (Quaternion.Euler(0f, 360f / GravitonTuning.RingSegments * s, 0f) * Vector3.forward).normalized;
                Quaternion rotation = Quaternion.LookRotation(radial, Vector3.up) *
                    Quaternion.Euler(-GravitonTuning.RingTiltDegrees, 0f, 0f);
                PrimitiveObjectToy bar = Spawn(
                    PrimitiveType.Cube,
                    ringCenter + (radial * GravitonTuning.RingStartRadius),
                    rotation,
                    new Vector3(0.3f, GravitonTuning.RingHeight, GravitonTuning.RingThickness),
                    GravitonTuning.RingColor);
                ring.Add((bar, radial));
                parts.Add(bar);
            }
        }
        catch (Exception exception)
        {
            Logger.Warn($"[BotRoundControl:SpecialWeapons] Graviton slam failed: {exception.GetBaseException().Message}");
        }

        float total = Mathf.Max(GravitonTuning.RingSeconds, GravitonTuning.CrackSeconds);
        float tangentialFactor = ring.Count > 0 ? 2f * Mathf.PI / ring.Count * 1.3f : 0f;
        float startedAt = Time.timeSinceLevelLoad;
        while (true)
        {
            float elapsed = Time.timeSinceLevelLoad - startedAt;
            AnimateRing(ring, ringCenter, maxRadius, tangentialFactor, elapsed);
            AnimateDome(dome, elapsed);
            AnimateCracks(cracks, elapsed);
            if (elapsed >= total)
            {
                break;
            }

            yield return Timing.WaitForSeconds(GravitonTuning.AnimationStepSeconds);
        }

        foreach (PrimitiveObjectToy part in parts)
        {
            Destroy(part);
        }
    }

    private static void AnimateRing(
        List<(PrimitiveObjectToy Bar, Vector3 Radial)> ring,
        Vector3 center,
        float maxRadius,
        float tangentialFactor,
        float elapsed)
    {
        Color color = GravitonTuning.RingColor;
        float t = Mathf.Clamp01(elapsed / GravitonTuning.RingSeconds);
        float eased = 1f - ((1f - t) * (1f - t));
        float radius = Mathf.Lerp(GravitonTuning.RingStartRadius, Mathf.Max(1f, maxRadius), eased);
        Color tint = WithAlpha(color, Quantize(Mathf.Lerp(Mathf.Clamp01(color.a), 0f, t)));
        var scale = new Vector3(Mathf.Max(0.3f, radius * tangentialFactor), GravitonTuning.RingHeight, GravitonTuning.RingThickness);
        foreach ((PrimitiveObjectToy bar, Vector3 radial) in ring)
        {
            if (bar is { IsDestroyed: false })
            {
                bar.Position = center + (radial * radius);
                bar.Scale = scale;
                bar.Color = tint;
            }
        }
    }

    private static void AnimateDome(PrimitiveObjectToy? dome, float elapsed)
    {
        if (dome is not { IsDestroyed: false })
        {
            return;
        }

        Color color = GravitonTuning.DomeColor;
        float t = Mathf.Clamp01(elapsed / GravitonTuning.DomeSeconds);
        float diameter = Mathf.Lerp(0.2f, GravitonTuning.DomePeakDiameter, 1f - ((1f - t) * (1f - t)));
        dome.Scale = new Vector3(diameter, diameter * GravitonTuning.DomeHeightFactor, diameter);
        dome.Color = WithAlpha(color, Quantize(Mathf.Lerp(color.a, 0f, t)));
    }

    private static void AnimateCracks(List<PrimitiveObjectToy> cracks, float elapsed)
    {
        if (elapsed <= GravitonTuning.CrackHoldSeconds)
        {
            return;
        }

        Color color = GravitonTuning.CrackColor;
        float fadeSeconds = Mathf.Max(0.1f, GravitonTuning.CrackSeconds - GravitonTuning.CrackHoldSeconds);
        float t = Mathf.Clamp01((elapsed - GravitonTuning.CrackHoldSeconds) / fadeSeconds);
        Color tint = WithAlpha(color, Quantize(Mathf.Lerp(color.a, 0f, t)));
        foreach (PrimitiveObjectToy crack in cracks)
        {
            if (crack is { IsDestroyed: false })
            {
                crack.Color = tint;
            }
        }
    }

    private PrimitiveObjectToy Spawn(PrimitiveType type, Vector3 position, Quaternion rotation, Vector3 scale, Color color)
    {
        PrimitiveObjectToy toy = PrimitiveObjectToy.Create(position, rotation, scale, parent: null, networkSpawn: false);
        toy.Type = type;
        toy.Flags = PrimitiveFlags.Visible;
        toy.Color = color;
        toy.IsStatic = false;
        toy.Base.NetworkMovementSmoothing = RawMovementSmoothing;
        toy.SyncInterval = 0f;
        toy.Spawn();
        toys.Add(toy);
        return toy;
    }

    private void Destroy(AdminToy toy)
    {
        toys.Remove(toy);
        try
        {
            if (toy is { IsDestroyed: false })
            {
                toy.Destroy();
            }
        }
        catch (Exception exception)
        {
            Logger.Warn($"[BotRoundControl:SpecialWeapons] Failed to destroy an effect toy: {exception.GetBaseException().Message}");
        }
    }

    private static float Quantize(float alpha) =>
        Mathf.Round(alpha * GravitonTuning.AlphaSteps) / GravitonTuning.AlphaSteps;

    private static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);
}
