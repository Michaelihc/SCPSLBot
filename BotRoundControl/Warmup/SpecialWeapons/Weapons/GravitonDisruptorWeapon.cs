#nullable enable

using System;
using System.Collections.Generic;
using Footprinting;
using InventorySystem.Items.Firearms.Modules;
using InventorySystem.Items.Firearms.Modules.Misc;
using InventorySystem.Items.ThrowableProjectiles;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerStatsSystem;
using SCPSLBot.Warmup.Policy;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Warmup.SpecialWeapons.Weapons;

/// <summary>
/// A tracked Particle Disruptor. Native firing, ammo and damage stay untouched; each discharge also
/// slams a cooldown-gated gravity shockwave at the impact point. The hook is the hitreg's discharge
/// event because ShotWeapon fires at trigger pull, a second early in single mode.
/// </summary>
internal sealed class GravitonDisruptorWeapon : SpecialWeapon
{
    public const string WeaponId = "graviton-disruptor";

    private const string HintTag = "special_weapon_graviton";
    private const float CardSeconds = 6f;
    private const float CardY = 760f;
    private const int CardTextSize = 18;

    private readonly GravitonDisruptorConfig config;
    private readonly Dictionary<ushort, float> readyAt = new();
    private readonly GravitonEffects effects = new();
    private readonly GravitonCarry carry = new();

    public GravitonDisruptorWeapon(GravitonDisruptorConfig config, ISpecialWeaponHost host)
        : base(SpecialWeaponKind.GravitonDisruptor, WeaponId, ItemType.ParticleDisruptor, host)
    {
        this.config = config;
    }

    public override bool IsEnabled => config.Enabled;

    public override void Enable() => HitscanHitregModuleBase.ServerAnyPlayerFired += OnServerAnyPlayerFired;

    public override void Disable()
    {
        HitscanHitregModuleBase.ServerAnyPlayerFired -= OnServerAnyPlayerFired;
        OnRoundReset();
    }

    public override void OnSelected(Player player, ushort serial)
    {
        Host.ShowHint(
            player,
            HintTag,
            Host.Text(
                player,
                $"<color=#B47CFF><b>GRAVITON DISRUPTOR</b></color>\nFire normally: each shot also slams a gravity shockwave at its impact. Recharges in {config.ShockwaveCooldownSeconds:0}s.",
                $"<color=#B47CFF><b>引力粒子裂解者</b></color>\n正常开火：每一发命中处同时砸下重力冲击波，冷却 {config.ShockwaveCooldownSeconds:0} 秒。"),
            CardSeconds,
            CardY,
            CardTextSize);
    }

    public override void OnDeselected(Player player, ushort serial) => Host.RemoveHint(player, HintTag);

    public override void OnItemRemoved(ushort serial) => readyAt.Remove(serial);

    public override void OnRoundReset()
    {
        readyAt.Clear();
        carry.Clear();
        effects.Clear();
    }

    /// <summary>First world hit along a ray; a bystander's collider cannot eat a ricochet segment.</summary>
    internal static bool TryRaycastWorld(Vector3 from, Vector3 direction, float range, out RaycastHit hit)
    {
        RaycastHit[] hits = Physics.RaycastAll(
            from,
            direction,
            Mathf.Max(0.5f, range),
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        Array.Sort(hits, static (left, right) => left.distance.CompareTo(right.distance));
        foreach (RaycastHit candidate in hits)
        {
            if (candidate.collider != null && candidate.collider.GetComponentInParent<ReferenceHub>() == null)
            {
                hit = candidate;
                return true;
            }
        }

        hit = default;
        return false;
    }

    private void OnServerAnyPlayerFired(ReferenceHub owner, HitscanResult result)
    {
        try
        {
            if (owner == null || Player.Get(owner) is not { IsDestroyed: false, IsAlive: true } player)
            {
                return;
            }

            Item? current = player.CurrentItem;
            if (current == null || !Host.IsWeapon(current.Serial, Kind))
            {
                return;
            }

            ushort serial = current.Serial;
            if (readyAt.TryGetValue(serial, out float ready) && Time.timeSinceLevelLoad < ready)
            {
                return;
            }

            // The cooldown is spent only when a slam lands.
            if (!TryResolveSlam(player, result, out Vector3 point, out List<Vector3> path))
            {
                return;
            }

            readyAt[serial] = Time.timeSinceLevelLoad + Mathf.Max(0.5f, config.ShockwaveCooldownSeconds);
            for (int i = 0; i + 1 < path.Count; i++)
            {
                effects.Beam(path[i], path[i + 1]);
            }

            Shockwave(player, point);
        }
        catch (Exception exception)
        {
            Logger.Warn($"[BotRoundControl:SpecialWeapons] Graviton discharge failed: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// A direct player hit slams at the body. Otherwise the aim ray walks down walls until it finds
    /// ground, because single mode does not record plain wall hits. A chain that never lands is discarded.
    /// </summary>
    private static bool TryResolveSlam(Player shooter, HitscanResult result, out Vector3 point, out List<Vector3> path)
    {
        Transform camera = shooter.Camera;
        Vector3 origin = camera.position;
        Vector3 muzzle = origin + (camera.forward * GravitonTuning.BeamMuzzleForward) + (Vector3.down * GravitonTuning.BeamMuzzleDrop);
        path = new List<Vector3> { muzzle };
        point = default;

        if (TryFindNearestPlayerHit(result, origin, out Vector3 playerHit))
        {
            path.Add(playerHit);
            point = playerHit;
            return true;
        }

        Vector3 direction = camera.forward.normalized;
        if (!TryRaycastWorld(origin, direction, GravitonTuning.FirstStrikeRangeMeters, out RaycastHit current))
        {
            return false;
        }

        path.Add(current.point);
        for (int bounce = 0; bounce <= GravitonTuning.MaxBounces; bounce++)
        {
            if (current.normal.y >= GravitonTuning.GroundNormalMinY)
            {
                point = current.point;
                return true;
            }

            if (bounce == GravitonTuning.MaxBounces)
            {
                break;
            }

            Vector3 reflected = Vector3.Reflect(direction, current.normal).normalized;
            Vector3 biased = Vector3.Slerp(reflected, Vector3.down, GravitonTuning.BounceDownwardBias).normalized;
            Vector3 from = current.point + (current.normal * GravitonTuning.BounceSurfaceOffset);
            if (!TryRaycastWorld(from, biased, GravitonTuning.BounceRangeMeters, out RaycastHit next))
            {
                break;
            }

            direction = biased;
            current = next;
            path.Add(current.point);
        }

        return false;
    }

    private static bool TryFindNearestPlayerHit(HitscanResult result, Vector3 origin, out Vector3 point)
    {
        point = default;
        float nearest = float.MaxValue;
        foreach (DestructibleHitPair destructible in result.Destructibles)
        {
            if (destructible.Destructible is HitboxIdentity { TargetHub: not null } hitbox
                && hitbox.TargetHub.roleManager.CurrentRole is IFpcRole
                && destructible.Hit.collider != null)
            {
                float distance = (destructible.Hit.point - origin).sqrMagnitude;
                if (distance < nearest)
                {
                    nearest = distance;
                    point = destructible.Hit.point;
                }
            }
        }

        return nearest < float.MaxValue;
    }

    private void Shockwave(Player attacker, Vector3 point)
    {
        float radius = Mathf.Max(0.5f, config.ShockwaveRadius);
        float coreRadius = Mathf.Clamp(config.CoreRadius, 0f, radius);
        var thrown = new List<Player>(GravitonTuning.MaxCarriesPerShockwave);
        foreach (Player target in Player.List)
        {
            if (!IsValidTarget(attacker, target))
            {
                continue;
            }

            Vector3 chest = target.Position + (Vector3.up * 0.6f);
            float distance = Vector3.Distance(point, chest);
            if (distance > radius
                || Physics.Linecast(chest, point + (Vector3.up * 0.05f), ThrownProjectile.HitBlockerMask, QueryTriggerInteraction.Ignore))
            {
                continue;
            }

            float damage = SpecialWeaponBallistics.ShockwaveDamage(
                target.MaxHealth,
                config.MaxHealthDamageFraction,
                config.DamageCap,
                distance <= coreRadius,
                config.CoreBonusDamage);

            // Penetration 100 keeps the attacker's own vest from reducing this constructor's damage.
            var handler = new ExplosionDamageHandler(new Footprint(attacker.ReferenceHub), Vector3.zero, damage, 100, ExplosionType.Grenade);
            if (target.Damage(handler)
                && thrown.Count < GravitonTuning.MaxCarriesPerShockwave
                && target is { IsDestroyed: false, IsAlive: true, IsHuman: true })
            {
                thrown.Add(target);
            }
        }

        effects.Slam(point, radius);
        foreach (Player victim in thrown)
        {
            carry.Throw(victim, point);
        }
    }

    private bool IsValidTarget(Player attacker, Player target) =>
        target is { IsDestroyed: false, IsAlive: true }
        && target != attacker
        && target.RoleBase is IFpcRole
        && target.RoleBase is IHealthbarRole
        && target.MaxHealth > 0f
        && Host.CanHit(attacker, target);
}
