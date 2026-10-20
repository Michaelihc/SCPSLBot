using Interactables.Interobjects.DoorUtils;
using InventorySystem;
using InventorySystem.Items;
using InventorySystem.Items.Armor;
using InventorySystem.Items.Firearms;
using InventorySystem.Items.Firearms.Modules;
using InventorySystem.Items.Keycards;
using PlayerRoles;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Human
{
    /// <summary>
    /// How much an item is worth to a human bot right now (0 = leave it, 1 = must have), given
    /// its role and what it carries. Only items the bot can use count: a firearm when it has none
    /// (or a clearly better one for a fighting role), ammo for its own firearm, better armor,
    /// medical items up to the native category limit, keycards adding permissions it lacks and
    /// grenades for armed humans. Everything else is junk. Native item templates are looked up
    /// once per item type.
    /// </summary>
    internal static class LootValuation
    {
        private const float FirearmForUnarmedCombatRole = 0.95f;
        private const float FirearmForUnarmedCivilian = 0.8f;
        private const float BetterFirearm = 0.5f;
        private const float EmptyFirearmReplacement = 0.7f;
        private const int BetterFirearmTierGap = 2;

        private static readonly Dictionary<ItemType, ItemCategory> Categories = new();
        private static readonly Dictionary<ItemType, ItemType> FirearmAmmo = new();
        private static readonly Dictionary<ItemType, DoorPermissionFlags> KeycardPermissions = new();

        public static ItemCategory CategoryOf(ItemType type)
        {
            if (!Categories.TryGetValue(type, out var category))
            {
                category = InventoryItemLoader.TryGetItem<ItemBase>(type, out var template) ? template.Category : ItemCategory.None;
                Categories.Add(type, category);
            }

            return category;
        }

        /// <summary>Value of an item of <paramref name="type"/> to the bot; 0 when it has no use for it or no room for it.</summary>
        public static float Value(ItemType type, HumanInventory inventory, RoleTypeId role)
        {
            var category = CategoryOf(type);
            if (!inventory.HasRoomFor(category))
            {
                return category == ItemCategory.Firearm ? SwapFirearmValue(type, inventory, role) : 0f;
            }

            switch (category)
            {
                case ItemCategory.Firearm:
                    return FirearmValue(type, inventory, role);
                case ItemCategory.Ammo:
                    return AmmoValue(type, inventory);
                case ItemCategory.Armor:
                    return ArmorValue(type, inventory, role);
                case ItemCategory.Medical:
                    return MedicalValue(type, inventory);
                case ItemCategory.Keycard:
                    return KeycardValue(type, inventory, role);
                case ItemCategory.Grenade:
                    return (type is ItemType.GrenadeHE or ItemType.GrenadeFlash)
                           && (inventory.HasUsableFirearm || HumanInventory.IsCombatRole(role))
                           && inventory.GrenadeCount < 2
                        ? 0.3f
                        : 0f;
                default:
                    return 0f;
            }
        }

        /// <summary>
        /// Worth of a firearm that only fits after dropping the carried one (category limit 1): a
        /// replacement for a firearm that can no longer fire, or a clearly better one. The loot behavior drops the old firearm right before searching.
        /// </summary>
        public static float SwapFirearmValue(ItemType type, HumanInventory inventory, RoleTypeId role)
        {
            if (inventory.Firearm == null || FirearmTier(type) <= 0 || AmmoFor(type) == ItemType.None)
            {
                return 0f;
            }

            if (!inventory.HasUsableFirearm)
            {
                return EmptyFirearmReplacement;
            }

            return FirearmTier(type) >= FirearmTier(inventory.Firearm.ItemTypeId) + BetterFirearmTierGap ? BetterFirearm : 0f;
        }

        private static float FirearmValue(ItemType type, HumanInventory inventory, RoleTypeId role)
        {
            if (FirearmTier(type) <= 0)
            {
                return 0f;
            }

            if (inventory.Firearm == null || !inventory.HasUsableFirearm)
            {
                return HumanInventory.IsCombatRole(role) ? FirearmForUnarmedCombatRole : FirearmForUnarmedCivilian;
            }

            return 0f;
        }

        private static float AmmoValue(ItemType type, HumanInventory inventory)
        {
            if (inventory.Firearm == null || inventory.FirearmAmmoType != type || inventory.AmmoLimit <= 0 || inventory.ReserveAmmo >= inventory.AmmoLimit)
            {
                return 0f;
            }

            var fill = (float)inventory.ReserveAmmo / inventory.AmmoLimit;
            return fill >= 0.75f ? 0f : 0.1f + 0.7f * (1f - fill);
        }

        private static float ArmorValue(ItemType type, HumanInventory inventory, RoleTypeId role)
        {
            var tier = ArmorTier(type);
            if (tier <= 0)
            {
                return 0f;
            }

            if (inventory.Armor == null)
            {
                return HumanInventory.IsCombatRole(role) ? 0.6f : 0.45f;
            }

            return tier > ArmorTier(inventory.Armor.ItemTypeId) ? 0.35f : 0f;
        }

        private static float MedicalValue(ItemType type, HumanInventory inventory)
        {
            var value = type switch
            {
                ItemType.SCP500 => 0.75f,
                ItemType.Medkit => 0.6f,
                ItemType.Painkillers => 0.4f,
                ItemType.Adrenaline => 0.4f,
                _ => 0f,
            };

            if (value <= 0f)
            {
                return 0f;
            }

            value *= 1f - 0.25f * Mathf.Min(inventory.MedicalCount, 3);
            if (inventory.HealthFraction < 0.6f)
            {
                value += 0.2f;
            }

            return Mathf.Min(value, 1f);
        }

        private static float KeycardValue(ItemType type, HumanInventory inventory, RoleTypeId role)
        {
            // Single-use passes (SurfaceAccessPass) report no permissions without a door to check
            // against, so they value as junk here; zone access still counts them at the gates.
            if (!KeycardPermissions.TryGetValue(type, out var permissions))
            {
                permissions = InventoryItemLoader.TryGetItem<KeycardItem>(type, out var keycard) ? keycard.GetPermissions(null) : DoorPermissionFlags.None;
                KeycardPermissions.Add(type, permissions);
            }

            var added = permissions & ~inventory.KeycardPermissions;
            if (added == DoorPermissionFlags.None)
            {
                return 0f;
            }

            var armoryWeight = HumanInventory.IsCombatRole(role) ? 0.15f : 0.08f;
            var value = 0.3f;
            value += Has(added, DoorPermissionFlags.Checkpoints) ? 0.3f : 0f;
            value += Has(added, DoorPermissionFlags.ExitGates) ? 0.3f : 0f;
            value += Has(added, DoorPermissionFlags.ContainmentLevelOne) ? 0.08f : 0f;
            value += Has(added, DoorPermissionFlags.ContainmentLevelTwo) ? 0.08f : 0f;
            value += Has(added, DoorPermissionFlags.ContainmentLevelThree) ? 0.08f : 0f;
            value += Has(added, DoorPermissionFlags.ArmoryLevelOne) ? armoryWeight : 0f;
            value += Has(added, DoorPermissionFlags.ArmoryLevelTwo) ? armoryWeight : 0f;
            value += Has(added, DoorPermissionFlags.ArmoryLevelThree) ? armoryWeight : 0f;
            return Mathf.Min(value, 0.95f);
        }

        private static bool Has(DoorPermissionFlags flags, DoorPermissionFlags flag) => (flags & flag) == flag;

        /// <summary>Rough strength order of the standard firearms; 0 for weapons combat does not use.</summary>
        public static int FirearmTier(ItemType type)
        {
            return type switch
            {
                ItemType.GunCOM15 => 1,
                ItemType.GunCOM18 => 2,
                ItemType.GunCom45 => 3,
                ItemType.GunFSP9 => 3,
                ItemType.GunRevolver => 3,
                ItemType.GunCrossvec => 4,
                ItemType.GunA7 => 4,
                ItemType.GunShotgun => 4,
                ItemType.GunSCP127 => 4,
                ItemType.GunAK => 5,
                ItemType.GunE11SR => 5,
                ItemType.GunLogicer => 6,
                ItemType.GunFRMG0 => 6,
                _ => 0,
            };
        }

        private static int ArmorTier(ItemType type)
        {
            return type switch
            {
                ItemType.ArmorLight => 1,
                ItemType.ArmorCombat => 2,
                ItemType.ArmorHeavy => 3,
                _ => 0,
            };
        }

        private static ItemType AmmoFor(ItemType firearmType)
        {
            if (!FirearmAmmo.TryGetValue(firearmType, out var ammo))
            {
                ammo = InventoryItemLoader.TryGetItem<Firearm>(firearmType, out var template)
                       && template.TryGetModule<IPrimaryAmmoContainerModule>(out var primaryAmmo)
                    ? primaryAmmo.AmmoType
                    : ItemType.None;
                FirearmAmmo.Add(firearmType, ammo);
            }

            return ammo;
        }
    }
}
