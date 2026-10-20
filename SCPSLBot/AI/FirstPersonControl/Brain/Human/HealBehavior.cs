using InventorySystem.Items.Usables;
using PlayerStatsSystem;
using SCPSLBot.AI.FirstPersonControl.Mind.Item;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Human
{
    /// <summary>
    /// Uses a medical item when hurt and no threat has been noticed for a few seconds: below half
    /// health the best of SCP-500 (kept for real emergencies), medkit, painkillers or adrenaline;
    /// below 70% only the slow ones (painkillers, adrenaline). The item is equipped under the item
    /// lease and used through the native usable-item path (<see cref="UsableItemsController.ServerEmulateMessage"/>,
    /// what the dummy "UsableItem->Start" action calls), so cooldowns, speed and LabAPI events
    /// apply. The bot stands still until the item is used up, cancels if a threat shows up, and
    /// puts its previous item (usually the firearm) back.
    /// </summary>
    internal sealed class HealBehavior : IBotBehavior
    {
        private const float HurtFraction = 0.5f;
        private const float BruisedFraction = 0.7f;
        private const float EmergencyFraction = 0.35f;
        private const float QuietSeconds = 4f;
        private const float EquipSettleSeconds = 0.5f;
        private const float StartTimeoutSeconds = 1.5f;
        private const float LeaseMarginSeconds = 3f;
        private const float FailedItemSeconds = 10f;
        private const float LeaseWaitSeconds = 2f;
        private const float EnoughAhp = 20f;

        private enum Stage
        {
            Equip,
            Settle,
            Using,
        }

        private readonly HumanBotContext context;
        private readonly FpcBotPlayer bot;
        private readonly BotHeldItemSwap swap;

        private UsableItem item;
        private ushort itemSerial;
        private Stage stage;
        private float stageStartedAt;
        private bool started;
        private ItemType failedType = ItemType.None;
        private float failedUntil;

        public HealBehavior(HumanBotContext context)
        {
            this.context = context;
            bot = context.Bot;
            swap = new BotHeldItemSwap(bot);
        }

        public string Name => "heal";

        public float Evaluate()
        {
            if (!context.IsHuman)
            {
                return 0f;
            }

            if (item != null && stage == Stage.Using && started && item.IsUsing)
            {
                // Finishing a use already under way beats starting anything else.
                return BehaviorPriority.Survival + 0.05f;
            }

            context.Threats.Refresh();
            if (context.Threats.SeesThreat || context.Threats.SecondsSinceThreat < QuietSeconds)
            {
                return 0f;
            }

            var health = context.Inventory.HealthFraction;
            if (health >= BruisedFraction || SelectItem(health) == null)
            {
                return 0f;
            }

            return BehaviorPriority.Survival + 0.05f * (1f - health);
        }

        public void OnEnter()
        {
            item = SelectItem(context.Inventory.HealthFraction);
            itemSerial = item != null ? item.ItemSerial : (ushort)0;
            stage = Stage.Equip;
            stageStartedAt = Time.time;
            started = false;
        }

        public void OnExit()
        {
            Finish();
        }

        public bool Tick()
        {
            var hub = bot.BotHub.PlayerHub;
            if (item == null || !hub.inventory.UserInventory.Items.ContainsKey(itemSerial))
            {
                // Used up (or gone): done.
                return Finish();
            }

            bot.Locomotion.Stop();
            context.Threats.Refresh();
            if (context.Threats.SeesThreat)
            {
                return Finish();
            }

            var now = Time.time;
            switch (stage)
            {
                case Stage.Equip:
                    if (swap.Equip(item, item.UseTime + LeaseMarginSeconds + EquipSettleSeconds))
                    {
                        Advance(Stage.Settle, now);
                    }
                    else if (now - stageStartedAt > LeaseWaitSeconds)
                    {
                        return Finish();
                    }

                    return true;

                case Stage.Settle:
                    if (now - stageStartedAt < EquipSettleSeconds)
                    {
                        return true;
                    }

                    UsableItemsController.ServerEmulateMessage(itemSerial, StatusMessage.StatusType.Start);
                    Advance(Stage.Using, now);
                    return true;

                default:
                    if (item.IsUsing)
                    {
                        started = true;
                        return true;
                    }

                    if (!started && now - stageStartedAt > StartTimeoutSeconds)
                    {
                        // Refused: on cooldown, blocked, or vetoed by a plugin.
                        failedType = item.ItemTypeId;
                        failedUntil = now + FailedItemSeconds;
                        return Finish();
                    }

                    // The use ended without consuming the item (cancelled natively): done.
                    return started ? Finish() : true;
            }
        }

        private void Advance(Stage next, float now)
        {
            stage = next;
            stageStartedAt = now;
        }

        private bool Finish()
        {
            if (item != null && started && item.IsUsing)
            {
                // A used item cannot be holstered: cancel first. Within the native cancellable
                // window this stops the use; past it the use completes and the item is consumed.
                UsableItemsController.ServerEmulateMessage(itemSerial, StatusMessage.StatusType.Cancel);
            }

            swap.Restore();
            item = null;
            itemSerial = 0;
            started = false;
            context.InvalidateInventory();
            return false;
        }

        private UsableItem SelectItem(float health)
        {
            var hub = bot.BotHub.PlayerHub;
            var handler = UsableItemsController.GetHandler(hub);
            var regenerating = handler.ActiveRegenerations.Count > 0;
            var ahp = hub.playerStats.GetModule<AhpStat>().CurValue;
            var now = Time.time;

            UsableItem best = null;
            var bestRank = 0;
            foreach (var candidate in hub.inventory.UserInventory.Items.Values)
            {
                if (candidate is not UsableItem usable || !usable.AllowEquip)
                {
                    continue;
                }

                var type = usable.ItemTypeId;
                if (type == failedType && now < failedUntil)
                {
                    continue;
                }

                var rank = Rank(type, health, regenerating, ahp);
                if (rank <= bestRank || UsableItemsController.GetCooldown(usable.ItemSerial, usable, handler) > 0f)
                {
                    continue;
                }

                best = usable;
                bestRank = rank;
            }

            return best;
        }

        /// <summary>Higher is better; 0 means not now.</summary>
        private static int Rank(ItemType type, float health, bool regenerating, float ahp)
        {
            var hurt = health < HurtFraction;
            return type switch
            {
                ItemType.Medkit when hurt => 5,
                // SCP-500 heals everything; keep it for an emergency without a medkit.
                ItemType.SCP500 when health < EmergencyFraction => 4,
                ItemType.SCP500 when hurt => 1,
                ItemType.Painkillers when !regenerating => 3,
                ItemType.Adrenaline when ahp < EnoughAhp => 2,
                _ => 0,
            };
        }
    }
}
