using InventorySystem.Items.Firearms;
using InventorySystem.Items.Jailbird;
using InventorySystem.Items.MicroHID;
using InventorySystem.Items.ThrowableProjectiles;
using PlayerRoles;
using SCPSLBot.AI.FirstPersonControl.Combat;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Human
{
    /// <summary>
    /// The nearest dangerous hostile a human bot noticed: an SCP or an armed enemy human it can
    /// see (or hear close by), with a short memory after it went out of view. Built on the shared
    /// <see cref="CombatWorldSnapshot"/> and refreshed at most four times a second however many
    /// behaviors ask; at most a few line casts per refresh.
    /// </summary>
    internal sealed class HumanThreatAwareness
    {
        private const float RefreshSeconds = 0.25f;
        private const float SightMeters = 30f;
        private const float HearMeters = 6f;
        private const float MemorySeconds = 6f;
        private const int MaxSightChecks = 4;

        private static readonly int VisionMask = LayerMask.GetMask("Default", "Door", "InteractableNoPlayerCollision", "Glass");

        private readonly FpcBotPlayer bot;
        private float nextRefreshAt;

        public HumanThreatAwareness(FpcBotPlayer bot)
        {
            this.bot = bot;
        }

        /// <summary>A threat is in view (or within earshot) right now.</summary>
        public bool SeesThreat { get; private set; }

        public float LastSeenAt { get; private set; } = float.NegativeInfinity;

        /// <summary>Where the threat was last noticed.</summary>
        public Vector3 ThreatPosition { get; private set; }
        public bool ThreatIsScp { get; private set; }
        public ReferenceHub ThreatHub { get; private set; }

        public bool HasRecentThreat => Time.time - LastSeenAt <= MemorySeconds;

        public float SecondsSinceThreat => Time.time - LastSeenAt;

        public void Refresh()
        {
            var now = Time.time;
            if (now < nextRefreshAt)
            {
                return;
            }

            nextRefreshAt = now + RefreshSeconds;
            SeesThreat = false;

            var botHub = bot.BotHub.PlayerHub;
            var origin = bot.CameraPosition;
            var position = bot.PlayerPosition;
            var checks = 0;
            var nearestSqr = float.PositiveInfinity;
            var nearest = Vector3.zero;
            var nearestIsScp = false;
            ReferenceHub nearestHub = null;
            var preferScps = HumanInventory.IsCivilian(botHub.roleManager.CurrentRole.RoleTypeId);

            var snapshot = CombatWorldSnapshot.Get(now);
            // Armed civilians must not lose their SCP escape priority to a nearer enemy human.
            for (var pass = 0; pass < (preferScps ? 2 : 1); pass++)
            {
                foreach (var candidate in snapshot)
                {
                    if (preferScps && (candidate.Team == Team.SCPs) != (pass == 0))
                    {
                        continue;
                    }
                    if (candidate.Hub == botHub || candidate.Role == RoleTypeId.Scp079)
                    {
                        continue;
                    }

                    var sqrDistance = (candidate.Position - position).sqrMagnitude;
                    if (sqrDistance > SightMeters * SightMeters || sqrDistance >= nearestSqr)
                    {
                        continue;
                    }

                    if (!IsDangerous(candidate) || !CombatTargetSelector.AreHostile(botHub, candidate.Hub))
                    {
                        continue;
                    }

                    if (sqrDistance > HearMeters * HearMeters)
                    {
                        if (checks >= MaxSightChecks)
                        {
                            continue;
                        }

                        checks++;
                        if (Physics.Linecast(origin, candidate.Position + Vector3.up * 0.8f, VisionMask, QueryTriggerInteraction.Ignore))
                        {
                            continue;
                        }
                    }

                    nearestSqr = sqrDistance;
                    nearest = candidate.Position;
                    nearestIsScp = candidate.Team == Team.SCPs;
                    nearestHub = candidate.Hub;
                }

                if (nearestSqr < float.PositiveInfinity)
                {
                    break;
                }
            }

            if (nearestSqr < float.PositiveInfinity)
            {
                SeesThreat = true;
                LastSeenAt = now;
                ThreatPosition = nearest;
                ThreatIsScp = nearestIsScp;
                ThreatHub = nearestHub;
            }
        }

        /// <summary>SCPs, and humans holding or carrying something that kills.</summary>
        private static bool IsDangerous(in CombatWorldEntry candidate)
        {
            if (candidate.Team == Team.SCPs)
            {
                return true;
            }

            var inventory = candidate.Hub.inventory;
            if (inventory == null)
            {
                return false;
            }

            if (inventory.CurInstance is Firearm or MicroHIDItem or JailbirdItem or ThrowableItem)
            {
                return true;
            }

            foreach (var item in inventory.UserInventory.Items.Values)
            {
                if (item is Firearm)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
