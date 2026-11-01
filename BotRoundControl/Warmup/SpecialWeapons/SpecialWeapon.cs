#nullable enable

using LabApi.Features.Wrappers;

namespace SCPSLBot.Warmup.SpecialWeapons;

/// <summary>Registry key. Add a value for each weapon.</summary>
internal enum SpecialWeaponKind
{
    GravitonDisruptor,
    GuidedRocket,
}

/// <summary>What a weapon may ask of its owner. Warmup, duel and safezone rules stay behind this.</summary>
internal interface ISpecialWeaponHost
{
    /// <summary>True when the serial is a live weapon of this kind.</summary>
    bool IsWeapon(ushort serial, SpecialWeaponKind kind);

    /// <summary>
    /// Asks every shot policy whether the player may fire this carrier. A weapon that fires without a
    /// native shot must call this first; a weapon firing through the native trigger is already covered.
    /// </summary>
    bool CanFire(Player player, ushort serial);

    /// <summary>True when an attacker may harm a target in the current warmup arena or duel.</summary>
    bool CanHit(Player attacker, Player target);

    /// <summary>Ends spawn protection for an attack that bypassed the native firearm path.</summary>
    void NotifyAttacked(Player player);

    /// <summary>Consumes a spent weapon.</summary>
    void RemoveWeapon(Player player, ushort serial);

    string Text(Player player, string english, string chinese);

    void ShowHint(Player player, string tag, string message, float seconds, float y, int size = 20);

    void RemoveHint(Player player, string tag);
}

/// <summary>
/// One weapon's behavior. <see cref="SpecialWeaponsRuntime"/> owns serial tracking, grants, input
/// routing and item lifetime, and calls these hooks only for serials this weapon owns. Every hook is
/// optional, so a weapon overrides only what it adds to its native carrier.
/// </summary>
internal abstract class SpecialWeapon
{
    protected SpecialWeapon(SpecialWeaponKind kind, string id, ItemType carrierItem, ISpecialWeaponHost host)
    {
        Kind = kind;
        Id = id;
        CarrierItem = carrierItem;
        Host = host;
    }

    public SpecialWeaponKind Kind { get; }

    /// <summary>The id used by <c>special:&lt;id&gt;</c> catalog entries and the RA command.</summary>
    public string Id { get; }

    /// <summary>The native item that holds the serial and occupies the inventory slot.</summary>
    public ItemType CarrierItem { get; }

    protected ISpecialWeaponHost Host { get; }

    public abstract bool IsEnabled { get; }

    /// <summary>True when the weapon fires from the shared ability keys instead of the native trigger.</summary>
    public virtual bool UsesAbilityKeys => false;

    /// <summary>Subscribes native hooks. Called while Standard warmup is active.</summary>
    public virtual void Enable()
    {
    }

    /// <summary>Releases everything <see cref="Enable"/> and later play created.</summary>
    public virtual void Disable()
    {
    }

    public virtual void OnGranted(Player player, ushort serial)
    {
    }

    public virtual void OnSelected(Player player, ushort serial)
    {
    }

    /// <summary>
    /// True when a deselect was this weapon hiding its own viewmodel, so the weapon stays selected.
    /// </summary>
    public virtual bool AbsorbForcedDeselect(Player player, ushort serial) => false;

    public virtual void OnDeselected(Player player, ushort serial)
    {
    }

    /// <summary>Primary Use while selected. True when the weapon consumed the press.</summary>
    public virtual bool OnPrimaryDown(Player player, ushort serial) => false;

    /// <summary>The item no longer exists, for any reason. No player is passed; the owner may be gone.</summary>
    public virtual void OnItemRemoved(ushort serial)
    {
    }

    /// <summary>All round-scoped state ends. No item from this round survives.</summary>
    public virtual void OnRoundReset()
    {
    }
}
