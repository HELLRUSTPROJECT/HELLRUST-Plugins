using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using static NPCVendingMachine;

namespace Oxide.Plugins
{
    [Info("Infinite Vending Stock", "Rustic", "1.0.2")]
    [Description("A very simple plugin unblocking buy amount limit and giving unlimited stock to all NPC Vending Machines")]

    public class InfiniteVendingStock : CovalencePlugin
    {

        private void OnServerInitialized()
        {
            server.Command("o.reload InfiniteVendingStock");
            
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var vendingMachine = entity as NPCVendingMachine;
                if (vendingMachine == null)
                    continue;

                RestockItems(vendingMachine);
            }
        }

        private void OnVendingTransaction(NPCVendingMachine vendingMachine)
        {
            NextTick(() =>
            {
                if (vendingMachine != null && !vendingMachine.IsDestroyed)
                {
                    RestockItems(vendingMachine);
                }
            });
        }

        private void RestockItems(NPCVendingMachine vendingMachine)
        {
            foreach (var item in vendingMachine.inventory.itemList)
            {
                if (item.amount != 1000000)
                {
                    item.amount = 1000000;
                    item.MarkDirty();
                }
            }
        }

    }
}