using InventorySystem.Items;
using InventorySystem.Items.Pickups;
using MapGeneration.Distributors;
using SCPSLBot.AI.FirstPersonControl.Mind.Item;
using SCPSLBot.AI.FirstPersonControl.Movement;
using SCPSLBot.Infrastructure;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Human
{
    /// <summary>
    /// Picks up nearby items worth having for the bot's role (<see cref="LootValuation"/>) and
    /// opens nearby locker chambers that may hold such items, using a carried keycard when the
    /// chamber needs one. Once a second it filters the shared pickup and locker snapshots by
    /// distance, values the candidates against the native inventory limits and checks line of
    /// sight for the best few only. Pickups are taken like a player (<see cref="BotPickupSession"/>);
    /// rejected or unreachable targets are skipped for a while. Scores in the Opportunity band, so
    /// it interrupts the objective only for something close and useful.
    /// </summary>
    internal sealed class LootBehavior : IBotBehavior
    {
        private const float ScanSeconds = 1f;
        private const float ScanMeters = 14f;
        private const float MaxHeightMeters = 2.5f;
        private const float NoticeMeters = 3f;
        private const float MinValue = 0.25f;
        private const float DistancePenalty = 0.3f;
        private const int MaxSightChecks = 4;
        private const int RankedCandidates = 4;
        private const float ChamberValueFactor = 0.6f;
        private const float ChamberMeters = 9f;
        private const float RejectedPickupSeconds = 120f;
        private const float DroppedFirearmSeconds = 300f;
        private const float ChamberRetrySeconds = 300f;
        private const float BaseTimeoutSeconds = 12f;
        private const float TimeoutMetersPerSecond = 1.5f;
        private const float SwapDropMeters = 2.2f;
        private const float StandOffMeters = 1f;
        private const float ReachChamberMeters = 1.3f;

        private static readonly int VisionMask = LayerMask.GetMask("Default", "Door", "Glass");

        private readonly HumanBotContext context;
        private readonly FpcBotPlayer bot;
        private readonly BotPickupSession session;
        private readonly BotLockerOpener opener;
        private readonly BotPickupMemory pickupMemory;
        private readonly Dictionary<LockerChamber, float> chamberRetryAt = new(ManagedReferenceComparer<LockerChamber>.Instance);
        private readonly List<(ItemPickupBase Pickup, float Value, float Score)> ranked = new(RankedCandidates + 1);

        private ItemPickupBase targetPickup;
        private Locker targetLocker;
        private int targetChamber = -1;
        private ushort targetSerial;
        private float targetScore;
        private bool workingChamber;
        private bool swapFirearm;
        private float nextScanAt;
        private float deadline;
        private int chamberGeneration = int.MinValue;

        public LootBehavior(HumanBotContext context)
        {
            this.context = context;
            bot = context.Bot;
            session = new BotPickupSession(bot);
            opener = new BotLockerOpener(bot);
            pickupMemory = BotPickupMemory.For(bot);
        }

        public string Name => "loot";

        private bool HasTarget => targetPickup != null || targetLocker != null;

        public float Evaluate()
        {
            if (!context.IsHuman)
            {
                ClearTarget();
                return 0f;
            }

            context.Threats.Refresh();
            var inventory = context.Inventory;
            if (context.Threats.HasRecentThreat && !inventory.CanFight)
            {
                // Keeping away from the threat comes first.
                return 0f;
            }

            if (HasTarget && !IsTargetStillWorthIt(inventory))
            {
                ClearTarget();
            }

            var now = Time.time;
            if (!HasTarget && now >= nextScanAt)
            {
                nextScanAt = now + ScanSeconds;
                Scan(inventory, now);
            }

            return HasTarget ? BehaviorPriority.Opportunity + 0.09f * Mathf.Clamp01(targetScore) : 0f;
        }

        public void OnEnter()
        {
            bot.Locomotion.DiscardAbandonRequest();
            var target = targetPickup != null ? targetPickup.Position : ChamberPosition();
            deadline = Time.time + BaseTimeoutSeconds + Vector3.Distance(bot.PlayerPosition, target) / TimeoutMetersPerSecond;
        }

        public void OnExit()
        {
            session.Cancel();
            opener.Finish();
        }

        public bool Tick()
        {
            var now = Time.time;
            if (bot.Locomotion.ConsumeAbandonRequest() || now > deadline)
            {
                GiveUpTarget();
                return false;
            }

            if (targetPickup != null)
            {
                return TickPickup();
            }

            if (targetLocker != null)
            {
                return TickChamber();
            }

            return false;
        }

        private bool TickPickup()
        {
            if (targetPickup == null)
            {
                ClearTarget();
                return false;
            }

            if (swapFirearm && (targetPickup.Position - bot.PlayerPosition).sqrMagnitude <= SwapDropMeters * SwapDropMeters)
            {
                DropCarriedFirearm();
            }

            session.Begin(targetPickup);
            switch (session.Tick())
            {
                case PickupProgress.PickedUp:
                    ClearTarget();
                    context.InvalidateInventory();
                    nextScanAt = 0f;
                    return false;
                case PickupProgress.Rejected:
                    GiveUpTarget();
                    return false;
                default:
                    return true;
            }
        }

        private bool TickChamber()
        {
            if (targetLocker == null || targetChamber < 0 || targetChamber >= targetLocker.Chambers.Length)
            {
                ClearTarget();
                return false;
            }

            var chamber = targetLocker.Chambers[targetChamber];
            var toChamber = chamber.transform.position - bot.PlayerPosition;
            toChamber.y = 0f;
            var toStand = StandPoint(chamber.transform) - bot.PlayerPosition;
            toStand.y = 0f;
            // The exact stand point may sit off the navmesh; close to the chamber is good enough.
            if (!workingChamber && toChamber.magnitude > ReachChamberMeters && toStand.magnitude > 0.6f)
            {
                bot.MoveToPosition(StandPoint(chamber.transform));
                return true;
            }

            workingChamber = true;
            switch (opener.Tick(targetLocker, targetChamber))
            {
                case LockerOpenProgress.Working:
                    return true;
                case LockerOpenProgress.Opened:
                    chamberRetryAt[chamber] = Time.time + ChamberRetrySeconds;
                    ClearTarget();
                    // Whatever lies inside is an ordinary pickup now.
                    nextScanAt = 0f;
                    return false;
                default:
                    GiveUpTarget();
                    return false;
            }
        }

        private void DropCarriedFirearm()
        {
            swapFirearm = false;
            var carried = context.Inventory.Firearm;
            if (carried == null || bot.ItemLease.IsHeld)
            {
                return;
            }

            var serial = carried.ItemSerial;
            carried.ServerDropItem(true);
            // Never swap back to the firearm just dropped.
            pickupMemory.Block(serial, DroppedFirearmSeconds);
            context.InvalidateInventory();
        }

        private void Scan(HumanInventory inventory, float now)
        {
            var role = bot.BotHub.PlayerHub.roleManager.CurrentRole.RoleTypeId;
            var position = bot.PlayerPosition;
            ranked.Clear();

            foreach (var entry in LootWorld.GetPickups(now))
            {
                var offset = entry.Position - position;
                if (Mathf.Abs(offset.y) > MaxHeightMeters)
                {
                    continue;
                }

                var sqrDistance = offset.x * offset.x + offset.z * offset.z;
                if (sqrDistance > ScanMeters * ScanMeters)
                {
                    continue;
                }

                var value = LootValuation.Value(entry.Type, inventory, role);
                if (value < MinValue || entry.Pickup == null || !entry.Pickup.CanSearch || pickupMemory.IsBlocked(entry.Pickup))
                {
                    continue;
                }

                var score = value - Mathf.Sqrt(sqrDistance) / ScanMeters * DistancePenalty;
                InsertRanked(entry.Pickup, value, score);
            }

            var checks = 0;
            foreach (var (pickup, _, score) in ranked)
            {
                if (checks >= MaxSightChecks)
                {
                    break;
                }

                if (!CanNotice(pickup.Position, ref checks))
                {
                    continue;
                }

                targetPickup = pickup;
                targetSerial = pickup.Info.Serial;
                targetScore = score;
                swapFirearm = LootValuation.CategoryOf(pickup.Info.ItemId) == ItemCategory.Firearm && inventory.Firearm != null;
                return;
            }

            ScanChambers(inventory, role, position, now, ref checks);
        }

        private void ScanChambers(HumanInventory inventory, PlayerRoles.RoleTypeId role, Vector3 position, float now, ref int checks)
        {
            var generation = Navigation.NavigationSystem.Instance.MapGeneration;
            if (generation != chamberGeneration)
            {
                chamberGeneration = generation;
                chamberRetryAt.Clear();
            }

            Locker bestLocker = null;
            var bestChamber = -1;
            var bestScore = MinValue;
            var hub = bot.BotHub.PlayerHub;
            foreach (var locker in LootWorld.GetLockers(now))
            {
                if (locker == null)
                {
                    continue;
                }

                var lockerPosition = locker.transform.position;
                var offset = lockerPosition - position;
                if (Mathf.Abs(offset.y) > MaxHeightMeters || offset.x * offset.x + offset.z * offset.z > ChamberMeters * ChamberMeters)
                {
                    continue;
                }

                var chambers = locker.Chambers;
                for (var index = 0; index < chambers.Length; index++)
                {
                    var chamber = chambers[index];
                    if (chamber == null || chamber.IsOpen || !chamber.CanInteract)
                    {
                        continue;
                    }

                    if (chamberRetryAt.TryGetValue(chamber, out var retryAt) && now < retryAt)
                    {
                        continue;
                    }

                    var value = 0f;
                    foreach (var acceptable in chamber.AcceptableItems)
                    {
                        value = Mathf.Max(value, LootValuation.Value(acceptable, inventory, role));
                    }

                    // Nobody knows what a closed chamber holds.
                    value *= ChamberValueFactor;
                    var score = value - Vector3.Distance(chamber.transform.position, position) / ChamberMeters * DistancePenalty;
                    if (score <= bestScore || LockerAccess.Evaluate(hub, chamber, out _) == DoorAccessKind.Denied)
                    {
                        continue;
                    }

                    bestScore = score;
                    bestLocker = locker;
                    bestChamber = index;
                }
            }

            if (bestLocker != null && checks < MaxSightChecks && CanNotice(StandPoint(bestLocker.Chambers[bestChamber].transform), ref checks))
            {
                targetLocker = bestLocker;
                targetChamber = bestChamber;
                targetScore = bestScore;
            }
        }

        private bool CanNotice(Vector3 target, ref int checks)
        {
            var origin = bot.CameraPosition;
            if ((target - origin).sqrMagnitude <= NoticeMeters * NoticeMeters)
            {
                return true;
            }

            checks++;
            return !Physics.Linecast(origin, target + Vector3.up * 0.1f, VisionMask, QueryTriggerInteraction.Ignore);
        }

        private void InsertRanked(ItemPickupBase pickup, float value, float score)
        {
            var index = ranked.Count;
            while (index > 0 && ranked[index - 1].Score < score)
            {
                index--;
            }

            if (index >= RankedCandidates)
            {
                return;
            }

            ranked.Insert(index, (pickup, value, score));
            if (ranked.Count > RankedCandidates)
            {
                ranked.RemoveAt(ranked.Count - 1);
            }
        }

        private bool IsTargetStillWorthIt(HumanInventory inventory)
        {
            if (targetLocker != null)
            {
                return targetChamber >= 0
                       && targetChamber < targetLocker.Chambers.Length
                       && !targetLocker.Chambers[targetChamber].IsOpen;
            }

            if (targetPickup == null || pickupMemory.IsBlocked(targetPickup))
            {
                return false;
            }

            if (session.IsSearching)
            {
                return true;
            }

            if (!targetPickup.CanSearch)
            {
                return false;
            }

            var role = bot.BotHub.PlayerHub.roleManager.CurrentRole.RoleTypeId;
            return LootValuation.Value(targetPickup.Info.ItemId, inventory, role) >= MinValue;
        }

        /// <summary>In front of a chamber: of the two sides along its axis, the open one is the side the bot comes from.</summary>
        private Vector3 StandPoint(Transform chamber)
        {
            var position = chamber.position;
            var axis = Vector3.ProjectOnPlane(chamber.forward, Vector3.up).normalized * StandOffMeters;
            var front = position - axis;
            var back = position + axis;
            return (front - bot.PlayerPosition).sqrMagnitude <= (back - bot.PlayerPosition).sqrMagnitude ? front : back;
        }

        private Vector3 ChamberPosition()
        {
            return targetLocker != null && targetChamber >= 0 && targetChamber < targetLocker.Chambers.Length
                ? targetLocker.Chambers[targetChamber].transform.position
                : bot.PlayerPosition;
        }

        private void GiveUpTarget()
        {
            if (targetSerial != 0)
            {
                pickupMemory.Block(targetSerial, RejectedPickupSeconds);
            }

            if (targetLocker != null && targetChamber >= 0 && targetChamber < targetLocker.Chambers.Length)
            {
                chamberRetryAt[targetLocker.Chambers[targetChamber]] = Time.time + ChamberRetrySeconds;
            }

            ClearTarget();
        }

        private void ClearTarget()
        {
            session.Cancel();
            opener.Finish();
            targetPickup = null;
            targetSerial = 0;
            targetLocker = null;
            targetChamber = -1;
            targetScore = 0f;
            swapFirearm = false;
            workingChamber = false;
        }
    }
}
