using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ModuleForge
{
    // Turns one module-definition JSON into a configured ModuleData.
    // A module's "target" chooses which stock shell to clone, and the
    // shell's moduleType (Passive vs WeaponAugmentation) is what routes
    // it to the ship body vs weapons - we don't touch slots/clusters.
    public static class ModuleBuilder
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge");

        // Same-type shells: cloning inherits the correct moduleType,
        // color/powerCore shapes, slot compatibility and whitelist.
        private const string ShipShell = "Module Passive Add Health";
        private const string WeaponShell = "Module Aug Firerate";

        public static ModuleEntry BuildModule(
            string filePath,
            HashSet<string> alreadyBuilt)
        {
            string fileName = Path.GetFileName(filePath);

            JObject root =
                JObject.Parse(File.ReadAllText(filePath));

            string name = (string)root["name"];

            if (string.IsNullOrEmpty(name))
            {
                Log.LogError(fileName + ": missing required \"name\".");
                return null;
            }

            if (alreadyBuilt != null && alreadyBuilt.Contains(name))
                return null;

            string target =
                ((string)root["target"] ?? "ship")
                    .Trim().ToLowerInvariant();

            bool isWeapon =
                target == "weapon" || target == "weaponaugment" ||
                target == "weaponaugmentation" || target == "gadget";

            string shellName = isWeapon ? WeaponShell : ShipShell;

            var shell =
                ForgeAssets.FindAsset(typeof(ModuleData), shellName)
                    as ModuleData;

            if (shell == null)
            {
                Log.LogError(
                    fileName + ": shell module '" + shellName +
                    "' not found (game version changed?).");
                return null;
            }

            var module = UnityEngine.Object.Instantiate(shell);
            module.name = "ModuleForge " + name;
            module.hideFlags = HideFlags.None;

            var idField =
                typeof(ModuleData).GetField(
                    "id",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            if (idField != null)
            {
                idField.SetValue(
                    module,
                    "MODULEFORGE-" + name.ToUpperInvariant());
            }

            module.displayName =
                (string)root["displayName"] ?? name.ToUpperInvariant();

            module.description =
                (string)root["description"] ??
                "Custom module built by Module Forge.";

            // Icon + color.
            var iconName = (string)root["icon"];

            if (!string.IsNullOrEmpty(iconName))
            {
                var sprite = ForgeAssets.ResolveSprite(iconName);
                if (sprite != null)
                    module.icon = sprite;
            }

            var colorName = (string)root["color"];

            if (!string.IsNullOrEmpty(colorName))
            {
                var colorAsset = ForgeAssets.ResolveColor(colorName);
                if (colorAsset != null)
                    module.color = colorAsset;
            }

            // Replace the shell's effects with the authored ones.
            if (module.effects == null)
                module.effects = new List<ModuleEffect>();

            module.effects.Clear();

            var effectsJson = root["effects"] as JArray;

            if (effectsJson != null)
            {
                foreach (JToken token in effectsJson)
                {
                    var e = token as JObject;
                    if (e == null)
                        continue;

                    var effect = EffectBuilder.Build(e, fileName);
                    if (effect != null)
                        module.effects.Add(effect);
                }
            }

            if (module.effects.Count == 0)
            {
                Log.LogWarning(
                    fileName + ": module '" + name +
                    "' has no valid effects - it will do nothing.");
            }

            // Optional module flags.
            var repeatInShop = (bool?)root["repeatInShop"];
            if (repeatInShop.HasValue)
                module.repeatInShop = repeatInShop.Value;

            var canBeBoosted = (bool?)root["canBeBoosted"];
            if (canBeBoosted.HasValue)
                module.canBeBoosted = canBeBoosted.Value;

            // Availability: loot / shop / both (default loot). Modules
            // aren't loadout picks, so there is no "starter".
            string source =
                ((string)root["source"] ?? "loot")
                    .Trim().ToLowerInvariant();

            // "none" = built and registered, but offered NOWHERE - no drops,
            // no shop. Handy for a module you only want to grant/test another
            // way, or to park a definition without it cluttering your runs.
            bool hidden =
                source == "none" || source == "nowhere" ||
                source == "hidden" || source == "never";

            bool inLoot = source == "loot" || source == "both";
            bool inShop = source == "shop" || source == "both";

            // Unknown value -> fall back to loot so it isn't lost. ("none" is
            // a deliberate choice, so it skips that safety net.)
            if (!inLoot && !inShop && !hidden)
                inLoot = true;

            float shopPrice = (float?)root["shopPrice"] ?? 100f;

            // Escalating shop price. The game adds a FLAT amount to the price
            // every time an item is bought (ShopItem.IncreasePrice), so this
            // is additive, not compounding. "shopPricePercent" is just a
            // friendlier way to express the same thing as a share of the
            // base price (167 -> +1.67x base per purchase).
            float shopPriceIncrement = (float?)root["shopPriceIncrement"] ?? 0f;
            float? pricePercent = (float?)root["shopPricePercent"];
            if (shopPriceIncrement <= 0f && pricePercent.HasValue)
                shopPriceIncrement = shopPrice * (pricePercent.Value / 100f);
            if (shopPriceIncrement < 0f)
                shopPriceIncrement = 0f;

            // The game only escalates (and only re-stocks) an item flagged
            // repeatInShop - otherwise it's removed after one purchase and
            // the increment would never be used. Turn it on automatically
            // when an increment was asked for, unless explicitly set.
            if (shopPriceIncrement > 0f && !repeatInShop.HasValue &&
                !module.repeatInShop)
            {
                module.repeatInShop = true;
                Log.LogInfo(
                    fileName + ": shop price increment set, so repeatInShop " +
                    "was enabled (the game only re-stocks and re-prices " +
                    "repeatable items).");
            }

            Log.LogInfo(
                "Built module '" + module.displayName + "' (" +
                (isWeapon ? "weapon" : "ship") + ", " +
                module.effects.Count + " effect(s)) from " + fileName);

            return new ModuleEntry
            {
                name = name,
                module = module,
                inLoot = inLoot,
                inShop = inShop,
                lootWeight = (float?)root["lootWeight"] ?? 10f,
                shopPrice = shopPrice,
                shopPriceIncrement = shopPriceIncrement,
                shopUnlockLevel = (int?)root["shopUnlockLevel"] ?? 1
            };
        }
    }
}
