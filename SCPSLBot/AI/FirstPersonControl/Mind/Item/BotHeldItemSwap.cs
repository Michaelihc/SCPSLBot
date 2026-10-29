using InventorySystem.Items;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item
{
    /// <summary>
    /// Temporarily holds another item (a keycard for a locker, a medkit) under the bot's
    /// <see cref="FpcBotPlayer.ItemLease"/> and puts the previously held item back afterwards,
    /// the same contract locomotion's door keycard swap follows.
    /// </summary>
    internal sealed class BotHeldItemSwap
    {
        private readonly FpcBotPlayer bot;
        private ushort previousSerial;
        private ushort equippedSerial;
        private bool active;

        public BotHeldItemSwap(FpcBotPlayer bot)
        {
            this.bot = bot;
        }

        public bool IsActive => active;

        /// <summary>Equips <paramref name="item"/>; false while someone else holds the lease or the game refuses the switch.</summary>
        public bool Equip(ItemBase item, float leaseSeconds)
        {
            var inventory = bot.BotHub.PlayerHub.inventory;
            if (item == null || inventory == null || !bot.ItemLease.Acquire(this, leaseSeconds))
            {
                return false;
            }

            if (!active)
            {
                previousSerial = inventory.CurItem.SerialNumber;
                active = true;
            }

            equippedSerial = item.ItemSerial;
            if (inventory.CurItem.SerialNumber != equippedSerial)
            {
                inventory.ServerSelectItem(equippedSerial);
            }

            return inventory.CurItem.SerialNumber == equippedSerial && inventory.CurInstance == item;
        }

        /// <summary>
        /// Puts the previous item back while our item is still in hand (or was used up), then
        /// releases the lease. Once the lease expired something else may have chosen what to hold.
        /// </summary>
        public void Restore()
        {
            if (!active)
            {
                return;
            }

            active = false;
            var inventory = bot.BotHub?.PlayerHub != null ? bot.BotHub.PlayerHub.inventory : null;
            if (inventory != null && bot.ItemLease.IsHeldBy(this))
            {
                var current = inventory.CurItem.SerialNumber;
                var items = inventory.UserInventory.Items;
                var stillOurs = current == equippedSerial || (current == 0 && !items.ContainsKey(equippedSerial));
                if (stillOurs
                    && previousSerial != equippedSerial
                    && previousSerial != current
                    && (previousSerial == 0 || items.ContainsKey(previousSerial)))
                {
                    inventory.ServerSelectItem(previousSerial);
                }
            }

            bot.ItemLease.Release(this);
        }
    }
}
