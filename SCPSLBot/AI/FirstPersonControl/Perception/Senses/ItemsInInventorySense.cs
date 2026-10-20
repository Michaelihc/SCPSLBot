using InventorySystem.Items;
using System;
using System.Collections.Generic;
using Unity.Jobs;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Perception.Senses
{
    internal class ItemsInInventorySense : ISense
    {
        public event Action<ItemBase> OnSensedItem;
        public event Action OnAfterSensedItems;

        public ItemsInInventorySense(FpcBotPlayer botPlayer)
        {
            _fpcBotPlayer = botPlayer;
        }

        public void ProcessSensedItems()
        {
            var userInventory = _fpcBotPlayer.BotHub.PlayerHub.inventory.UserInventory;
            foreach (var item in userInventory.Items.Values)
            {
                OnSensedItem?.Invoke(item);
            }
            OnAfterSensedItems?.Invoke();
        }

        public void ProcessEnter(Collider other)
        {
        }

        public void ProcessExit(Collider other)
        {
        }

        public IEnumerator<JobHandle> ProcessSensibility()
        {
            yield break;
        }

        private readonly FpcBotPlayer _fpcBotPlayer;
    }
}
