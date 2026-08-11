using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // Asset lookups + color/sprite resolution, mirroring WeaponForge's
    // JsonFieldMapper helpers (kept self-contained so ModuleForge does
    // not depend on WeaponForge).
    public static class ForgeAssets
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge");

        // Friendly resource name -> actual asset name. The game uses a
        // couple of internal aliases (Stamina == White, Gel == Purple).
        private static readonly Dictionary<string, string> ResourceAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Health", "Resource Health" },
                { "Stamina", "Resource White" },
                { "White", "Resource White" },
                { "Caps", "Resource Caps" },
                { "Electron", "Resource Electron" },
                { "Fuel", "Resource Fuel" },
                { "Gel", "Resource Purple" },
                { "Purple", "Resource Purple" },
                { "Tech", "Resource Tech" },
                { "Money", "Resource Money" },
                { "Fire", "Resource Caps" },
            };

        public static UnityEngine.Object FindAsset(Type type, string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            foreach (var asset in
                Resources.FindObjectsOfTypeAll(type))
            {
                if (string.Equals(
                    asset.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return asset;
                }
            }

            return null;
        }

        public static Resource ResolveResource(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            string assetName = name.Trim();

            // Map a friendly/short name to the real asset name.
            string mapped;

            if (!assetName.StartsWith(
                    "Resource ", StringComparison.OrdinalIgnoreCase) &&
                ResourceAlias.TryGetValue(assetName, out mapped))
            {
                assetName = mapped;
            }

            var res = FindAsset(typeof(Resource), assetName) as Resource;

            if (res == null)
            {
                // Last resort: try "Resource <name>".
                res = FindAsset(
                    typeof(Resource), "Resource " + name.Trim())
                    as Resource;
            }

            if (res == null)
                Log.LogWarning("Resource '" + name + "' not found.");

            return res;
        }

        // The picture on the module card. Accepts a stock sprite name and
        // also your OWN art, by the name it has in Weapon Forge's sprites
        // folder - that mod owns the whole import pipeline and this one
        // borrows it, so there is a single folder and a single set of names
        // across both. Without Weapon Forge installed, only stock sprites
        // resolve and the warning says so.
        //
        // Stock is tried FIRST: every module written before custom icons
        // existed names a stock sprite, and a same-named PNG quietly
        // replacing one would be horrible to debug. A collision is reported
        // instead.
        public static Sprite ResolveIcon(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            name = name.Trim();

            var stock = FindAsset(typeof(Sprite), name) as Sprite;
            Sprite custom = ForgeInterop.TryResolveIcon(name);

            if (stock != null)
            {
                if (custom != null)
                {
                    Log.LogWarning(
                        "Icon '" + name + "' is both a game sprite and one " +
                        "of yours in Weapon Forge's sprites folder. The " +
                        "GAME's is used. Rename yours to use it.");
                }

                return stock;
            }

            if (custom != null)
                return custom;

            if (!ForgeInterop.HasCustomSprites)
            {
                Log.LogWarning(
                    "Icon '" + name + "' is not a sprite in the game (module " +
                    "icons are called HUD_GridTiles_NN). If you meant your " +
                    "own art: custom icons are loaded from Weapon Forge's " +
                    "'sprites' folder, and Weapon Forge is not installed. " +
                    "The module keeps the template's icon.");
            }
            else
            {
                Log.LogWarning(
                    "Icon '" + name + "' is neither a game sprite nor one of " +
                    "the " + ForgeInterop.CustomSpriteCount + " in Weapon " +
                    "Forge's sprites folder. Stock module icons are called " +
                    "HUD_GridTiles_NN; your own art is named by the PNG " +
                    "file (or by the name in its sheet .json). The module " +
                    "keeps the template's icon.");
            }

            return null;
        }

        // "#rrggbb" / html name / a game ColorAsset name ("ColorPurple").
        // Hex/html builds a fresh ColorAsset; otherwise looks one up.
        public static ColorAsset ResolveColor(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            text = text.Trim();

            Color parsed;

            if (text.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(text, out parsed))
                    return MakeColorAsset(parsed, text);

                Log.LogWarning("'" + text + "' is not a valid hex color.");
                return null;
            }

            var asset =
                FindAsset(typeof(ColorAsset), text) as ColorAsset;

            if (asset != null)
                return asset;

            if (ColorUtility.TryParseHtmlString(text, out parsed))
                return MakeColorAsset(parsed, text);

            Log.LogWarning("Color '" + text + "' not found.");
            return null;
        }

        private static ColorAsset MakeColorAsset(Color color, string label)
        {
            var asset = ScriptableObject.CreateInstance<ColorAsset>();
            asset.name = "ModuleForge Color " + label;
            asset.hideFlags = HideFlags.HideAndDontSave;
            asset.color = color;
            return asset;
        }
    }
}
