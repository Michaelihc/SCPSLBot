#nullable enable

using System;
using System.Collections.Generic;
using CustomItems;
using CustomPlayerEffects;
using Footprinting;
using InventorySystem.Items.Armor;
using LabApi.Features.Wrappers;
using MEC;
using PlayerStatsSystem;
using SCPSLBot.Warmup.Policy;
using UnityEngine;
using Utils;
using Logger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Warmup.SpecialWeapons.Weapons;

/// <summary>
/// A tracked shotgun carrier whose native viewmodel is replaced by a launcher model. Primary Use fires
/// a slow missile; a visible enemy near the crosshair receives a lock, otherwise it flies straight.
/// The reload animation is the only delay between shots, and it survives switching away.
/// </summary>
internal sealed class GuidedRocketWeapon : SpecialWeapon
{
    public const string WeaponId = "guided-rocket";
    public const string ProjectileModel = "guided-rocket-projectile";

    private const string LauncherModel = "guided-rocket-launcher";
    private const string HintTag = "special_weapon_rocket";
    private const float PromptY = 760f;
    private const float ReloadHintY = 700f;
    private const float ChestHeight = 1.05f;
    private const int RocketPenetrationPercent = 50;
    private const int NeutralPenetrationPercent = 100;
    private const int ReloadBarCells = 12;
    private const float ReloadHintSeconds = 0.35f;
    private const float ReloadHintIntervalSeconds = 0.12f;

    private readonly GuidedRocketConfig config;
    private readonly HeldMeshManager heldMesh = new("GuidedRocket");
    private readonly Dictionary<ushort, Launcher> launchers = new();
    private readonly HashSet<GuidedMissile> missiles = new();

    public GuidedRocketWeapon(GuidedRocketConfig config, ISpecialWeaponHost host)
        : base(SpecialWeaponKind.GuidedRocket, WeaponId, ItemType.GunShotgun, host)
    {
        this.config = config;
    }

    public static int SightMask => SpecialWeaponRaycasts.WorldWithoutHitboxes;

    public override bool IsEnabled => config.Enabled;

    public override bool UsesAbilityKeys => true;

    public override void Disable() => OnRoundReset();

    public override void OnGranted(Player player, ushort serial) =>
        launchers[serial] = new Launcher(Mathf.Max(1, config.RocketsPerLauncher));

    public override void OnSelected(Player player, ushort serial)
    {
        if (!launchers.TryGetValue(serial, out Launcher launcher))
        {
            return;
        }

        launcher.IsSelected = true;
        if (launcher.IsReloading && Time.timeSinceLevelLoad >= launcher.ReloadEndsAt)
        {
            FinishReload(player, launcher);
            return;
        }

        if (launcher.IsReloading)
        {
            HideNativeViewmodel(player, launcher);
            EnsureReloadRunning(player, launcher);
            return;
        }

        ShowLauncher(player);
        Prompt(player, Host.Text(
            player,
            $"<color=#3E86D6><b>GUIDED ROCKET LAUNCHER</b></color> · {launcher.Rockets} rockets\nPrimary Use fires. Bind it under Settings > Server-Specific > Gameplay.",
            $"<color=#3E86D6><b>制导火箭筒</b></color> · {launcher.Rockets} 发\n按“主要使用”发射。请在 设置 > 服务器专属设置 > 技能按键 绑定。"), 6f);
    }

    public override bool AbsorbForcedDeselect(Player player, ushort serial)
    {
        if (launchers.TryGetValue(serial, out Launcher launcher) && launcher.AbsorbNextDeselect)
        {
            launcher.AbsorbNextDeselect = false;
            return true;
        }

        return heldMesh.AbsorbForcedNone(player);
    }

    public override void OnDeselected(Player player, ushort serial)
    {
        heldMesh.Hide(player);
        if (!player.IsDestroyed)
        {
            Host.RemoveHint(player, HintTag);
        }

        if (launchers.TryGetValue(serial, out Launcher launcher))
        {
            // The deadline stays, so switching weapons cannot skip the reload.
            launcher.IsSelected = false;
            launcher.AbsorbNextDeselect = false;
            launcher.StopRoutine();
            launcher.DestroyVisual();
        }
    }

    public override bool OnPrimaryDown(Player player, ushort serial)
    {
        if (player.IsDestroyed || !player.IsAlive || !launchers.TryGetValue(serial, out Launcher launcher))
        {
            return false;
        }

        float now = Time.timeSinceLevelLoad;
        if (launcher.IsReloading && now >= launcher.ReloadEndsAt)
        {
            FinishReload(player, launcher);
        }

        if (launcher.IsReloading)
        {
            ShowReloadStatus(player, launcher, now);
            return true;
        }

        if (!Host.CanFire(player, serial))
        {
            return true;
        }

        Player? target = AcquireTarget(player);
        Transform camera = player.Camera;
        Vector3 forward = camera.forward.normalized;
        Vector3 origin = camera.position + (forward * 0.7f) - (Vector3.up * 0.16f);
        Vector3 direction = target != null ? (Chest(target) - origin).normalized : forward;
        var missile = new GuidedMissile(this, config, player, target, origin, direction);
        if (!missile.Spawn())
        {
            Prompt(player, Host.Text(player, "Launcher fault; no rocket spent.", "发射器故障，未消耗火箭。"));
            return true;
        }

        missiles.Add(missile);
        launcher.Rockets--;
        Host.NotifyAttacked(player);
        Prompt(player, target != null
            ? Host.Text(
                player,
                $"<color=#FF773D><b>TARGET LOCKED</b></color> · {EscapeRichText(target.Nickname)} · {launcher.Rockets} left",
                $"<color=#FF773D><b>目标锁定</b></color> · {EscapeRichText(target.Nickname)} · 剩余 {launcher.Rockets} 发")
            : Host.Text(
                player,
                $"<color=#FFB347><b>MISSILE AWAY</b></color> · unguided · {launcher.Rockets} left",
                $"<color=#FFB347><b>火箭发射</b></color> · 无制导 · 剩余 {launcher.Rockets} 发"));

        if (launcher.Rockets > 0)
        {
            BeginReload(player, launcher, now);
        }
        else
        {
            Host.RemoveWeapon(player, serial);
        }

        return true;
    }

    public override void OnItemRemoved(ushort serial)
    {
        if (launchers.TryGetValue(serial, out Launcher launcher))
        {
            launcher.Stop();
            launchers.Remove(serial);
        }
    }

    public override void OnRoundReset()
    {
        foreach (GuidedMissile missile in new List<GuidedMissile>(missiles))
        {
            missile.Destroy();
        }

        foreach (Launcher launcher in launchers.Values)
        {
            launcher.Stop();
        }

        missiles.Clear();
        launchers.Clear();
        heldMesh.Clear();
    }

    internal static Vector3 Chest(Player player) => player.Position + (Vector3.up * ChestHeight);

    internal static bool HasLineOfSight(Vector3 from, Player target, Vector3 chest)
    {
        Vector3 delta = chest - from;
        float distance = delta.magnitude;
        if (distance < 0.25f
            || !Physics.Raycast(from, delta / distance, out RaycastHit hit, distance, SightMask, QueryTriggerInteraction.Ignore))
        {
            return true;
        }

        for (Transform? current = hit.collider != null ? hit.collider.transform : null; current != null; current = current.parent)
        {
            if (current == target.ReferenceHub.transform)
            {
                return true;
            }
        }

        return false;
    }

    internal bool IsValidTarget(Player owner, Player target)
    {
        if (target is not { IsDestroyed: false, IsAlive: true } || target == owner)
        {
            return false;
        }

        return !target.ReferenceHub.playerEffectsController.GetEffect<Invisible>().IsEnabled
            && Host.CanHit(owner, target);
    }

    internal void Detonate(GuidedMissile missile, Vector3 point, Player? directTarget)
    {
        Player owner = missile.Owner;
        int hits = 0;
        float radius = Mathf.Max(0.1f, config.SplashRadius);
        foreach (Player target in Player.List)
        {
            if (IsValidTarget(owner, target) && TryDamage(owner, target, point, radius, target == directTarget))
            {
                hits++;
            }
        }

        try
        {
            ExplosionUtils.ServerSpawnEffect(point, ItemType.GrenadeHE);
        }
        catch (Exception exception)
        {
            Logger.Warn($"[BotRoundControl:SpecialWeapons] Rocket explosion effect failed: {exception.GetBaseException().Message}");
        }

        if (hits > 0 && !owner.IsDestroyed)
        {
            Hitmarker.SendHitmarkerDirectly(owner.ReferenceHub, 1f);
        }
    }

    internal void OnMissileFinished(GuidedMissile missile) => missiles.Remove(missile);

    private bool TryDamage(Player owner, Player target, Vector3 point, float radius, bool isDirectHit)
    {
        Vector3 chest = Chest(target);
        float distance = Vector3.Distance(point, chest);
        if (distance > radius || (!isDirectHit && !HasLineOfSight(point, target, chest)))
        {
            return false;
        }

        float damage = SpecialWeaponBallistics.BlastDamage(config.CenterDamage, distance, radius);
        if (damage <= 0.5f)
        {
            return false;
        }

        Vector3 away = chest - point;
        Vector3 force = away.sqrMagnitude > 0.001f
            ? away.normalized * (1f - Mathf.Clamp01(distance / radius)) * 4f
            : Vector3.up * 0.5f;

        // The native handler applies armor from the attacker's inventory, so the victim's vest is applied
        // here and the neutral penetration keeps the attacker's vest from counting a second time.
        int vestEfficacy = target.ReferenceHub.inventory.TryGetBodyArmor(out BodyArmor armor) ? armor.VestEfficacy : 0;
        float adjusted = SpecialWeaponBallistics.ApplyVictimArmor(damage, vestEfficacy, RocketPenetrationPercent);
        var handler = new ExplosionDamageHandler(new Footprint(owner.ReferenceHub), force, adjusted, NeutralPenetrationPercent, ExplosionType.Grenade);
        return target.Damage(handler);
    }

    private Player? AcquireTarget(Player owner)
    {
        Vector3 eye = owner.Camera.position;
        Vector3 forward = owner.Camera.forward;
        float rangeSquared = config.LockRange * config.LockRange;
        float minimumDot = Mathf.Cos(Mathf.Clamp(config.LockConeHalfAngleDegrees, 1f, 89f) * Mathf.Deg2Rad);
        Player? best = null;
        float bestScore = float.PositiveInfinity;
        foreach (Player candidate in Player.List)
        {
            if (!IsValidTarget(owner, candidate))
            {
                continue;
            }

            Vector3 chest = Chest(candidate);
            Vector3 offset = chest - eye;
            float distanceSquared = offset.sqrMagnitude;
            if (distanceSquared < 0.01f || distanceSquared > rangeSquared)
            {
                continue;
            }

            float dot = Vector3.Dot(forward, offset / Mathf.Sqrt(distanceSquared));
            if (dot < minimumDot || !HasLineOfSight(eye, candidate, chest))
            {
                continue;
            }

            // Angle dominates; distance breaks near-equal crosshair candidates.
            float score = ((1f - dot) * 10000f) + (distanceSquared * 0.001f);
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private void BeginReload(Player player, Launcher launcher, float now)
    {
        float duration = Mathf.Max(0f, config.ReloadSeconds);
        if (duration <= 0f)
        {
            return;
        }

        launcher.Stop();
        launcher.IsReloading = true;
        launcher.ReloadStartedAt = now;
        launcher.ReloadEndsAt = now + duration;
        heldMesh.Hide(player);
        EnsureReloadRunning(player, launcher);
    }

    private void EnsureReloadRunning(Player player, Launcher launcher)
    {
        if (launcher.Visual is null or { IsDestroyed: true })
        {
            var visual = new RocketReloadAnimation(player, BuildHeldSpec());
            if (visual.Spawn())
            {
                launcher.Visual = visual;
            }
            else
            {
                visual.Destroy();
            }
        }

        if (!launcher.Routine.IsRunning)
        {
            launcher.Routine = Timing.RunCoroutine(RunReload(player, launcher));
        }
    }

    private IEnumerator<float> RunReload(Player player, Launcher launcher)
    {
        float nextHintAt = 0f;
        while (launcher.IsReloading)
        {
            if (player.IsDestroyed || !player.IsAlive)
            {
                yield break;
            }

            float now = Time.timeSinceLevelLoad;
            if (now >= launcher.ReloadEndsAt)
            {
                break;
            }

            launcher.Visual?.Apply(launcher.ReloadProgress(now));
            if (now >= nextHintAt)
            {
                ShowReloadStatus(player, launcher, now);
                nextHintAt = now + ReloadHintIntervalSeconds;
            }

            yield return Timing.WaitForOneFrame;
        }

        // Cleared first: FinishReload must not kill the coroutine it is running on.
        launcher.Routine = default;
        FinishReload(player, launcher);
    }

    private void FinishReload(Player player, Launcher launcher)
    {
        launcher.Stop();
        if (!player.IsDestroyed && player.IsAlive && launcher.IsSelected)
        {
            ShowLauncher(player);
            Prompt(player, Host.Text(
                player,
                $"<color=#74E6A8><b>ROCKET READY</b></color> · {launcher.Rockets} left",
                $"<color=#74E6A8><b>装填完成</b></color> · 剩余 {launcher.Rockets} 发"), 1.2f);
        }
    }

    private void HideNativeViewmodel(Player player, Launcher launcher)
    {
        launcher.AbsorbNextDeselect = true;
        try
        {
            player.CurrentItem = null;
        }
        catch (Exception exception)
        {
            launcher.AbsorbNextDeselect = false;
            Logger.Warn($"[BotRoundControl:SpecialWeapons] Could not hide the viewmodel during reload: {exception.GetBaseException().Message}");
        }
    }

    private void ShowReloadStatus(Player player, Launcher launcher, float now)
    {
        float progress = launcher.ReloadProgress(now);
        int filled = Mathf.Clamp(Mathf.FloorToInt(progress * ReloadBarCells), 0, ReloadBarCells);
        string bar = new string('=', filled) + new string('-', ReloadBarCells - filled);
        int percent = Mathf.RoundToInt(progress * 100f);
        Host.ShowHint(
            player,
            HintTag,
            Host.Text(
                player,
                $"<color=#FFB347><b>RELOADING</b></color> [{bar}] {percent}%",
                $"<color=#FFB347><b>装填火箭</b></color> [{bar}] {percent}%"),
            ReloadHintSeconds,
            ReloadHintY);
    }

    private void ShowLauncher(Player player) => heldMesh.Show(player, BuildHeldSpec(), HeldVisualMode.HideAndReplace);

    private void Prompt(Player player, string message, float seconds = 2.8f) =>
        Host.ShowHint(player, HintTag, message, seconds, PromptY);

    // One model for every viewer, seated on the right shoulder so it also reads on the body in third person.
    // The authored grip is the model origin, and the shoulder saddle sits 0.25 m behind it.
    private static HeldMeshSpec BuildHeldSpec() => new(
        EmbeddedItemModels.Load(LauncherModel),
        new Vector3(0.21f, -0.27f, 0.32f),
        1f,
        preserveAuthoredOrigin: true,
        rotationEuler: new Vector3(0f, -2f, 0f));

    private static string EscapeRichText(string value) =>
        (value ?? string.Empty).Replace('<', '‹').Replace('>', '›');

    private sealed class Launcher
    {
        public Launcher(int rockets) => Rockets = rockets;

        public int Rockets { get; set; }

        public bool IsSelected { get; set; }

        public bool AbsorbNextDeselect { get; set; }

        public bool IsReloading { get; set; }

        public float ReloadStartedAt { get; set; }

        public float ReloadEndsAt { get; set; }

        public CoroutineHandle Routine { get; set; }

        public RocketReloadAnimation? Visual { get; set; }

        public float ReloadProgress(float now) =>
            Mathf.Clamp01((now - ReloadStartedAt) / Mathf.Max(0.01f, ReloadEndsAt - ReloadStartedAt));

        public void StopRoutine()
        {
            if (Routine.IsRunning)
            {
                Timing.KillCoroutines(Routine);
            }

            Routine = default;
        }

        public void DestroyVisual()
        {
            Visual?.Destroy();
            Visual = null;
        }

        public void Stop()
        {
            StopRoutine();
            DestroyVisual();
            IsReloading = false;
            ReloadStartedAt = 0f;
            ReloadEndsAt = 0f;
        }
    }
}
