using AdminToys;
using CustomPlayerEffects;
using LabApi.Features.Enums;
using LabApi.Features.Wrappers;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using MapGeneration;
using PlayerRoles;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PrimitiveObjectToy = LabApi.Features.Wrappers.PrimitiveObjectToy;
using TextToy = LabApi.Features.Wrappers.TextToy;

namespace WarmupSafezone.Playtests;

public sealed class Scp914SafezoneScenario : Scenario
{
    private const float ProbeDamage = 17f;

    public override string Name => "warmup-safezone-914";
    public override string[] Aliases => ["safezone-914"];
    public override string[] Suites => ["warmup-safezone"];
    public override string Description => "Proves event-time 914 damage rules, immediate exit protection, native-state isolation, and restored Surface visuals.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Actor attacker = ctx.SpawnActor("safezone-attacker", RoleTypeId.ClassD, SpawnSpec.Native(), useMovementProvider: false);
        yield return attacker.WaitReady();
        Actor victim = ctx.SpawnActor("safezone-victim", RoleTypeId.NtfPrivate, SpawnSpec.Native(), useMovementProvider: false);
        yield return victim.WaitReady();
        Actor outsider = ctx.SpawnActor("safezone-outsider", RoleTypeId.ClassD, SpawnSpec.Native(), useMovementProvider: false);
        yield return outsider.WaitReady();

        Player attackerPlayer = Player.Get(attacker.PlayerId)
            ?? throw new RequireException("attacker has no LabAPI player wrapper");
        Player victimPlayer = Player.Get(victim.PlayerId)
            ?? throw new RequireException("victim has no LabAPI player wrapper");
        Player outsiderPlayer = Player.Get(outsider.PlayerId)
            ?? throw new RequireException("outsider has no LabAPI player wrapper");
        bool nativeProtectionEnabled = SpawnProtected.IsProtectionEnabled;
        float nativeProtectionDuration = SpawnProtected.SpawnDuration;

        yield return attacker.GoTo(RoomName.LczClassDSpawn);
        yield return victim.GoTo(RoomName.LczClassDSpawn);
        yield return outsider.GoTo(RoomName.LczClassDSpawn);
        yield return ctx.Wait(5f); // allow any native role-spawn protection to expire
        AssertDamageAllowed(ctx, attackerPlayer, victimPlayer, "outside-to-outside");
        RestoreHealth(ctx, victimPlayer);

        yield return victim.GoTo(RoomName.Lcz914);
        ctx.Require(victim.RoomName == nameof(RoomName.Lcz914), "victim did not settle inside SCP-914");
        ctx.Require(!victimPlayer.IsGodModeEnabled, "WarmupSafezone must not grant godmode inside SCP-914");
        AssertDamageBlocked(ctx, attackerPlayer, victimPlayer, "outside-to-inside");

        yield return attacker.GoTo(RoomName.Lcz914);
        ctx.Require(attacker.RoomName == nameof(RoomName.Lcz914), "attacker did not settle inside SCP-914");
        ctx.Require(!attackerPlayer.IsGodModeEnabled, "WarmupSafezone must not grant attacker godmode inside SCP-914");
        AssertDamageBlocked(ctx, attackerPlayer, victimPlayer, "inside-to-inside");

        yield return victim.GoTo(RoomName.LczClassDSpawn);
        AssertDamageBlocked(ctx, attackerPlayer, victimPlayer, "inside-to-outside");

        yield return attacker.GoTo(RoomName.LczClassDSpawn);
        AssertDamageBlocked(ctx, attackerPlayer, victimPlayer, "immediate-egress-exit-protection");

        bool roleRequestObserved = false;
        void CancelRoleRequest(PlayerChangingRoleEventArgs ev)
        {
            if (ev.Player.PlayerId == attackerPlayer.PlayerId)
            {
                roleRequestObserved = true;
                ev.IsAllowed = false;
            }
        }

        PlayerEvents.ChangingRole += CancelRoleRequest;
        try
        {
            attackerPlayer.SetRole(RoleTypeId.Scientist, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        }
        finally
        {
            PlayerEvents.ChangingRole -= CancelRoleRequest;
        }

        ctx.Require(roleRequestObserved, "cancellable native role request did not reach ChangingRole");
        AssertDamageBlocked(ctx, outsiderPlayer, attackerPlayer, "exit-protection-survives-cancelled-role-request");

        AssertDoorPanel(ctx);
        AssertSurfaceVisualsAndGround(ctx);
        IEnumerator<float> surfaceProbe = ProbeSurfaceWithSettlingDummy(ctx, victim, victimPlayer);
        while (surfaceProbe.MoveNext())
        {
            yield return surfaceProbe.Current;
        }
        IEnumerator<float> scpBlockerProbe = ProbeSurfaceBlockerWithScp(ctx);
        while (scpBlockerProbe.MoveNext())
        {
            yield return scpBlockerProbe.Current;
        }
        ctx.Require(SpawnProtected.IsProtectionEnabled == nativeProtectionEnabled,
            "WarmupSafezone changed process-wide SpawnProtected.IsProtectionEnabled");
        ctx.Require(Math.Abs(SpawnProtected.SpawnDuration - nativeProtectionDuration) < 0.001f,
            "WarmupSafezone changed process-wide SpawnProtected.SpawnDuration");

        ctx.Arrange("pre-existing admin godmode ownership probe", () => attackerPlayer.IsGodModeEnabled = true);
        yield return attacker.GoTo(RoomName.Lcz914);
        yield return attacker.GoTo(RoomName.LczClassDSpawn);
        ctx.Require(attackerPlayer.IsGodModeEnabled, "WarmupSafezone modified godmode owned by an admin or another plugin");
        ctx.Arrange("clean up admin godmode probe", () => attackerPlayer.IsGodModeEnabled = false);
    }

    private static void AssertDamageAllowed(ScenarioContext ctx, Player attacker, Player victim, string label)
    {
        float before = victim.Health;
        bool applied = victim.Damage(ProbeDamage, attacker, Vector3.zero, 100);
        float after = victim.Health;
        ctx.Info($"safezone damage label={label} allowed expected=true health={before:0.##}->{after:0.##} applied={applied}");
        ctx.Require(after < before - 0.01f, $"{label}: expected damage, health stayed {before:0.##}->{after:0.##}");
    }

    private static void AssertDamageBlocked(ScenarioContext ctx, Player attacker, Player victim, string label)
    {
        // Direct damage deliberately isolates the final PlayerEvents.Hurting policy. Native firearm
        // actions are separately denied before hit registration, so the harness's blocked-shot verb
        // (which requires a Hurting event) is intentionally not the correct oracle here.
        float before = victim.Health;
        bool applied = victim.Damage(ProbeDamage, attacker, Vector3.zero, 100);
        float after = victim.Health;
        ctx.Info($"safezone damage label={label} blocked expected=true health={before:0.##}->{after:0.##} applied={applied}");
        ctx.Require(Math.Abs(after - before) < 0.01f, $"{label}: protection failed, health changed {before:0.##}->{after:0.##}");
    }

    private static void RestoreHealth(ScenarioContext ctx, Player victim) =>
        ctx.Arrange("restore victim health between isolated damage-policy cases", () => victim.Health = victim.MaxHealth);

    private static void AssertDoorPanel(ScenarioContext ctx)
    {
        Door door = Door.Get(DoorName.Lcz914Gate)
            ?? throw new RequireException("SCP-914 gate wrapper is unavailable");
        TextToy[] labels = TextToy.List
            .Where(toy => toy.Parent == door.Transform
                && (toy.TextFormat.Contains("安全区") || toy.TextFormat.Contains("SAFE ZONE")))
            .ToArray();
        ctx.Require(labels.Length == 2, $"expected two-sided SCP-914 door text, observed {labels.Length}");
        ctx.Require(labels.All(toy => toy.TextFormat.IndexOf("godmode", StringComparison.OrdinalIgnoreCase) < 0
            && !toy.TextFormat.Contains("无敌")), "SCP-914 panel still advertises removed godmode behavior");

        PrimitiveObjectToy[] backings = PrimitiveObjectToy.List
            .Where(toy => toy.Parent == door.Transform
                && toy.Type == PrimitiveType.Cube
                && toy.Color.a > 0.9f)
            .ToArray();
        ctx.Require(backings.Length == 2, $"expected exactly two SCP-914 panel backings, observed {backings.Length}");
        ctx.Require(backings.All(toy => (toy.Flags & PrimitiveFlags.Collidable) == 0),
            "SCP-914 panel must not alter the gate collision path");
        ctx.Require(backings.All(toy =>
                Approximately(toy.Scale.x, 11.5f)
                && Approximately(toy.Scale.y, 5.5f)
                && Approximately(toy.Scale.z, 0.25f)),
            "SCP-914 panel backing is not scaled to 10x");
        ctx.Require(labels.All(toy =>
                Approximately(toy.Scale.x, 0.12f)
                && Approximately(toy.Scale.y, 0.12f)
                && Approximately(toy.Scale.z, 0.12f)),
            "SCP-914 panel text did not retain its normal scale");
    }

    private static void AssertSurfaceVisualsAndGround(ScenarioContext ctx)
    {
        PrimitiveObjectToy[] boundaries = PrimitiveObjectToy.List
            .Where(toy => toy.Parent == null
                && toy.Type == PrimitiveType.Cube
                && Math.Abs(toy.Color.r - 0.25f) < 0.02f
                && Math.Abs(toy.Color.g - 0.85f) < 0.02f
                && Math.Abs(toy.Color.b - 1f) < 0.02f)
            .ToArray();

        PrimitiveObjectToy[] restoredWalls = boundaries
            .Where(toy => Approximately(toy.Position.z, -17.05f) || Approximately(toy.Position.z, -16.95f))
            .ToArray();
        ctx.Require(restoredWalls.Length == 2, $"expected two restored Surface boundary faces, observed {restoredWalls.Length}");
        ctx.Require(restoredWalls.All(toy =>
                Approximately(toy.Scale.x, 169f)
                && Approximately(toy.Scale.y, 36f)
                && Approximately(toy.Scale.z, 0.08f)
                && (toy.Flags & PrimitiveFlags.Collidable) == 0),
            "restored Surface boundary geometry is incorrect or collidable");

        TextToy[] labels = TextToy.List
            .Where(toy => toy.Parent == null
                && (toy.TextFormat.Contains("安全区") || toy.TextFormat.Contains("SAFE ZONE")))
            .Where(toy => Approximately(toy.Position.x, 136.45f) && Approximately(toy.Position.z, -16.86f))
            .ToArray();
        ctx.Require(labels.Length == 3, $"expected three restored Surface labels, observed {labels.Length}");
        ctx.Require(labels.All(toy => Approximately(toy.Scale.x, 0.32f)
                && Approximately(toy.Scale.y, 0.32f)
                && Approximately(toy.Scale.z, 0.32f)),
            "restored Surface labels do not use their original text scale");

        foreach (Bounds bounds in Map.EscapeZones)
        {
            Vector3 rayOrigin = new(bounds.center.x, bounds.max.y + 5f, bounds.center.z);
            ctx.Require(Physics.Raycast(new Ray(rayOrigin, Vector3.down), out RaycastHit _, bounds.size.y + 20f),
                $"no ground/collider found below native escape-zone centre {bounds.center}");
        }
    }

    private static IEnumerator<float> ProbeSurfaceWithSettlingDummy(ScenarioContext ctx, Actor actor, Player player)
    {
        Bounds bounds = Map.EscapeZones.FirstOrDefault();
        ctx.Require(bounds.size.sqrMagnitude > 0.01f, "Map.EscapeZones has no usable native bound");
        float searchRadius = Math.Max(1f, Math.Min(bounds.extents.x, bounds.extents.z) - 1f);
        Vector3 safePoint = actor.FindSafePointNear(bounds.center, searchRadius);
        ctx.Require(bounds.Contains(safePoint), $"surface safe-point probe escaped authoritative bounds: {safePoint}");
        yield return actor.GoTo(safePoint);
        yield return actor.Settle(maxDrop: 3f, timeoutSeconds: 4f);
        yield return actor.Soak(1f);
        ctx.Require(bounds.Contains(actor.Position), $"surface dummy left/fell out of the native bound after settling: {actor.Position}");

        float before = player.Health;
        actor.ApplyTeslaDamage(ProbeDamage);
        yield return ctx.Wait(0.2f);
        ctx.Require(Math.Abs(player.Health - before) < 0.01f,
            $"surface safezone failed environmental-damage protection: {before:0.##}->{player.Health:0.##}");
    }

    private static IEnumerator<float> ProbeSurfaceBlockerWithScp(ScenarioContext ctx)
    {
        Actor scp = ctx.SpawnActor("surface-blocker-scp", RoleTypeId.Scp173, SpawnSpec.Native(), useMovementProvider: false);
        yield return scp.WaitReady();

        Vector3[] requestedPoints =
        {
            new(136.45f, 295f, -21.5f),
            new(125f, 295f, -21.5f),
            new(150f, 295f, -21.5f),
        };
        Vector3? resolvedShellPoint = null;
        List<string> probeFailures = new();
        foreach (Vector3 requested in requestedPoints)
        {
            try
            {
                Vector3 candidate = scp.FindSafePointNear(requested, 2.5f);
                if (candidate.x > 91f && candidate.z >= -26f && candidate.z < -17f)
                {
                    resolvedShellPoint = candidate;
                    break;
                }
            }
            catch (RequireException exception)
            {
                probeFailures.Add(exception.Message);
            }
        }

        if (!resolvedShellPoint.HasValue)
        {
            throw new RequireException(
                $"could not resolve native ground inside the restored Surface blocker band: {string.Join(" | ", probeFailures)}");
        }

        Vector3 shellPoint = resolvedShellPoint.Value;
        yield return scp.GoTo(shellPoint);
        yield return scp.Settle(maxDrop: 3f, timeoutSeconds: 4f);
        ctx.Require(scp.Position.x > 91f && scp.Position.z >= -26f && scp.Position.z < -17f,
            $"SCP blocker probe did not remain in the restored Surface band: {scp.Position}");

        Player scpPlayer = Player.Get(scp.PlayerId)
            ?? throw new RequireException("Surface blocker SCP has no LabAPI player wrapper");
        float before = scpPlayer.Health;
        yield return ctx.Wait(5.5f);
        ctx.Require(scpPlayer.Health < before - 0.01f,
            $"SCP class bypassed the Surface blocker drain: {before:0.##}->{scpPlayer.Health:0.##}");
    }

    private static bool Approximately(float left, float right) => Math.Abs(left - right) < 0.15f;
}

internal static class BoundsTestExtensions
{
    public static Bounds ExpandCopy(this Bounds bounds, float amount)
    {
        bounds.Expand(amount);
        return bounds;
    }
}
