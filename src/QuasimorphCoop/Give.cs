using System.Linq;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop
{
    // Handing items to the partner: an adjacent party merc's backpack shows up as an extra tab in your
    // inventory (the container view, like a storage box), so items move by the usual drag and drop.
    // Online nothing extra is needed: ItemSync already sends every creature inventory within reach.
    [HarmonyPatch(typeof(InventoryScreen), nameof(InventoryScreen.ConfigureTabs))]
    internal static class GiveTabsPatch
    {
        private static void Postfix(ItemTabsView ____tabsView, ItemsStorageView ____objectStoreView)
        {
            if (!Net.NetSession.LocalInputAllowed)
            {
                return;
            }
            bool hadTabs = ____tabsView.TabsCount > 0;
            foreach (Player partner in Treat.Candidates().ToList())
            {
                ItemStorage backpack = partner.CreatureData.Inventory.BackpackStore;
                if (backpack == null)
                {
                    continue;
                }
                ____tabsView.AddTab(____objectStoreView, backpack);
                ____objectStoreView.AddCaptionByTab(backpack, CoopSession.DisplayName(partner.Mercenary) + " (backpack)");
            }
            if (!hadTabs && ____tabsView.TabsCount > 0)
            {
                ____tabsView.SelectAndShowFirstTab();
            }
        }
    }
}
