#nullable enable

using System.Linq;
using InventorySystem.Items.Firearms.Attachments;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using PlayerStatsSystem;
using UnityEngine;

namespace SCPSLBot.Warmup.SpecialWeapons.Weapons;

/// <summary>
/// A tracked AK in a fixed fit. The client rate-limits automatic fire from its own prefab, so only a
/// native attachment can raise the cadence; the Muzzle Booster is the one the AK offers. The gun's
/// lighter round is applied per hit, and locking the fit keeps that trade from being undone.
/// </summary>
internal sealed class Ak12Weapon : SpecialWeapon
{
    public const string WeaponId = "ak-12";

    private const string HintTag = "special_weapon_ak12";
    private const float CardSeconds = 6f;
    private const float CardY = 760f;
    private const int CardTextSize = 18;

    private static readonly AttachmentName[] Fit =
    {
        AttachmentName.HoloSight,
        AttachmentName.MuzzleBooster,
        AttachmentName.StandardStock,
        AttachmentName.StandardMagAP,
        AttachmentName.Foregrip,
    };

    private readonly Ak12Config config;

    public Ak12Weapon(Ak12Config config, ISpecialWeaponHost host)
        : base(SpecialWeaponKind.Ak12, WeaponId, ItemType.GunAK, host)
    {
        this.config = config;
    }

    public override bool IsEnabled => config.Enabled;

    public override void Enable()
    {
        PlayerEvents.Hurting += OnHurting;
        PlayerEvents.ChangingAttachments += OnChangingAttachments;
    }

    public override void Disable()
    {
        PlayerEvents.Hurting -= OnHurting;
        PlayerEvents.ChangingAttachments -= OnChangingAttachments;
    }

    public override void OnGranted(Player player, ushort serial)
    {
        if (player.Items.FirstOrDefault(item => item.Serial == serial) is FirearmItem firearm)
        {
            firearm.AttachmentsCode = firearm.GetCodeFromAttachmentNamesRaw(Fit);
            player.AddAmmo(firearm.AmmoType, (ushort)Mathf.Clamp(config.ReserveAmmo, 0, ushort.MaxValue));
        }
    }

    public override void OnSelected(Player player, ushort serial) => Host.ShowHint(
        player,
        HintTag,
        Host.Text(
            player,
            "<color=#7FB3D5><b>AK-12</b></color>\nFaster fire and a lighter round than the AK. The fit is locked.",
            "<color=#7FB3D5><b>AK-12</b></color>\n射速更高、弹药更轻，配件已锁定。"),
        CardSeconds,
        CardY,
        CardTextSize);

    public override void OnDeselected(Player player, ushort serial) => Host.RemoveHint(player, HintTag);

    private void OnHurting(PlayerHurtingEventArgs ev)
    {
        if (ev.DamageHandler is FirearmDamageHandler shot
            && shot.Firearm != null
            && Host.IsWeapon(shot.Firearm.ItemSerial, Kind))
        {
            shot.Damage *= Mathf.Max(0f, config.DamageMultiplier);
        }
    }

    private void OnChangingAttachments(PlayerChangingAttachmentsEventArgs ev)
    {
        if (ev.FirearmItem != null && Host.IsWeapon(ev.FirearmItem.Serial, Kind))
        {
            ev.IsAllowed = false;
        }
    }
}