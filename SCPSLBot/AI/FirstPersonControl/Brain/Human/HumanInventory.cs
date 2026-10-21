using Interactables.Interobjects.DoorUtils;
using InventorySystem;
using InventorySystem.Configs;
using InventorySystem.Items;
using InventorySystem.Items.Armor;
using InventorySystem.Items.Firearms;
using InventorySystem.Items.Firearms.Modules;
using InventorySystem.Items.Keycards;
using PlayerRoles;
using PlayerStatsSystem;
using SCPSLBot.AI.FirstPersonControl.Combat;
using SCPSLBot.Api;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Human
{
    /// <summary>
    /// What a human bot carries, read once per decision from the native inventory: counts per
    /// category (as the native category limits count them), the firearm and its ammo, armor,
    /// medical items and the union of keycard permissions. Reused per bot; allocation-free.
    /// </summary>
    internal sealed class HumanInventory
    {
        private readonly int[] categoryCounts = new int[16];

        public ReferenceHub Hub { get; private set; }
        public int ItemCount { get; private set; }
        public Firearm Firearm { get; private set; }
        public bool HasUsableFirearm { get; private set; }
        public ItemType FirearmAmmoType { get; private set; } = ItemType.None;
        public int ReserveAmmo { get; private set; }
        public int AmmoLimit { get; private set; }
        public BodyArmor Armor { get; private set; }
        public int MedicalCount { get; private set; }
        public int GrenadeCount { get; private set; }
        public DoorPermissionFlags KeycardPermissions { get; private set; }
        public float HealthFraction { get; private set; } = 1f;

        public int CategoryCount(ItemCategory category) => categoryCounts[(int)category];

        /// <summary>True when one more item of <paramref name="category"/> fits (8 slots, native category limit).</summary>
        public bool HasRoomFor(ItemCategory category)
        {
            if (category == ItemCategory.Ammo)
            {
                return true;
            }

            if (category == ItemCategory.Armor)
            {
                // Armor swaps with the worn one; a full inventory only refuses a first armor.
                return Armor != null || ItemCount < 8;
            }

            if (ItemCount >= 8)
            {
                return false;
            }

            if (category == ItemCategory.None)
            {
                return true;
            }

            var limit = Mathf.Abs(InventoryLimits.GetCategoryLimit(category, Hub));
            return categoryCounts[(int)category] < limit;
        }

        public void Read(ReferenceHub hub)
        {
            Hub = hub;
            System.Array.Clear(categoryCounts, 0, categoryCounts.Length);
            ItemCount = 0;
            Firearm = null;
            HasUsableFirearm = false;
            FirearmAmmoType = ItemType.None;
            ReserveAmmo = 0;
            AmmoLimit = 0;
            Armor = null;
            MedicalCount = 0;
            GrenadeCount = 0;
            KeycardPermissions = DoorPermissionFlags.None;

            var inventory = hub.inventory;
            foreach (var item in inventory.UserInventory.Items.Values)
            {
                ItemCount++;
                if (item.CountsTowardsCategoryLimit && (int)item.Category < categoryCounts.Length)
                {
                    categoryCounts[(int)item.Category]++;
                }

                switch (item)
                {
                    case Firearm firearm:
                        // Prefer a firearm that can still fire.
                        if (Firearm == null || (!HasUsableFirearm && HumanWeaponCombatStrategy.HasAmmo(firearm)))
                        {
                            Firearm = firearm;
                            HasUsableFirearm = HumanWeaponCombatStrategy.HasAmmo(firearm);
                        }

                        break;
                    case BodyArmor armor:
                        Armor = armor;
                        break;
                    case KeycardItem keycard:
                        KeycardPermissions |= keycard.GetPermissions(null);
                        break;
                }

                if (item.Category == ItemCategory.Medical)
                {
                    MedicalCount++;
                }
                else if (item.Category == ItemCategory.Grenade)
                {
                    GrenadeCount++;
                }
            }

            if (Firearm != null && Firearm.TryGetModule<IPrimaryAmmoContainerModule>(out var primaryAmmo))
            {
                FirearmAmmoType = primaryAmmo.AmmoType;
                ReserveAmmo = inventory.GetCurAmmo(primaryAmmo.AmmoType);
                AmmoLimit = InventoryLimits.GetAmmoLimit(primaryAmmo.AmmoType, hub);
            }

            var health = hub.playerStats.GetModule<HealthStat>();
            HealthFraction = health.MaxValue > 0f ? Mathf.Clamp01(health.CurValue / health.MaxValue) : 1f;
        }

        /// <summary>Humans the item behaviors serve: every human role but Tutorial (left to its native behavior).</summary>
        public static bool IsHumanBot(PlayerRoleBase role)
        {
            return role is HumanRole && role.RoleTypeId != RoleTypeId.Tutorial;
        }

        public static bool IsCivilian(RoleTypeId role) => role is RoleTypeId.ClassD or RoleTypeId.Scientist;

        /// <summary>Roles that fight by default: guards, MTF and Chaos.</summary>
        public static bool IsCombatRole(RoleTypeId role)
        {
            return role is RoleTypeId.FacilityGuard
                or RoleTypeId.NtfPrivate or RoleTypeId.NtfSergeant or RoleTypeId.NtfSpecialist or RoleTypeId.NtfCaptain
                or RoleTypeId.ChaosConscript or RoleTypeId.ChaosRifleman or RoleTypeId.ChaosMarauder or RoleTypeId.ChaosRepressor;
        }

        /// <summary>
        /// False when combat would not engage: outside Standard warmup a human needs a firearm
        /// that can still fire. Such humans keep away from threats instead.
        /// </summary>
        public bool CanFight => HasUsableFirearm || BotServerPolicy.IsStandardWarmup;
    }
}
