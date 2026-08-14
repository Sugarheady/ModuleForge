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
                var sprite = ForgeAssets.ResolveIcon(iconName);
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

            // May the same module drop more than once in a run?
            ApplyLootRepeat(module, root, fileName);

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
                lootGroups = ParseLootFrom(root, inLoot, fileName),
                lootWeight = (float?)root["lootWeight"] ?? 10f,
                shopPrice = shopPrice,
                shopPriceIncrement = shopPriceIncrement,
                shopUnlockLevel = (int?)root["shopUnlockLevel"] ?? 1
            };
        }

        // "lootFrom": which crate pools this module may drop from.
        //
        // Absent (or "all") keeps the original behaviour - every module pool -
        // because that is what "source": "loot" has always meant here. A list
        // picks specific ones. Returns null for "all".
        private static string[] ParseLootFrom(
            JObject root,
            bool inLoot,
            string fileName)
        {
            JToken token = root["lootFrom"];

            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (!inLoot)
            {
                Log.LogWarning(
                    fileName + ": \"lootFrom\" was set but this module is not " +
                    "loot-enabled, so it can never drop. Add " +
                    "\"source\": \"loot\" (or \"both\") too.");
            }

            var names = new List<string>();

            if (token.Type == JTokenType.String)
            {
                names.Add((string)token);
            }
            else if (token is JArray)
            {
                foreach (JToken t in (JArray)token)
                {
                    string s = (string)t;

                    if (!string.IsNullOrEmpty(s))
                        names.Add(s);
                }
            }
            else
            {
                Log.LogWarning(
                    fileName + ": \"lootFrom\" should be a name or a list of " +
                    "names (" + ModuleLootPools.FriendlyList() + ") - ignored.");
                return null;
            }

            var resolved = new List<string>();

            foreach (string name in names)
            {
                string trimmed = (name ?? string.Empty).Trim();

                if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals("any", StringComparison.OrdinalIgnoreCase))
                {
                    return null;   // every pool
                }

                string canonical;

                if (!ModuleLootPools.TryResolve(trimmed, out canonical))
                {
                    Log.LogWarning(
                        fileName + ": lootFrom '" + trimmed + "' is not a " +
                        "known crate pool - use " +
                        ModuleLootPools.FriendlyList() + ". Skipped.");
                    continue;
                }

                if (!ModuleLootPools.IsSupported(canonical))
                {
                    Log.LogWarning(
                        fileName + ": lootFrom '" + trimmed + "' resolves to '" +
                        canonical + "', which the GAME never rolls and which " +
                        "cannot be grafted on. A module in that pool could " +
                        "never drop. Skipped.");
                    continue;
                }

                // Money and Level 2 have no module roll of their own, so
                // naming one makes the mod ADD one to that crate. Say so - it
                // changes what a stock crate gives, which is worth knowing.
                string table;

                if (ModuleLootPools.NeedsGraft(canonical, out table))
                {
                    Log.LogInfo(
                        fileName + ": '" + trimmed + "' has no module drop in " +
                        "the base game, so a module roll will be ADDED to '" +
                        table + "'. That crate keeps everything it normally " +
                        "drops and gains a module on top." +
                        (canonical == ModuleLootPools.Level2
                            ? " This also revives the 5 stock regen/generator " +
                              "modules in that pool, which the game otherwise " +
                              "never rolls - your module competes with them."
                            : " Only Forge content targeting \"money\" is in " +
                              "that pool, so one of it always drops."));
                }

                if (!resolved.Contains(canonical))
                    resolved.Add(canonical);
            }

            if (resolved.Count == 0)
            {
                Log.LogWarning(
                    fileName + ": \"lootFrom\" left no usable pools, so this " +
                    "module falls back to dropping from ALL of them.");
                return null;
            }

            Log.LogInfo(
                fileName + ": drops only from " +
                string.Join(", ", resolved.ToArray()));

            return resolved.ToArray();
        }

        // "lootRepeat": may the same module drop more than once in a run?
        //
        // The game's own anti-duplicate rule lives on the module:
        // DroppabbleItemDistribution.GetWeight multiplies an entry's weight by
        // repeatedDropChanceMultiplyer ONCE FOR EACH copy already dropped this
        // run. 120 of ~145 stock modules set it to 0, so a module you already
        // own drops to weight 0 and cannot appear again.
        //
        // Our module is a private clone of a shell, so changing this affects
        // only this module - it can never make a stock one start repeating.
        private static void ApplyLootRepeat(
            ModuleData module,
            JObject root,
            string fileName)
        {
            JToken token = root["lootRepeat"];

            if (module == null || token == null ||
                token.Type == JTokenType.Null)
            {
                return;   // keep whatever the template shell had
            }

            float value;

            if (token.Type == JTokenType.Boolean)
            {
                // true = full chance every time, false = the stock "once only".
                value = (bool)token ? 1f : 0f;
            }
            else
            {
                // Strings are accepted deliberately: the builder page writes
                // this field as text, and anyone hand-editing is just as likely
                // to type "true" as true. Rejecting those would turn a
                // reasonable file into a silent no-op.
                if (!TryReadRepeat(token, out value))
                {
                    Log.LogWarning(
                        fileName + ": \"lootRepeat\" should be true, false, or " +
                        "a number from 0 to 1 - got '" + token +
                        "', ignored.");
                    return;
                }

                value = Mathf.Max(0f, value);

                if (value > 1f)
                {
                    Log.LogWarning(
                        fileName + ": \"lootRepeat\": " + value +
                        " is above 1, which makes the module MORE likely to " +
                        "drop again the more you already have. Legal, but " +
                        "probably not what you meant - 1 keeps the chance " +
                        "unchanged.");
                }
            }

            module.repeatedDropChanceMultiplyer = value;

            Log.LogInfo(
                fileName + ": lootRepeat " + value +
                (value <= 0f
                    ? " (drops once per run, the stock behaviour)"
                    : (value >= 1f
                        ? " (can drop again at full chance)"
                        : " (each copy you own makes the next x" + value +
                          " as likely)")));
        }

        // Accepts a real number, or the words a person would actually type.
        private static bool TryReadRepeat(JToken token, out float value)
        {
            value = 0f;

            if (token.Type == JTokenType.Integer ||
                token.Type == JTokenType.Float)
            {
                float? n = (float?)token;

                if (!n.HasValue)
                    return false;

                value = n.Value;
                return true;
            }

            string s = ((string)token ?? string.Empty).Trim();

            if (s.Length == 0)
                return false;

            if (s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                value = 1f;
                return true;
            }

            if (s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                value = 0f;
                return true;
            }

            return float.TryParse(
                s,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }
    }
}
