using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // Makes shop-enabled custom modules purchasable. PUNK gates the shop
    // by a station threshold: unlockedShopCount (== stations unlocked)
    // indexes ShopUpgradeData.perLevelData[N]. We inject at a
    // RunData.Initialize prefix (both shop assets are live there, and it
    // runs before the first shop roll):
    //   1. a ShopItemConfig price (mandatory - missing it crashes the
    //      shop with a NullReference), then
    //   2. a dedicated probablity-1 single-module group at the chosen
    //      station tier, so the module reliably appears at its tier.
    [HarmonyPatch(typeof(RunData), "Initialize")]
    public class ModuleShopPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge");

        private static readonly HashSet<string> _injected =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static int _nextLineNumber = 9500;

        static void Prefix()
        {
            try
            {
                ModuleForgeRegistry.BuildAll();

                ShopUpgradeData shopData;
                ShopItemsConfig config;

                if (!ServiceLocator.TryGet<ShopUpgradeData>(out shopData) ||
                    shopData == null)
                {
                    return;
                }

                if (!ServiceLocator.TryGet<ShopItemsConfig>(out config) ||
                    config == null)
                {
                    return;
                }

                bool configChanged = false;

                foreach (ModuleEntry entry in ModuleForgeRegistry.Entries)
                {
                    if (!entry.inShop || entry.module == null)
                        continue;

                    if (_injected.Contains(entry.module.Id))
                        continue;

                    if (!EnsureConfig(config, entry, ref configChanged))
                        continue;

                    InjectPool(shopData, entry.module, entry.shopUnlockLevel);

                    _injected.Add(entry.module.Id);

                    Log.LogInfo(
                        "Added module '" + entry.module.Id +
                        "' to the shop at unlock level " +
                        entry.shopUnlockLevel + " for " +
                        entry.shopPrice + ".");
                }

                if (configChanged)
                    config.Initialize();
            }
            catch (Exception e)
            {
                Log.LogError("Module shop injection failed: " + e);
            }
        }

        private static bool EnsureConfig(
            ShopItemsConfig config,
            ModuleEntry entry,
            ref bool configChanged)
        {
            if (config.Get(entry.module.Id) != null)
                return true;

            var money = ForgeAssets.ResolveResource("Resource Money");

            if (money == null)
            {
                Log.LogWarning(
                    "Can't shop-add '" + entry.module.Id +
                    "': 'Resource Money' currency not found.");
                return false;
            }

            var itemConfig = new ShopItemConfig
            {
                id = entry.module.Id,
                lineNumber = _nextLineNumber++,
                price = new List<Price>
                {
                    new Price
                    {
                        currencyType = Price.CurrencyType.Resource,
                        resource = money,
                        amount = entry.shopPrice
                    }
                },
                priceIncrement = new List<Price>
                {
                    new Price
                    {
                        currencyType = Price.CurrencyType.Resource,
                        resource = money,
                        amount = 0f
                    }
                },
                unlockRequirements = new List<Ingredient>()
            };

            FieldInfo itemListField =
                typeof(ShopItemsConfig).BaseType.GetField(
                    "itemList",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            if (itemListField == null)
            {
                Log.LogError("ShopItemsConfig itemList field not found.");
                return false;
            }

            var itemList =
                itemListField.GetValue(config) as List<ShopItemConfig>;

            if (itemList == null)
                return false;

            itemList.Add(itemConfig);
            configChanged = true;
            return true;
        }

        private static void InjectPool(
            ShopUpgradeData shopData,
            ModuleData module,
            int level)
        {
            if (level < 0)
                level = 0;

            if (shopData.perLevelData == null)
                shopData.perLevelData =
                    new ShopUpgradeData.PerLevelData[0];

            if (level >= shopData.perLevelData.Length)
            {
                var resized =
                    new ShopUpgradeData.PerLevelData[level + 1];

                Array.Copy(
                    shopData.perLevelData, resized,
                    shopData.perLevelData.Length);

                for (int i = shopData.perLevelData.Length;
                     i <= level; i++)
                {
                    resized[i] = new ShopUpgradeData.PerLevelData
                    {
                        groups =
                            new ShopUpgradeData.PerLevelData.PerLevelGroup[0]
                    };
                }

                shopData.perLevelData = resized;
            }

            var group =
                ScriptableObject.CreateInstance<ShopItemGroup>();

            group.hideFlags = HideFlags.HideAndDontSave;
            group.moduleDistribution.Add(module, 1f);

            var plg =
                new ShopUpgradeData.PerLevelData.PerLevelGroup
                {
                    probablity = 1f,
                    group = group
                };

            ShopUpgradeData.PerLevelData tier =
                shopData.perLevelData[level];

            var existing = tier.groups
                ?? new ShopUpgradeData.PerLevelData.PerLevelGroup[0];

            var newGroups =
                new ShopUpgradeData.PerLevelData.PerLevelGroup[
                    existing.Length + 1];

            Array.Copy(existing, newGroups, existing.Length);
            newGroups[existing.Length] = plg;

            tier.groups = newGroups;
            shopData.perLevelData[level] = tier;
        }
    }
}
