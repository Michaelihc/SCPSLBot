using InventorySystem.Items;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item.Beliefs
{
    /// <summary>A carried item matching the criteria (the first one found), or null.</summary>
    internal class ItemInInventory<C> : Belief<bool> where C : IItemBeliefCriteria
    {
        public C Criteria { get; }
        public ItemInInventory(C criteria, ItemsInInventorySense inventorySense)
        {
            Criteria = criteria;
            this._itemsInInventorySense = inventorySense;

            _itemsInInventorySense.OnSensedItem += ProcessSensedItem;
            _itemsInInventorySense.OnAfterSensedItems += ProcessAbsentItem;
        }

        private readonly ItemsInInventorySense _itemsInInventorySense;
        private ItemBase firstMatch;
        private bool currentStillCarried;

        private void ProcessSensedItem(ItemBase item)
        {
            if (!Criteria.EvaluateItem(item))
            {
                return;
            }

            firstMatch ??= item;
            if (ReferenceEquals(item, Item))
            {
                currentStillCarried = true;
            }
        }

        private void ProcessAbsentItem()
        {
            // Keep the item already believed in while it is carried; otherwise switch to another
            // matching item (or none) so actions never act on an item that left the inventory.
            if (!currentStillCarried && !ReferenceEquals(firstMatch, Item))
            {
                Update(firstMatch);
            }

            firstMatch = null;
            currentStillCarried = false;
        }

        public ItemBase Item { get; private set; }

        public void Update(ItemBase item)
        {
            Item = item;
            InvokeOnUpdate();
        }

        public override string ToString()
        {
            return $"{nameof(ItemInInventory<C>)}({Criteria}): {Item}";
        }
    }
}
