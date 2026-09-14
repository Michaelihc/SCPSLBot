using System;
using System.Collections.Generic;
using System.Linq;
using CustomPlayerEffects;
using InventorySystem.Items.Jailbird;
using InventorySystem.Items.MicroHID.Modules;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.Scp049Events;
using LabApi.Events.Arguments.Scp096Events;
using LabApi.Events.Arguments.Scp106Events;
using LabApi.Events.Arguments.Scp173Events;
using LabApi.Events.Arguments.Scp3114Events;
using LabApi.Events.Arguments.Scp939Events;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PlayerRoles.PlayableScps.Scp096;
using ScpslPluginStarter.Core;
using JailbirdItem = LabApi.Features.Wrappers.JailbirdItem;

namespace ScpslPluginStarter.Services;

internal sealed class SafezoneEnforcementService
{
    private readonly WarmupSafezoneConfig _config;
    private readonly SafezoneOccupancyService _occupancy;
    private readonly ExitProtectionService _exitProtection;
    private readonly OwnedDamageRegistry _ownedDamage;
    private readonly SurfaceBlockerService _blocker;
    private readonly IHintDisplayProvider _hints;
    private readonly WarmupLocalization _localization;
    private readonly Dictionary<int, long> _lastActionHintMilliseconds = new();
    private readonly Core.IMonotonicClock _clock;
    private bool _enabled;

    public SafezoneEnforcementService(
        WarmupSafezoneConfig config,
        SafezoneOccupancyService occupancy,
        ExitProtectionService exitProtection,
        OwnedDamageRegistry ownedDamage,
        SurfaceBlockerService blocker,
        IHintDisplayProvider hints,
        WarmupLocalization localization,
        Core.IMonotonicClock clock)
    {
        _config = config;
        _occupancy = occupancy;
        _exitProtection = exitProtection;
        _ownedDamage = ownedDamage;
        _blocker = blocker;
        _hints = hints;
        _localization = localization;
        _clock = clock;
    }

    public void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        PlayerEvents.Hurting += OnHurting;
        PlayerEvents.UpdatingEffect += OnUpdatingEffect;
        PlayerEvents.UpdatedEffect += OnUpdatedEffect;
        PlayerEvents.ShootingWeapon += OnShootingWeapon;
        PlayerEvents.DryFiringWeapon += OnDryFiringWeapon;
        PlayerEvents.UsingItem += OnUsingItem;
        PlayerEvents.ItemUsageEffectsApplying += OnItemUsageEffectsApplying;
        PlayerEvents.ProcessingJailbirdMessage += OnProcessingJailbirdMessage;
        PlayerEvents.DroppingItem += OnDroppingItem;
        PlayerEvents.ThrowingItem += OnThrowingItem;
        PlayerEvents.ThrowingProjectile += OnThrowingProjectile;
        PlayerEvents.Left += OnLeft;
        PlayerEvents.Dying += OnDying;
        PlayerEvents.ChangingRole += OnChangingRole;
        Scp049Events.Attacking += OnScp049Attacking;
        Scp096Events.AddingTarget += OnScp096AddingTarget;
        Scp096Events.Charging += OnScp096Charging;
        Scp106Events.TeleportingPlayer += OnScp106TeleportingPlayer;
        Scp173Events.Snapping += OnScp173Snapping;
        Scp173Events.CreatingTantrum += OnScp173CreatingTantrum;
        Scp3114Events.StrangleStarting += OnScp3114StrangleStarting;
        Scp939Events.Attacking += OnScp939Attacking;
        Scp939Events.Lunging += OnScp939Lunging;
        Scp939Events.CreatingAmnesticCloud += OnScp939CreatingCloud;
    }

    public void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        Scp939Events.CreatingAmnesticCloud -= OnScp939CreatingCloud;
        Scp939Events.Lunging -= OnScp939Lunging;
        Scp939Events.Attacking -= OnScp939Attacking;
        Scp3114Events.StrangleStarting -= OnScp3114StrangleStarting;
        Scp173Events.CreatingTantrum -= OnScp173CreatingTantrum;
        Scp173Events.Snapping -= OnScp173Snapping;
        Scp106Events.TeleportingPlayer -= OnScp106TeleportingPlayer;
        Scp096Events.Charging -= OnScp096Charging;
        Scp096Events.AddingTarget -= OnScp096AddingTarget;
        Scp049Events.Attacking -= OnScp049Attacking;
        PlayerEvents.ChangingRole -= OnChangingRole;
        PlayerEvents.Dying -= OnDying;
        PlayerEvents.Left -= OnLeft;
        PlayerEvents.ThrowingProjectile -= OnThrowingProjectile;
        PlayerEvents.ThrowingItem -= OnThrowingItem;
        PlayerEvents.DroppingItem -= OnDroppingItem;
        PlayerEvents.ProcessingJailbirdMessage -= OnProcessingJailbirdMessage;
        PlayerEvents.ItemUsageEffectsApplying -= OnItemUsageEffectsApplying;
        PlayerEvents.UsingItem -= OnUsingItem;
        PlayerEvents.DryFiringWeapon -= OnDryFiringWeapon;
        PlayerEvents.ShootingWeapon -= OnShootingWeapon;
        PlayerEvents.UpdatedEffect -= OnUpdatedEffect;
        PlayerEvents.UpdatingEffect -= OnUpdatingEffect;
        PlayerEvents.Hurting -= OnHurting;
        _lastActionHintMilliseconds.Clear();
    }

    public void TickDangerousItems()
    {
        if (!IsActive)
        {
            return;
        }

        foreach (Player player in Player.List.Where(SafezoneVolumeService.IsEligible))
        {
            if (IsRestrictedAtEvent(player)
                && (IsChargingOrFiringMicroHid(player) || IsJailbirdCharging(player) || IsUsingScp244(player)))
            {
                StopDangerousItem(player);
                SendActionBlocked(player);
            }
        }
    }

    public void TickScp096Calm()
    {
        if (!IsActive)
        {
            return;
        }

        foreach (Player player in Player.List.Where(SafezoneVolumeService.IsEligible))
        {
            if ((_occupancy.ResolveAtEvent(player) & (SafezoneMembership.SurfaceEscape | SafezoneMembership.Scp914)) != 0)
            {
                TryEndScp096Rage(player);
            }
        }
    }

    private bool IsActive => _config.Enabled;

    private void OnHurting(PlayerHurtingEventArgs ev)
    {
        if (!IsActive || !SafezoneVolumeService.IsEligible(ev.Player))
        {
            return;
        }

        bool victimInZone = _occupancy.ResolveAtEvent(ev.Player) != SafezoneMembership.None;
        bool victimExitProtected = _exitProtection.IsProtected(ev.Player.PlayerId);
        DamageActorKind attackerKind = ev.Attacker == null
            ? DamageActorKind.None
            : ev.Attacker.PlayerId == ev.Player.PlayerId ? DamageActorKind.Self : DamageActorKind.Other;
        bool attackerInZone = false;
        bool attackerExitProtected = false;
        if (ev.Attacker != null && SafezoneVolumeService.IsEligible(ev.Attacker))
        {
            attackerInZone = _occupancy.ResolveAtEvent(ev.Attacker) != SafezoneMembership.None;
            attackerExitProtected = _exitProtection.IsProtected(ev.Attacker.PlayerId);
        }

        if (!DamagePolicy.ShouldBlock(
                attackerKind,
                attackerInZone,
                attackerExitProtected,
                victimInZone,
                victimExitProtected,
                _ownedDamage.Contains(ev.Player.PlayerId)))
        {
            return;
        }

        ev.IsAllowed = false;
        if (attackerKind == DamageActorKind.Other && ev.Attacker != null && (attackerInZone || attackerExitProtected))
        {
            StopDangerousItem(ev.Attacker);
            SendActionBlocked(ev.Attacker);
        }
    }

    private void OnUpdatingEffect(PlayerEffectUpdatingEventArgs ev)
    {
        if (IsActive && IsInZoneAtEvent(ev.Player) && IsFlashEffect(ev.Effect))
        {
            ev.IsAllowed = false;
            ev.Intensity = 0;
            ev.Duration = 0f;
        }
    }

    private void OnUpdatedEffect(PlayerEffectUpdatedEventArgs ev)
    {
        if (IsActive && IsInZoneAtEvent(ev.Player) && IsFlashEffect(ev.Effect))
        {
            ev.Player.DisableEffect(ev.Effect);
        }
    }

    private void OnShootingWeapon(PlayerShootingWeaponEventArgs ev) => CancelRestrictedAction(ev.Player, SafezoneActionKind.Firearm, () => ev.IsAllowed = false);
    private void OnDryFiringWeapon(PlayerDryFiringWeaponEventArgs ev) => CancelRestrictedAction(ev.Player, SafezoneActionKind.DryFire, () => ev.IsAllowed = false);

    private void OnUsingItem(PlayerUsingItemEventArgs ev)
    {
        if (IsBlockedDangerousCurrentItem(ev.Player))
        {
            CancelRestrictedAction(ev.Player, SafezoneActionKind.ChargedDangerousItem, () =>
            {
                ev.IsAllowed = false;
                StopDangerousItem(ev.Player);
            });
        }
    }

    private void OnItemUsageEffectsApplying(PlayerItemUsageEffectsApplyingEventArgs ev)
    {
        if (ev.UsableItem is Scp244Item && IsRestrictedAtEvent(ev.Player))
        {
            ev.IsAllowed = false;
            ev.ContinueProcess = false;
            StopDangerousItem(ev.Player);
            SendActionBlocked(ev.Player);
        }
    }

    private void OnProcessingJailbirdMessage(PlayerProcessingJailbirdMessageEventArgs ev)
    {
        if (IsJailbirdDangerousUseMessage(ev.Message))
        {
            CancelRestrictedAction(ev.Player, SafezoneActionKind.ChargedDangerousItem, () =>
            {
                ev.IsAllowed = false;
                ev.AllowAttack = false;
                StopDangerousItem(ev.Player);
            });
        }
    }

    private void OnDroppingItem(PlayerDroppingItemEventArgs ev)
    {
        if (ev.Throw)
        {
            CancelRestrictedAction(ev.Player, SafezoneActionKind.Throwable, () =>
            {
                ev.IsAllowed = false;
                ev.Throw = false;
            });
        }
    }

    private void OnThrowingItem(PlayerThrowingItemEventArgs ev) => CancelRestrictedAction(ev.Player, SafezoneActionKind.Throwable, () => ev.IsAllowed = false);
    private void OnThrowingProjectile(PlayerThrowingProjectileEventArgs ev) => CancelRestrictedAction(ev.Player, SafezoneActionKind.Throwable, () => ev.IsAllowed = false);

    private void OnScp049Attacking(Scp049AttackingEventArgs ev) =>
        CancelRestrictedOffense(ev.Player, ev.Target, SafezoneActionKind.ScpTargetedOffense, () => ev.IsAllowed = false);

    private void OnScp096AddingTarget(Scp096AddingTargetEventArgs ev) =>
        CancelRestrictedOffense(ev.Player, ev.Target, SafezoneActionKind.ScpTargetedOffense, () => ev.IsAllowed = false);

    private void OnScp096Charging(Scp096ChargingEventArgs ev) =>
        CancelRestrictedAction(ev.Player, SafezoneActionKind.ScpAreaOffense, () => ev.IsAllowed = false);

    private void OnScp106TeleportingPlayer(Scp106TeleportingPlayerEvent ev) =>
        CancelRestrictedOffense(ev.Player, ev.Target, SafezoneActionKind.ScpTargetedOffense, () => ev.IsAllowed = false);

    private void OnScp173Snapping(Scp173SnappingEventArgs ev) =>
        CancelRestrictedOffense(ev.Player, ev.Target, SafezoneActionKind.ScpTargetedOffense, () => ev.IsAllowed = false);

    private void OnScp173CreatingTantrum(Scp173CreatingTantrumEventArgs ev) =>
        CancelRestrictedAction(ev.Player, SafezoneActionKind.ScpAreaOffense, () => ev.IsAllowed = false);

    private void OnScp3114StrangleStarting(Scp3114StrangleStartingEventArgs ev) =>
        CancelRestrictedOffense(ev.Player, ev.Target, SafezoneActionKind.ScpTargetedOffense, () => ev.IsAllowed = false);

    private void OnScp939Attacking(Scp939AttackingEventArgs ev) =>
        CancelRestrictedOffense(ev.Player, ev.Target, SafezoneActionKind.ScpTargetedOffense, () => ev.IsAllowed = false);

    private void OnScp939Lunging(Scp939LungingEventArgs ev) =>
        CancelRestrictedAction(ev.Player, SafezoneActionKind.ScpAreaOffense, () => ev.IsAllowed = false);

    private void OnScp939CreatingCloud(Scp939CreatingAmnesticCloudEventArgs ev) =>
        CancelRestrictedAction(ev.Player, SafezoneActionKind.ScpAreaOffense, () => ev.IsAllowed = false);

    private void OnLeft(PlayerLeftEventArgs ev) => Forget(ev.Player, clearProtection: true);
    private void OnDying(PlayerDyingEventArgs ev) => Forget(ev.Player, clearProtection: true);
    private void OnChangingRole(PlayerChangingRoleEventArgs ev) => Forget(ev.Player, clearProtection: false);

    private void Forget(Player player, bool clearProtection)
    {
        // ChangingRole is cancellable and fires before the native mutation. Never let a role
        // request erase an already-active exit-protection lease; death/disconnect still clear it.
        _occupancy.Forget(player.PlayerId, clearProtection);
        _blocker.Forget(player);
        _lastActionHintMilliseconds.Remove(player.PlayerId);
        _hints.Clear(player);
    }

    private void CancelRestrictedAction(Player player, SafezoneActionKind action, Action cancel)
    {
        if (!IsActive)
        {
            return;
        }

        bool actorProtected = IsRestrictedAtEvent(player);
        if (!ActionPolicy.ShouldCancel(action, actorProtected, targetProtected: false))
        {
            return;
        }

        cancel();
        SendActionBlocked(player);
    }

    private void CancelRestrictedOffense(Player actor, Player? target, SafezoneActionKind action, Action cancel)
    {
        if (!IsActive)
        {
            return;
        }

        bool actorRestricted = IsRestrictedAtEvent(actor);
        bool targetProtected = target != null && IsRestrictedAtEvent(target);
        if (!ActionPolicy.ShouldCancel(action, actorRestricted, targetProtected))
        {
            return;
        }

        cancel();
        SendActionBlocked(actor);
    }

    private bool IsInZoneAtEvent(Player player) => SafezoneVolumeService.IsEligible(player)
        && _occupancy.ResolveAtEvent(player) != SafezoneMembership.None;

    private bool IsRestrictedAtEvent(Player player) => SafezoneVolumeService.IsEligible(player)
        && (_occupancy.ResolveAtEvent(player) != SafezoneMembership.None || _exitProtection.IsProtected(player.PlayerId));

    private void SendActionBlocked(Player player)
    {
        long now = _clock.NowMilliseconds;
        if (_lastActionHintMilliseconds.TryGetValue(player.PlayerId, out long last) && now - last < 1250L)
        {
            return;
        }

        _lastActionHintMilliseconds[player.PlayerId] = now;
        _hints.ShowPrompt(
            player,
            "action-blocked",
            _config.HintDisplay.ActionPromptY,
            _localization.For(
                player,
                "<color=#8fe7ff><b>SAFEZONE</b></color> · Action blocked.",
                "<color=#8fe7ff><b>安全区保护</b></color> · 操作已阻止。"),
            1.5f);
    }

    private static bool IsFlashEffect(StatusEffectBase effect) =>
        effect is Flashed or Blindness or Deafened or Concussed;

    private static bool IsJailbirdDangerousUseMessage(JailbirdMessageType message) =>
        message is JailbirdMessageType.AttackTriggered
            or JailbirdMessageType.AttackPerformed
            or JailbirdMessageType.ChargeLoadTriggered
            or JailbirdMessageType.ChargeStarted;

    private static bool IsBlockedDangerousCurrentItem(Player player)
    {
        Item? item = player.CurrentItem;
        return item is MicroHIDItem
            or JailbirdItem
            or Scp244Item
            or ThrowableItem
            || item?.Type is ItemType.GrenadeHE
                or ItemType.GrenadeFlash
                or ItemType.SCP018
                or ItemType.Snowball;
    }

    private static void StopDangerousItem(Player player)
    {
        if (player.CurrentItem is MicroHIDItem microHid && IsDangerousMicroHidPhase(microHid.Phase))
        {
            microHid.Phase = MicroHidPhase.Standby;
        }

        if (player.CurrentItem is JailbirdItem jailbird && jailbird.IsCharging)
        {
            jailbird.Reset();
        }

        if (player.CurrentItem is Scp244Item scp244 && scp244.IsUsing)
        {
            scp244.IsUsing = false;
        }
    }

    private static bool IsUsingScp244(Player player) =>
        player.CurrentItem is Scp244Item scp244 && scp244.IsUsing;
    private static bool IsJailbirdCharging(Player player) => player.CurrentItem is JailbirdItem jailbird && jailbird.IsCharging;
    private static bool IsChargingOrFiringMicroHid(Player player) =>
        player.CurrentItem is MicroHIDItem microHid && IsDangerousMicroHidPhase(microHid.Phase);
    private static bool IsDangerousMicroHidPhase(MicroHidPhase phase) =>
        phase is MicroHidPhase.WindingUp or MicroHidPhase.WoundUpSustain or MicroHidPhase.Firing;

    private static void TryEndScp096Rage(Player player)
    {
        if (player.Role != RoleTypeId.Scp096)
        {
            return;
        }

        if (player.ReferenceHub?.roleManager.CurrentRole is Scp096Role role
            && role.SubroutineModule.TryGetSubroutine(out Scp096RageManager rage)
            && rage.IsEnragedOrDistressed)
        {
            rage.ServerEndEnrage(true);
        }
    }
}
