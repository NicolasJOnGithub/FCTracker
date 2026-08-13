using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;

namespace FCTracker.Services
{
    using System.Linq;
    using System.Text;
    using Dalamud.Game.Text;
    using ECommons.DalamudServices;
    using ECommons.ExcelServices;
    using ECommons.IPC;
    using ECommons.Throttlers;
    using Lumina.Excel.Sheets;
    using Newtonsoft.Json;

    [JsonObject(MemberSerialization.OptIn)]
    public class InventoryTrackingData
    {
        [JsonProperty]
        public ulong CID;

        [JsonObject(MemberSerialization.OptOut)]
        private record InventoryItemData(uint Id, int Quantity);

        [JsonProperty]
        private Dictionary<InventoryType, HashSet<InventoryItemData>> inventoryContainers = [];

        private Dictionary<uint, int>? cachedItemCounts;

        private Dictionary<uint, int> CachedItemCounts
        {
            get
            {
                if (this.cachedItemCounts == null)
                {
                    this.cachedItemCounts = new Dictionary<uint, int>();
                    foreach (HashSet<InventoryItemData> container in this.inventoryContainers.Values)
                    {
                        foreach (InventoryItemData item in container)
                            if (this.cachedItemCounts.TryGetValue(item.Id, out int count))
                                this.cachedItemCounts[item.Id] = count + item.Quantity;
                            else
                                this.cachedItemCounts[item.Id] = item.Quantity;
                    }
                }
                return this.cachedItemCounts;
            }
        }

        public int Gil => this.GetItemCount(1);

        private static readonly uint[]  SALVAGE_ITEM_IDS = [22500u, 22501u, 22502u, 22503u, 22504u, 22505u, 22506u, 22507u];
        public static           Item?[] SalvageItems       => field ??= SALVAGE_ITEM_IDS.Select(ExcelItemHelper.Get).Where(item => item != null).ToArray();
        public                  int     GetSalvageCount  => SalvageItems.Sum(item => this.GetItemCount(item!.Value.RowId));
        public                  int[]   GetSalvageCounts => SalvageItems.Select(item => this.GetItemCount(item!.Value.RowId)).ToArray();

        internal static readonly InventoryType[] BAG = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
        internal static readonly InventoryType[] ARMORY = [InventoryType.ArmoryBody, InventoryType.ArmoryEar, InventoryType.ArmoryFeets, InventoryType.ArmoryHands,
            InventoryType.ArmoryHead, InventoryType.ArmoryLegs, InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryNeck,
            InventoryType.ArmoryWaist, InventoryType.ArmorySoulCrystal, InventoryType.ArmoryWrist];

        internal static readonly InventoryType[] SADDLEBAG   = [InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2];
        internal static readonly InventoryType[] FREE_COMPANY = [InventoryType.FreeCompanyCrystals, InventoryType.FreeCompanyGil, InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2, InventoryType.FreeCompanyPage3, InventoryType.FreeCompanyPage4, InventoryType.FreeCompanyPage5];
        
        internal static readonly InventoryType[] OTHER     = [InventoryType.Currency, InventoryType.Crystals];

        internal static readonly InventoryType[] ALL = [..ARMORY, ..BAG, ..SADDLEBAG, ..FREE_COMPANY, ..OTHER];
        internal static readonly uint[] ALL_AT = ALL.Select(t => (uint)t).ToArray();

        public static string GetSalvageText(int[] counts)
        {
            StringBuilder sb = new();

            long gilTotal = 0;

            for (int i = 0; i < counts.Length; i++)
            {
                int itemCount = counts[i];
                if (itemCount > 0)
                {
                    Item salvageItem    = SalvageItems[i]!.Value;
                    long itemTotalPrice = itemCount * salvageItem.PriceLow;
                    gilTotal += itemTotalPrice;

                    sb.AppendLine($"{salvageItem.Name} {itemCount}x {salvageItem.PriceLow:##,#0}{SeIconChar.Gil.ToIconString()}: {itemTotalPrice:##,#0}{SeIconChar.Gil.ToIconString()}");
                }
            }
            sb.Insert(0, $"Total Gil: {gilTotal:##,#0}{SeIconChar.Gil.ToIconString()}\n");
            return sb.ToString();
        }

        public int GetItemCount(uint id)
        {
            this.cachedItemCounts ??= [];
            if (ECommonsIPC.AllaganTools.Available && this.CID != 0 && (!this.cachedItemCounts.ContainsKey(id) || EzThrottler.Throttle($"ItemCheck_{id}_{this.CID}", 300_000)))
            {
                if(Configuration.Instance.AllCharData.TryGetValue(this.CID, out CharData charData))
                    if (charData.ATChars != null)
                    {
                        uint atCount = charData.ATChars.Aggregate<ulong, uint>(0, (current, type) => current + ECommonsIPC.AllaganTools.ItemCount(id, type, -1));
                        Svc.Log.Debug($"Item count polled from AT for {id} for CID {this.CID}: {atCount}");

                        this.cachedItemCounts[id] = (int)atCount;
                    }
            }

            return this.CachedItemCounts.GetValueOrDefault(id, 0);
        }

        public void WipeInventory()
        {
            this.inventoryContainers.Clear();
            Configuration.Instance.Save();
        }

        public unsafe void RefreshInventoryData()
        {
            this.cachedItemCounts?.Clear();

            if (!Configuration.Instance.GlobalData.GatherDataSelf || ECommonsIPC.AllaganTools.Available)
                return;

            InventoryManager* inventory = InventoryManager.Instance();

            foreach (InventoryType type in Enum.GetValues<InventoryType>())
            {
                InventoryContainer* container = inventory->GetInventoryContainer(type);

                if (container != null && container->IsLoaded)
                {
                    this.inventoryContainers[type] = [];
                    for (int i = 0; i < container->Size; i++)
                    {
                        InventoryItem inventoryItem = container->Items[i];
                        if(inventoryItem.ItemId != 0)
                            this.inventoryContainers[type].Add(new InventoryItemData(inventoryItem.ItemId, inventoryItem.Quantity));
                    }
                }
            }

            this.cachedItemCounts = null;

            Configuration.Instance.Save();
        }
    }
}