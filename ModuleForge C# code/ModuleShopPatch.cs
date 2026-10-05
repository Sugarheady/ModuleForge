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
            ModuleForgeLog.Source("ModuleForge");

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

                    // ★ THE CONFIG IS HANDED BACK, NOT LOOKED UP AGAIN.
                    // `config.Get(id)` reads a dictionary that is only rebuilt
                    // by `config.Initialize()` after this loop, so for an item
                    // added this pass it answers null - which is why every
                    // Forge module's summary line said "for free" in R19 and
                    // the "hidden until you have found" half never printed.
                    // Weapon Forge had the identical read and it cost more
                    // there: its repeat-in-shop switch hung off the same null.
                    ShopItemConfig made;

                    if (!EnsureConfig(config, entry, ref configChanged, out made))
                        continue;

                    bool gated = made.unlockRequirements != null &&
                                 made.unlockRequirements.Count > 0;

                    // ★ A GATED ITEM GETS THE PRICE AND NOT THE POOL - R19
                    // test 270, STARGATE CORE on sale at the first station with
                    // no Powerstar found. `RunData.RegisterShopUnlock` draws
                    // from the per-tier pools and never looks at
                    // `unlockRequirements`, and it runs at run start. The gate
                    // is honoured in exactly one place: `Shop` calls
                    // `RunData.AddShopItemsWhereRequirementsMet` as it opens,
                    // which walks the price table and adds anything whose
                    // ingredients you have ever owned.
                    if (!gated)
                        InjectPool(shopData, entry.module, entry.shopUnlockLevel);

                    _injected.Add(entry.module.Id);

                    // Read the price back off the CONFIG rather than
                    // echoing the authored numbers. A currency that could not
                    // be resolved is dropped, and a line printing what the
                    // file asked for would report a price the shop is not
                    // charging - a confirmation overruling its own refusal.
                    Log.LogInfo(
                        "Added module '" + entry.module.Id + "' to the shop " +
                        (gated
                            ? "HIDDEN until you have found " +
                              Names(made.unlockRequirements) + " (then on " +
                              "sale at any station you open - a gated item " +
                              "is not tied to a station tier" +
                              (entry.shopUnlockLevel > 0
                                  ? ", so its shopUnlockLevel " +
                                    entry.shopUnlockLevel + " is not used"
                                  : "") + ")"
                            : "at unlock level " + entry.shopUnlockLevel) +
                        " for " + ModuleForgeShopCost.Describe(made) + ".");
                }

                if (configChanged)
                    config.Initialize();
            }
            catch (Exception e)
            {
                Log.LogError("Module shop injection failed: " + e);
            }
        }

        private static string Names(List<Ingredient> ingredients)
        {
            var parts = new List<string>();

            for (int i = 0; i < ingredients.Count; i++)
            {
                if (ingredients[i] == null)
                    continue;

                parts.Add(ingredients[i].displayName ?? ingredients[i].id);
            }

            return string.Join(" and ", parts.ToArray());
        }

        // Hands the config back - see the call site for why it must not be
        // looked up again before `config.Initialize()`.
        private static bool EnsureConfig(
            ShopItemsConfig config,
            ModuleEntry entry,
            ref bool configChanged,
            out ShopItemConfig made)
        {
            made = config.Get(entry.module.Id);

            if (made != null)
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
                        // Added to the price on every purchase (0 = fixed).
                        amount = entry.shopPriceIncrement
                    }
                },
                // Filled below. Left empty here so both lists are always
                // constructed, which is what `Apply` appends onto.
                unlockRequirements = new List<Ingredient>()
            };

            // EVERYTHING BEYOND MONEY. Appended rather than replacing, and
            // that is the stock convention rather than a compromise: all 62
            // items in the game's own shop table carry a money price, and 25
            // of them carry ingredients ON TOP of it. A file that wants an
            // ingredient-only cost writes `"shopPrice": 0`.
            ModuleForgeShopCost.Apply(
                itemConfig, entry.shopCost, entry.shopCostIncrement,
                entry.module.Id);

            itemConfig.unlockRequirements =
                ModuleForgeShopCost.Unlocks(entry.shopUnlock, entry.module.Id);

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
            made = itemConfig;
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
