#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using CustomItems;
using CustomPlayerEffects;
using InventorySystem;
using InventorySystem.Items;
using InventorySystem.Items.Firearms;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using SCPSLBot.Api;
using SCPSLBot.Infrastructure;
using SCPSLBot.Presentation;
using SCPSLBot.Warmup.SpecialWeapons.Weapons;
using ServerKeybinds;
using Logger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Warmup.SpecialWeapons;

/// <summary>
/// Owns every special weapon's lifetime during Standard warmup. A weapon is bound to the player who
/// received it: it cannot be dropped or looted, and it ends with that life, so managed bots never hold one.
/// Native inventory changes stay authoritative; this class translates them into weapon hooks.
/// </summary>
internal sealed class SpecialWeaponsRuntime : ISpecialWeaponHost
{
    private const string LogPrefix = "[BotRoundControl:SpecialWeapons]";
    private const string BoundHintTag = "special_weapon_bound";

    private readonly SpecialWeaponsConfig config;
    private readonly BotLocalization localization;
    private readonly IHintDisplayProvider hints;
    private readonly ItemRegistry<SpecialWeaponKind> items = new("BotRoundControl.SpecialWeapons");
    private readonly Dictionary<string, SpecialWeapon> weaponsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SpecialWeaponKind, SpecialWeapon> weaponsByKind = new();
    private readonly Dictionary<ReferenceHub, Selection> selections = new(ManagedReferenceComparer<ReferenceHub>.Instance);
    private IDisposable? abilityKeys;
    private bool enabled;

    public SpecialWeaponsRuntime(SpecialWeaponsConfig config, HintDisplayConfig hintDisplay, string language)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        localization = new BotLocalization(language);
        hints = new HsmHintDisplayProvider(hintDisplay, "BotRoundControl.SpecialWeapons");

        // A new weapon registers here and adds a row to SpecialWeaponCatalogDefaults.
        Register(new GravitonDisruptorWeapon(config.GravitonDisruptor, this));
        Register(new GuidedRocketWeapon(config.GuidedRocket, this));
    }

    public IEnumerable<string> WeaponIds => weaponsById.Keys.OrderBy(id => id, StringComparer.Ordinal);

    public bool IsAvailable(string weaponId) =>
        config.Enabled
        && weaponsById.TryGetValue(weaponId ?? string.Empty, out SpecialWeapon? weapon)
        && weapon.IsEnabled;

    public void Enable()
    {
        if (enabled || !config.Enabled)
        {
            return;
        }

        enabled = true;
        hints.Enable();
        foreach (SpecialWeapon weapon in weaponsById.Values.Where(weapon => weapon.IsEnabled))
        {
            weapon.Enable();
        }

        if (weaponsById.Values.Any(weapon => weapon.IsEnabled && weapon.UsesAbilityKeys))
        {
            abilityKeys = AbilityKeys.Acquire("BotRoundControl.SpecialWeapons", OnAbilityKeyDown);
        }

        PlayerEvents.ChangedItem += OnChangedItem;
        PlayerEvents.DroppingItem += OnDroppingItem;
        PlayerEvents.Dying += OnDying;
        PlayerEvents.ChangingRole += OnChangingRole;
        PlayerEvents.Left += OnLeft;
        ServerEvents.WaitingForPlayers += ResetRound;
        ServerEvents.RoundRestarted += ResetRound;
        InventoryExtensions.OnInventoryDropped += OnInventoryDropped;
        ItemBase.OnItemRemoved += OnItemRemoved;
    }

    public void Disable()
    {
        if (!enabled)
        {
            return;
        }

        enabled = false;
        ServerEvents.WaitingForPlayers -= ResetRound;
        ServerEvents.RoundRestarted -= ResetRound;
        PlayerEvents.ChangedItem -= OnChangedItem;
        PlayerEvents.DroppingItem -= OnDroppingItem;
        PlayerEvents.Dying -= OnDying;
        PlayerEvents.ChangingRole -= OnChangingRole;
        PlayerEvents.Left -= OnLeft;
        InventoryExtensions.OnInventoryDropped -= OnInventoryDropped;
        ItemBase.OnItemRemoved -= OnItemRemoved;
        abilityKeys?.Dispose();
        abilityKeys = null;

        // Leaving warmup takes the weapons back, so they never reach a normal round.
        foreach (Player player in Player.List.ToArray())
        {
            RemoveWeapons(player);
        }

        ResetRound();
        foreach (SpecialWeapon weapon in weaponsById.Values.Where(weapon => weapon.IsEnabled))
        {
            weapon.Disable();
        }

        hints.Disable();
    }

    public bool TryGrant(Player player, string weaponId)
    {
        if (!enabled
            || !IsAvailable(weaponId)
            || player == null
            || player.IsDestroyed
            || !player.IsAlive
            || !player.IsHuman
            || ManagedBotIdentity.IsManaged(player))
        {
            return false;
        }

        SpecialWeapon weapon = weaponsById[weaponId];
        Item? item = player.AddItem(weapon.CarrierItem);
        if (item == null)
        {
            return false;
        }

        items.TrackGranted(item.Serial, weapon.Kind, player.UserId, "warmup-grant");
        weapon.OnGranted(player, item.Serial);
        return true;
    }

    /// <summary>Presses Primary Use for an RA walkthrough, exactly as the bound key would.</summary>
    public bool PressPrimary(Player player) => OnAbilityKeyDown(player, AbilityKey.PrimaryUse, false);

    public string DescribeState()
    {
        IReadOnlyDictionary<ushort, SpecialWeaponKind> tracked = items.Snapshot();
        string serials = string.Join(", ", tracked.Select(pair => $"{pair.Key}={pair.Value}"));
        return $"enabled={enabled} tracked={tracked.Count} selected={selections.Count}"
            + (serials.Length > 0 ? $" [{serials}]" : string.Empty);
    }

    bool ISpecialWeaponHost.IsWeapon(ushort serial, SpecialWeaponKind kind) => items.IsKind(serial, kind);

    bool ISpecialWeaponHost.CanFire(Player player, ushort serial)
    {
        // The duel and safezone policies are subscribed to the native pre-shot event, and the safezone
        // lives in a separate plugin. Asking the same event keeps them the only owners of those rules.
        Firearm? firearm = FindItem(player, serial)?.Base as Firearm;
        if (firearm == null)
        {
            return false;
        }

        var shooting = new PlayerShootingWeaponEventArgs(player.ReferenceHub, firearm);
        PlayerEvents.OnShootingWeapon(shooting);
        return shooting.IsAllowed;
    }

    bool ISpecialWeaponHost.CanHit(Player attacker, Player target)
    {
        ReferenceHub? attackerHub = attacker?.ReferenceHub;
        ReferenceHub? targetHub = target?.ReferenceHub;
        return attackerHub != null
            && targetHub != null
            && HitboxIdentity.IsDamageable(attackerHub, targetHub)
            && WarmupManager.Instance.CanWeaponHit(attackerHub, targetHub);
    }

    void ISpecialWeaponHost.NotifyAttacked(Player player)
    {
        PlayerEffectsController? effects = player?.ReferenceHub?.playerEffectsController;
        if (effects != null && effects.GetEffect<SpawnProtected>().IsEnabled)
        {
            effects.DisableEffect<SpawnProtected>();
        }
    }

    void ISpecialWeaponHost.RemoveWeapon(Player player, ushort serial)
    {
        Item? item = FindItem(player, serial);
        if (item != null)
        {
            player.RemoveItem(item);
        }
    }

    string ISpecialWeaponHost.Text(Player player, string english, string chinese) =>
        localization.IsEnglish(player) ? english : chinese;

    void ISpecialWeaponHost.ShowHint(Player player, string tag, string message, float seconds, float y, int size) =>
        hints.Show(player, new HintRequest(tag, message, seconds, 0f, y, size));

    void ISpecialWeaponHost.RemoveHint(Player player, string tag) => hints.Remove(player, tag);

    private void Register(SpecialWeapon weapon)
    {
        weaponsById.Add(weapon.Id, weapon);
        weaponsByKind.Add(weapon.Kind, weapon);
    }

    private bool OnAbilityKeyDown(Player player, AbilityKey key, bool modifierHeld)
    {
        return key == AbilityKey.PrimaryUse
            && player?.ReferenceHub != null
            && selections.TryGetValue(player.ReferenceHub, out Selection selection)
            && selection.Weapon.OnPrimaryDown(player, selection.Serial);
    }

    private void OnChangedItem(PlayerChangedItemEventArgs ev)
    {
        Player player = ev.Player;
        if (player == null || player.IsDestroyed)
        {
            return;
        }

        ReferenceHub hub = player.ReferenceHub;
        bool hasSelection = selections.TryGetValue(hub, out Selection current);
        Item? newItem = ev.NewItem;
        if (newItem == null && hasSelection && current.Weapon.AbsorbForcedDeselect(player, current.Serial))
        {
            return;
        }

        if (hasSelection && (newItem == null || newItem.Serial != current.Serial))
        {
            selections.Remove(hub);
            current.Weapon.OnDeselected(player, current.Serial);
        }

        if (newItem != null && items.TryGetKind(newItem.Serial, out SpecialWeaponKind kind))
        {
            // Stored first: a weapon that hides its viewmodel re-enters this handler synchronously.
            SpecialWeapon weapon = weaponsByKind[kind];
            selections[hub] = new Selection(weapon, newItem.Serial);
            weapon.OnSelected(player, newItem.Serial);
        }
    }

    private void OnDroppingItem(PlayerDroppingItemEventArgs ev)
    {
        if (ev.Item == null || !items.IsTracked(ev.Item.Serial))
        {
            return;
        }

        ev.IsAllowed = false;
        Player player = ev.Player;
        hints.Show(player, new HintRequest(
            BoundHintTag,
            ((ISpecialWeaponHost)this).Text(player, "This weapon is bound to you.", "这件武器与你绑定，无法丢弃。"),
            2f,
            0f,
            760f,
            20));
    }

    private void OnDying(PlayerDyingEventArgs ev) => RemoveWeapons(ev.Player);

    private void OnChangingRole(PlayerChangingRoleEventArgs ev) => RemoveWeapons(ev.Player);

    private void OnLeft(PlayerLeftEventArgs ev)
    {
        Player player = ev.Player;
        if (player?.ReferenceHub != null && selections.TryGetValue(player.ReferenceHub, out Selection selection))
        {
            selections.Remove(player.ReferenceHub);
            selection.Weapon.OnDeselected(player, selection.Serial);
        }
    }

    // ServerDropEverything raises no drop event for each item. This runs before it snapshots the serials.
    private void OnInventoryDropped(ReferenceHub hub)
    {
        if (hub != null)
        {
            RemoveWeapons(Player.Get(hub));
        }
    }

    private void OnItemRemoved(ItemBase item)
    {
        ushort serial = item.ItemSerial;
        if (!items.TryGetKind(serial, out SpecialWeaponKind kind))
        {
            return;
        }

        items.Untrack(serial, "item-removed");
        SpecialWeapon weapon = weaponsByKind[kind];
        ReferenceHub owner = item.Owner;
        if (!ReferenceEquals(owner, null)
            && selections.TryGetValue(owner, out Selection selection)
            && selection.Serial == serial)
        {
            selections.Remove(owner);
            if (owner != null)
            {
                selection.Weapon.OnDeselected(Player.Get(owner), serial);
            }
        }

        weapon.OnItemRemoved(serial);
    }

    private void RemoveWeapons(Player? player)
    {
        if (player == null || player.IsDestroyed)
        {
            return;
        }

        foreach (Item item in player.Items.Where(item => items.IsTracked(item.Serial)).ToArray())
        {
            try
            {
                player.RemoveItem(item);
            }
            catch (Exception exception)
            {
                Logger.Warn($"{LogPrefix} Failed to remove weapon serial {item.Serial}: {exception.GetBaseException().Message}");
            }
        }
    }

    private void ResetRound()
    {
        selections.Clear();
        items.Clear();
        foreach (SpecialWeapon weapon in weaponsById.Values.Where(weapon => weapon.IsEnabled))
        {
            weapon.OnRoundReset();
        }
    }

    private static Item? FindItem(Player player, ushort serial) =>
        player == null || player.IsDestroyed ? null : player.Items.FirstOrDefault(item => item.Serial == serial);

    private readonly struct Selection
    {
        public Selection(SpecialWeapon weapon, ushort serial)
        {
            Weapon = weapon;
            Serial = serial;
        }

        public SpecialWeapon Weapon { get; }

        public ushort Serial { get; }
    }
}
