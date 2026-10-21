using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Brain.Human
{
    /// <summary>
    /// Registers the human item and survival behaviors on a bot's brain. Each behavior scores 0
    /// unless the bot currently has a human role, so one registration covers runtime role changes.
    /// <list type="bullet">
    /// <item><see cref="FleeBehavior"/> (Survival, highest): civilians escape SCPs even while armed; unarmed humans escape dangerous enemies.</item>
    /// <item><see cref="HealBehavior"/> (Survival): use a medical item when hurt and no threat is around.</item>
    /// <item><see cref="LootBehavior"/> (Opportunity): pick up nearby items worth having for the role.</item>
    /// </list>
    /// </summary>
    internal static class HumanBehaviors
    {
        public static BotBrain AddTo(BotBrain brain, FpcBotPlayer bot)
        {
            var context = new HumanBotContext(bot);
            return brain
                .Add(new FleeBehavior(context))
                .Add(new HealBehavior(context))
                .Add(new LootBehavior(context));
        }
    }

    /// <summary>Per-bot state the human behaviors share: threat awareness and a per-frame inventory reading.</summary>
    internal sealed class HumanBotContext
    {
        private readonly HumanInventory inventory = new();
        private int inventoryFrame = -1;

        public HumanBotContext(FpcBotPlayer bot)
        {
            Bot = bot;
            Threats = bot.HumanThreats;
        }

        public FpcBotPlayer Bot { get; }

        public HumanThreatAwareness Threats { get; }

        /// <summary>True when the bot currently has a role these behaviors serve.</summary>
        public bool IsHuman
        {
            get
            {
                var role = Bot.BotHub.PlayerHub.roleManager.CurrentRole;
                return Bot.FpcRole != null && HumanInventory.IsHumanBot(role);
            }
        }

        /// <summary>The inventory read once per frame, however many behaviors ask.</summary>
        public HumanInventory Inventory
        {
            get
            {
                var frame = Time.frameCount;
                if (frame != inventoryFrame)
                {
                    inventoryFrame = frame;
                    inventory.Read(Bot.BotHub.PlayerHub);
                }

                return inventory;
            }
        }

        /// <summary>Forces a fresh inventory reading (after a pickup or use changed it this frame).</summary>
        public void InvalidateInventory()
        {
            inventoryFrame = -1;
        }
    }
}
