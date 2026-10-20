using InventorySystem;
using InventorySystem.Items.Firearms.Ammo;
using InventorySystem.Items.Pickups;
using InventorySystem.Searching;
using MEC;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item
{
    internal enum PickupProgress
    {
        Approaching,
        Searching,
        PickedUp,
        /// <summary>Gone, taken by someone else, refused by the native checks (inventory full, category or ammo limit) or a plugin.</summary>
        Rejected,
    }

    /// <summary>
    /// Picks one pickup up the way a player does: walk within reach, look at it, stand still for
    /// the native search time (<see cref="ItemPickupBase.SearchTimeForPlayer"/>) while the native
    /// completor keeps validating, then complete through that completor so inventory limits, armor
    /// swaps and the LabAPI searching/picking-up events all apply. The pickup is marked in use for
    /// the duration, as <see cref="ItemPickupBase.ServerValidateRequest"/> does for a player.
    /// </summary>
    internal sealed class BotPickupSession
    {
        private const float StandHorizontalMeters = 1.4f;
        private const float StandSqrMeters = 2.2f * 2.2f;
        private const float StallSeconds = 1.5f;
        private const float ApproachTimeoutSeconds = 25f;
        private const float LookToleranceDegrees = 12f;
        private const float ClaimGraceSeconds = 1.5f;

        private readonly FpcBotPlayer bot;
        private ItemPickupBase pickup;
        private ushort serial;
        private ItemType itemType;
        private ISearchCompletor completor;
        private float finishAt;
        private float startedAt;
        private float bestSqrDistance;
        private float improvedAt;
        private ushort ammoBefore;
        private bool claimed;
        private int claimToken;

        public BotPickupSession(FpcBotPlayer bot)
        {
            this.bot = bot;
        }

        public ItemPickupBase Target => pickup;

        /// <summary>Serial of the pickup last passed to <see cref="Begin"/>; kept after the session ends.</summary>
        public ushort Serial => serial;

        public bool IsActive => !ReferenceEquals(pickup, null);

        public bool IsSearching => completor != null;

        public void Begin(ItemPickupBase target)
        {
            if (ReferenceEquals(target, pickup) && IsActive)
            {
                return;
            }

            Cancel();
            pickup = target;
            serial = target != null ? target.Info.Serial : (ushort)0;
            itemType = target != null ? target.Info.ItemId : ItemType.None;
            startedAt = Time.time;
            bestSqrDistance = float.PositiveInfinity;
            improvedAt = Time.time;
        }

        /// <summary>Stops walking or searching and gives the pickup back to everyone else.</summary>
        public void Cancel()
        {
            ReleaseClaim();
            completor = null;
            pickup = null;
        }

        public PickupProgress Tick()
        {
            if (!IsActive)
            {
                return PickupProgress.Rejected;
            }

            if (pickup == null)
            {
                // Destroyed: someone else (or a completed search) took it.
                Cancel();
                return PickupProgress.Rejected;
            }

            return completor == null ? Approach() : Search();
        }

        private PickupProgress Approach()
        {
            var hub = bot.BotHub.PlayerHub;
            var now = Time.time;
            var position = pickup.Position;
            var body = hub.transform.position;
            var sqrDistance = (position - body).sqrMagnitude;
            var reachSqr = hub.searchCoordinator.ServerMaxRayDistanceSqr;

            if (sqrDistance < bestSqrDistance - 0.04f)
            {
                bestSqrDistance = sqrDistance;
                improvedAt = now;
            }

            var dx = position.x - body.x;
            var dz = position.z - body.z;
            var close = dx * dx + dz * dz <= StandHorizontalMeters * StandHorizontalMeters && sqrDistance <= StandSqrMeters;
            // A pickup on clutter or a shelf the path cannot reach: searching works from anywhere in reach.
            var stalledInReach = sqrDistance <= reachSqr * 0.8f && now - improvedAt > StallSeconds;
            if (!close && !stalledInReach)
            {
                if (now - startedAt > ApproachTimeoutSeconds)
                {
                    Cancel();
                    return PickupProgress.Rejected;
                }

                bot.MoveToPosition(position);
                if (sqrDistance < 16f)
                {
                    bot.LookToPosition(position);
                }

                return PickupProgress.Approaching;
            }

            bot.Locomotion.Stop();
            var direction = position - bot.CameraPosition;
            if (direction.sqrMagnitude > 0.0001f && Vector3.Angle(bot.CameraForward, direction) > LookToleranceDegrees)
            {
                bot.LookToPosition(position);
                if (now - startedAt <= ApproachTimeoutSeconds)
                {
                    return PickupProgress.Approaching;
                }
            }

            if (!pickup.CanSearch)
            {
                // Locked, or another player is searching it right now.
                Cancel();
                return PickupProgress.Rejected;
            }

            var searchCompletor = pickup.GetSearchCompletor(hub.searchCoordinator, reachSqr);
            if (searchCompletor == null || !searchCompletor.ValidateStart())
            {
                Cancel();
                return PickupProgress.Rejected;
            }

            completor = searchCompletor;
            ammoBefore = pickup is AmmoPickup ? hub.inventory.GetCurAmmo(itemType) : (ushort)0;
            var searchSeconds = pickup.SearchTimeForPlayer(hub);
            finishAt = now + searchSeconds;
            Claim(searchSeconds);
            return PickupProgress.Searching;
        }

        private PickupProgress Search()
        {
            bot.Locomotion.Stop();
            bot.LookToPosition(pickup.Position);
            if (!completor.ValidateUpdate())
            {
                Cancel();
                return PickupProgress.Rejected;
            }

            if (Time.time < finishAt)
            {
                return PickupProgress.Searching;
            }

            var hub = bot.BotHub.PlayerHub;
            var isAmmo = pickup is AmmoPickup;
            var finished = completor;
            completor = null;
            // The completor resets the in-use flag itself when a plugin vetoes the pickup.
            claimed = false;
            pickup = null;
            finished.Complete();

            var gained = isAmmo
                ? hub.inventory.GetCurAmmo(itemType) > ammoBefore
                : hub.inventory.UserInventory.Items.ContainsKey(serial);
            return gained ? PickupProgress.PickedUp : PickupProgress.Rejected;
        }

        private void Claim(float searchSeconds)
        {
            var info = pickup.Info;
            info.InUse = true;
            pickup.NetworkInfo = info;
            claimed = true;

            // A bot that is removed mid-search never reaches Cancel; never leave the pickup
            // stuck in use for everyone else.
            var token = ++claimToken;
            var claimedPickup = pickup;
            Timing.CallDelayed(searchSeconds + ClaimGraceSeconds, () =>
            {
                if (token == claimToken && claimed && ReferenceEquals(pickup, claimedPickup))
                {
                    ReleaseClaim();
                }
            });
        }

        private void ReleaseClaim()
        {
            if (!claimed)
            {
                return;
            }

            claimed = false;
            claimToken++;
            if (pickup != null)
            {
                var info = pickup.Info;
                info.InUse = false;
                pickup.NetworkInfo = info;
            }
        }
    }

    /// <summary>
    /// Pickups a bot recently failed to take (rejected by limits or a plugin, unreachable), shared
    /// by its objective and its loot behavior so neither retries them in a loop. Keyed by item
    /// serial; forgotten when a new map is generated.
    /// </summary>
    internal sealed class BotPickupMemory
    {
        private static readonly ConditionalWeakTable<FpcBotPlayer, BotPickupMemory> Table = new();

        private readonly Dictionary<ushort, float> blockedUntil = new();
        private int generation = int.MinValue;

        public static BotPickupMemory For(FpcBotPlayer bot) => Table.GetValue(bot, _ => new BotPickupMemory());

        public bool IsBlocked(ItemPickupBase pickup)
        {
            if (pickup == null)
            {
                return true;
            }

            EnsureGeneration();
            return blockedUntil.TryGetValue(pickup.Info.Serial, out var until) && Time.time < until;
        }

        public void Block(ushort serial, float seconds)
        {
            if (serial == 0)
            {
                return;
            }

            EnsureGeneration();
            blockedUntil[serial] = Time.time + seconds;
        }

        private void EnsureGeneration()
        {
            var current = Navigation.NavigationSystem.Instance.MapGeneration;
            if (current != generation)
            {
                generation = current;
                blockedUntil.Clear();
            }
        }
    }
}
