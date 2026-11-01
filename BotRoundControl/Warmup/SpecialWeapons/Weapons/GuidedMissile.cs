#nullable enable

using System;
using System.Collections.Generic;
using CustomItems;
using LabApi.Features.Wrappers;
using MEC;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;
using PrimitiveFlags = AdminToys.PrimitiveFlags;

namespace SCPSLBot.Warmup.SpecialWeapons.Weapons;

/// <summary>
/// One live missile. It homes only on the lock taken at launch, and cover permanently breaks that lock.
/// Its swept 20 Hz path detonates on the first valid body or world surface it meets.
/// </summary>
internal sealed class GuidedMissile
{
    private const byte RawMovementSmoothing = 60;
    private const float TickSeconds = 0.05f;
    private const float RangeMargin = 1.25f;

    private readonly GuidedRocketWeapon weapon;
    private readonly GuidedRocketConfig config;
    private readonly List<AdminToy> toys = new();
    private readonly Vector3 origin;
    private Vector3 direction;
    private Player? target;
    private PrimitiveObjectToy? root;
    private CoroutineHandle flight;
    private bool done;

    public GuidedMissile(
        GuidedRocketWeapon weapon,
        GuidedRocketConfig config,
        Player owner,
        Player? target,
        Vector3 origin,
        Vector3 direction)
    {
        this.weapon = weapon;
        this.config = config;
        Owner = owner;
        this.target = target;
        this.origin = origin;
        this.direction = direction;
    }

    public Player Owner { get; }

    public bool Spawn()
    {
        try
        {
            IReadOnlyList<MeshPrimitive> model = EmbeddedItemModels.Load(GuidedRocketWeapon.ProjectileModel);
            if (model.Count == 0)
            {
                return false;
            }

            root = PrimitiveObjectToy.Create(origin, Quaternion.LookRotation(direction), Vector3.one, parent: null, networkSpawn: false);
            root.Type = PrimitiveType.Cube;
            root.Flags = PrimitiveFlags.None;
            root.Color = Color.clear;
            root.IsStatic = false;
            root.Base.NetworkMovementSmoothing = RawMovementSmoothing;
            root.SyncInterval = 0f;
            root.Spawn();
            toys.Add(root);

            var exhaust = new Vector3(0f, 0f, -0.132f);
            foreach (MeshPrimitive primitive in model)
            {
                if (string.Equals(primitive.Name, "marker_exhaust", StringComparison.OrdinalIgnoreCase))
                {
                    exhaust = primitive.Position;
                }
                else if (primitive.Name?.StartsWith("marker_", StringComparison.OrdinalIgnoreCase) != true)
                {
                    AddPart(primitive);
                }
            }

            LightSourceToy light = LightSourceToy.Create(exhaust, Quaternion.identity, root.Transform, networkSpawn: false);
            light.Type = LightType.Point;
            light.Color = new Color(1f, 0.32f, 0.06f, 1f);
            light.Intensity = 2.4f;
            light.Range = 2.6f;
            light.ShadowType = LightShadows.None;
            light.IsStatic = false;
            light.Spawn();
            toys.Add(light);
            flight = Timing.RunCoroutine(Fly());
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warn($"[BotRoundControl:SpecialWeapons] Missile spawn failed: {exception.GetBaseException().Message}");
            Destroy();
            return false;
        }
    }

    public void Destroy()
    {
        if (done)
        {
            return;
        }

        done = true;
        if (flight.IsRunning)
        {
            Timing.KillCoroutines(flight);
        }

        foreach (AdminToy toy in toys)
        {
            try
            {
                if (toy is { IsDestroyed: false })
                {
                    toy.Destroy();
                }
            }
            catch
            {
                // Round teardown can invalidate toys between the check and Destroy.
            }
        }

        toys.Clear();
        root = null;
        weapon.OnMissileFinished(this);
    }

    private void AddPart(MeshPrimitive primitive)
    {
        PrimitiveObjectToy part = PrimitiveObjectToy.Create(primitive.Position, Quaternion.Euler(primitive.Rotation), primitive.Scale, root!.Transform, networkSpawn: false);
        part.Type = primitive.Type;
        part.Flags = primitive.Flags & ~PrimitiveFlags.Collidable;
        part.Color = primitive.Color;
        part.IsStatic = true;
        part.Spawn();
        toys.Add(part);
    }

    private IEnumerator<float> Fly()
    {
        float startedAt = Time.timeSinceLevelLoad;
        float travelled = 0f;
        Vector3 position = origin;
        float step = Mathf.Max(1f, config.MissileSpeed) * TickSeconds;
        while (!done)
        {
            if (root is null or { IsDestroyed: true }
                || Owner.IsDestroyed
                || !Owner.IsAlive
                || Time.timeSinceLevelLoad - startedAt >= config.MaxFlightSeconds
                || travelled >= config.LockRange * RangeMargin)
            {
                Destroy();
                yield break;
            }

            Steer(position);
            Vector3 next = position + (direction * step);
            bool wall = Physics.Raycast(position, direction, out RaycastHit wallHit, step, GuidedRocketWeapon.SightMask, QueryTriggerInteraction.Ignore);
            Player? impact = FindImpact(position, wall ? wallHit.point : next);
            if (impact != null)
            {
                // The chest point scores a direct hit through the same falloff as every splash hit.
                weapon.Detonate(this, GuidedRocketWeapon.Chest(impact), impact);
                Destroy();
                yield break;
            }

            if (wall)
            {
                weapon.Detonate(this, wallHit.point, directTarget: null);
                Destroy();
                yield break;
            }

            position = next;
            travelled += step;
            root.Position = position;
            root.Rotation = Quaternion.LookRotation(direction);
            yield return Timing.WaitForSeconds(TickSeconds);
        }
    }

    private void Steer(Vector3 position)
    {
        if (target == null)
        {
            return;
        }

        Vector3 chest = GuidedRocketWeapon.Chest(target);
        if (!weapon.IsValidTarget(Owner, target) || !GuidedRocketWeapon.HasLineOfSight(position, target, chest))
        {
            target = null;
            return;
        }

        direction = Vector3.RotateTowards(
            direction,
            (chest - position).normalized,
            Mathf.Max(0f, config.TurnRateDegreesPerSecond) * Mathf.Deg2Rad * TickSeconds,
            0f).normalized;
    }

    private Player? FindImpact(Vector3 from, Vector3 to)
    {
        Vector3 segment = to - from;
        float length = segment.magnitude;
        Vector3 forward = length > 0.0001f ? segment / length : direction;
        float radius = Mathf.Max(0.1f, config.HitRadius);
        Player? impact = null;
        float earliest = float.PositiveInfinity;
        foreach (Player candidate in Player.List)
        {
            if (!weapon.IsValidTarget(Owner, candidate))
            {
                continue;
            }

            Vector3 feet = candidate.Position;
            Vector3 bottom = feet + (Vector3.up * BodyCapsule.BottomUp);
            Vector3 top = feet + (Vector3.up * BodyCapsule.TopUp);
            if (BodyCapsule.SegmentHits(from, to, bottom, top, radius))
            {
                float along = Mathf.Clamp(Vector3.Dot(GuidedRocketWeapon.Chest(candidate) - from, forward), 0f, length);
                if (along < earliest)
                {
                    earliest = along;
                    impact = candidate;
                }
            }
        }

        return impact;
    }
}
