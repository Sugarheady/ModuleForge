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
            ModuleForgeLog.Source("ModuleForge");

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

        // A PARTICLE SYSTEM PREFAB, by name - and the filter is the point.
        //
        // `FindAsset` above is fine for a ScriptableObject, because those have
        // no scene instances. A `ParticleSystem` has plenty:
        // `Resources.FindObjectsOfTypeAll` returns every one currently alive in
        // the world alongside the prefabs, and the muzzle flash that happens to
        // be playing right now matches "MuzzleParticle Laser" just as well as
        // the prefab does.
        //
        // Handing a LIVE one to `WeaponBase.MuzzleParticlePrefab` would have
        // `InitializeVisuals` instantiate copies of an object that is mid-play
        // and about to be destroyed - and a destroyed Unity object compares
        // equal to null, so the flash would simply stop working partway through
        // a run with nothing in the log. That is the same shape as the
        // ResourcePickup cache bug in CLAUDE.md: *a live instance is whichever
        // one happened to be lying around.*
        //
        // A prefab's GameObject belongs to no scene, which is the test. Live
        // instances are still accepted as a LAST resort, because a prefab this
        // scan cannot see is worse than a warning - but it says so.
        public static ParticleSystem ResolveParticlePrefab(
            string name, string fileName)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            name = name.Trim();

            ParticleSystem live = null;

            foreach (var asset in
                Resources.FindObjectsOfTypeAll(typeof(ParticleSystem)))
            {
                var ps = asset as ParticleSystem;

                if (ps == null || ps.gameObject == null)
                    continue;

                if (!string.Equals(ps.name, name,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!ps.gameObject.scene.IsValid())
                    return ps;

                if (live == null)
                    live = ps;
            }

            if (live != null)
            {
                Log.LogWarning(
                    fileName + ": '" + name + "' matched a particle system " +
                    "that is ALIVE IN THE WORLD rather than a prefab. It is " +
                    "being used, but it belongs to something on screen - if " +
                    "that object is destroyed the effect stops working with " +
                    "nothing in the log. Prefer a prefab name such as " +
                    "\"MuzzleParticle Laser\".");
            }

            return live;
        }

        // A sprite, or a NUMBERED RUN of them as one flipbook.
        //
        // STOCK ART ONLY, deliberately. Custom PNGs live in Weapon Forge's
        // sprites folder and are borrowed through ForgeInterop everywhere else -
        // but the one caller here is the standalone leech orb, which by
        // definition only runs when Weapon Forge is ABSENT. Reaching for the
        // borrowed loader would be dead code, and `TryResolveIcon` rebuilds at
        // the module-icon footprint (24x24 at PPU 40) which is the wrong size
        // for anything that is not an icon.
        //
        // THE NAMING RULE MATCHES WEAPON FORGE'S, and both are shaped by what
        // must not break: a name ending in _<digits> means exactly that one
        // frame, because names like "part_cyrcle_12" are already in use and
        // animating them through the whole run would change existing art. A
        // sequence is asked for by the BARE BASE ("area_zero_particle_tuff" =
        // nine tuff-bubble frames) or an "Anim" suffix.
        private static readonly Dictionary<string, Sprite[]> _runs =
            new Dictionary<string, Sprite[]>(StringComparer.OrdinalIgnoreCase);

        // The name may carry a FRAME SELECTION, the same syntax Weapon Forge
        // uses so there is one vocabulary across both mods:
        //
        //     "area_zero_particle_tuff"        all nine frames
        //     "area_zero_particle_tuff:2-6"    frames 2 to 6
        //     "area_zero_particle_tuff:8-0"    all nine, REVERSED
        //     "area_zero_particle_tuff:0,3,5"  those three, in that order
        //
        // Frame numbers are 0-based, and for a stock run the number IS the one in
        // the sprite's name. SPEED is NOT taken from the name here: this mod's
        // one caller (the leech orb) has its own `orbFps` key, so an `@fps`
        // suffix would be two ways to say one thing - it warns and points at the
        // key rather than being ignored.
        //
        // Duplicated rather than borrowed, like the tint owner and the buff HUD:
        // the caller only runs with Weapon Forge absent.
        public static Sprite[] ResolveSpriteFrames(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            string raw = name.Trim();
            Sprite[] cached;

            if (_runs.TryGetValue(raw, out cached))
                return cached;

            Sprite[] found = null;

            try
            {
                string key = raw;
                int[] order = null;

                int at = key.LastIndexOf('@');

                if (at >= 0)
                {
                    Log.LogWarning(
                        "sprite name '" + raw + "': a speed in the name ('@...') " +
                        "is a Weapon Forge spelling. Here the animation speed is " +
                        "the effect's own \"orbFps\" key, so this part is " +
                        "ignored - the frames still work.");

                    key = key.Substring(0, at).Trim();
                }

                int colon = key.LastIndexOf(':');

                if (colon >= 0)
                {
                    order = ParseOrder(key.Substring(colon + 1).Trim(), raw);
                    key = key.Substring(0, colon).Trim();
                }

                found = Gather(key);

                if (order != null && found != null && found.Length > 0)
                    found = Reorder(found, order, raw);
            }
            catch (Exception e)
            {
                Log.LogWarning("Resolving sprite frames failed: " + e.Message);
            }

            _runs[raw] = found;
            return found;
        }

        // "2-6" | "6-2" | "0,3,5" | "4"
        private static int[] ParseOrder(string text, string raw)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var made = new List<int>();

            foreach (string chunk in text.Split(','))
            {
                string part = chunk.Trim();

                if (part.Length == 0)
                    continue;

                int dash = part.IndexOf('-', 1);

                if (dash > 0)
                {
                    int a, b;

                    if (!int.TryParse(part.Substring(0, dash).Trim(), out a) ||
                        !int.TryParse(part.Substring(dash + 1).Trim(), out b))
                    {
                        Log.LogWarning(
                            "sprite name '" + raw + "': '" + part + "' is not a " +
                            "frame range. Use \"2-6\", \"6-2\" to reverse it, " +
                            "\"0,3,5\" for a list, or \"4\" for one frame.");
                        continue;
                    }

                    // A RANGE RUNS EITHER WAY - that is the reverse feature.
                    int step = (b >= a) ? 1 : -1;

                    for (int n = a; ; n += step)
                    {
                        made.Add(n);

                        if (n == b || made.Count > 512)
                            break;
                    }
                }
                else
                {
                    int one;

                    if (int.TryParse(part, out one))
                        made.Add(one);
                }
            }

            return (made.Count > 0) ? made.ToArray() : null;
        }

        private static Sprite[] Reorder(Sprite[] frames, int[] order, string raw)
        {
            var made = new List<Sprite>(order.Length);

            for (int i = 0; i < order.Length; i++)
            {
                int n = order[i];

                if (n >= 0 && n < frames.Length && frames[n] != null)
                    made.Add(frames[n]);
            }

            if (made.Count == 0)
            {
                // Falling through to the full run is the useful failure: the art
                // appears and the log says why it is not what was asked for.
                Log.LogWarning(
                    "sprite name '" + raw + "': none of those frame numbers " +
                    "exist - this animation has " + frames.Length +
                    " frame(s), numbered 0 to " + (frames.Length - 1) +
                    ". Using all of them.");

                return frames;
            }

            return made.ToArray();
        }

        private static Sprite[] Gather(string key)
        {
            // An explicit frame number is a request for that frame.
            if (EndsInFrameNumber(key))
            {
                var one = FindAsset(typeof(Sprite), key) as Sprite;
                return (one != null) ? new[] { one } : null;
            }

            string baseName = key;

            if (baseName.EndsWith("Anim", StringComparison.OrdinalIgnoreCase) &&
                baseName.Length > 4)
            {
                baseName = baseName.Substring(0, baseName.Length - 4);
            }

            // ONE scan for the whole run. FindAsset above walks every loaded
            // Sprite on each call, so probing _0, _1, _2 separately would be one
            // full scan per frame.
            var byIndex = new Dictionary<int, Sprite>();
            string prefix = baseName + "_";

            foreach (var obj in Resources.FindObjectsOfTypeAll(typeof(Sprite)))
            {
                var s = obj as Sprite;

                if (s == null || s.name == null ||
                    !s.name.StartsWith(
                        prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int n;

                if (!int.TryParse(s.name.Substring(prefix.Length), out n) ||
                    n < 0)
                {
                    continue;
                }

                if (!byIndex.ContainsKey(n))
                    byIndex[n] = s;
            }

            var run = new List<Sprite>();

            // Gaps END the run rather than being skipped: 0,1,2,7 is far more
            // likely to be two effects sharing a prefix than one animation with
            // a hole in it.
            for (int n = 0; byIndex.ContainsKey(n); n++)
                run.Add(byIndex[n]);

            if (run.Count > 0)
                return run.ToArray();

            // No run - fall back to the exact name as a single sprite.
            var exact = FindAsset(typeof(Sprite), baseName) as Sprite;
            return (exact != null) ? new[] { exact } : null;
        }

        private static bool EndsInFrameNumber(string name)
        {
            int i = name.LastIndexOf('_');

            if (i < 0 || i == name.Length - 1)
                return false;

            for (int c = i + 1; c < name.Length; c++)
            {
                if (!char.IsDigit(name[c]))
                    return false;
            }

            return true;
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
        // A GAME sprite for use as an icon, frame-suffix aware.
        //
        // `:frames` is the ONLY way to choose which frame of a numbered run an
        // icon shows, and without this the whole string reached `FindAsset`, so
        // "HUD_GridTiles_4:2" matched no asset and fell through to the "not a
        // sprite in the game" warning with the suffix as the unstated reason.
        // (That example used to read "_04:2" - doubly unresolvable, since the
        // padding is wrong too. See `PaddedRetry`.)
        //
        // The exact name is tried FIRST, so nothing already written changes and
        // an unsuffixed icon never pays for a run scan. `@fps` is dropped
        // rather than warned about: an icon genuinely cannot animate here -
        // the widget assigns a sprite and nothing ever ticks it.
        private static Sprite StockIcon(string name)
        {
            var exact = FindAsset(typeof(Sprite), name) as Sprite;

            if (exact != null)
                return exact;

            int at = name.LastIndexOf('@');
            string key = (at >= 0) ? name.Substring(0, at).Trim() : name;

            // ★ THE PADDED RETRY HAS TO BE REACHABLE FROM HERE. This branch
            // used to return null for any name with no ':' - which is every
            // plain icon name, "HUD_GridTiles_07" included - so the retry at
            // the bottom of this method only ever ran for a FRAME-suffixed
            // name, the one shape it cannot parse. The fix it was written for
            // never ran; R20's log still printed the "_07" warning every
            // launch. Read what RETURNS above your line.
            if (key.IndexOf(':') < 0)
            {
                if (at >= 0)
                {
                    var bare = FindAsset(typeof(Sprite), key) as Sprite;

                    if (bare != null)
                        return bare;
                }

                return PaddedRetry(key);
            }

            Sprite[] frames = ResolveSpriteFrames(key);

            if (frames != null && frames.Length > 0)
                return frames[0];

            return PaddedRetry(name);
        }

        // "HUD_GridTiles_NN" IS THE WRONG SHAPE AND WE ARE THE ONES WHO TAUGHT
        // IT. The real assets are `HUD_GridTiles_0` .. `_44` and
        // `HUD_Modules_0` .. `_35` - UNPADDED. "NN" reads as two digits, and
        // that string is in five places across the two repos including the
        // worked example in the comment above this method
        // ("HUD_GridTiles_04:2", which resolves to nothing).
        //
        // R17's log has the cost: `Icon 'HUD_GridTiles_07' is neither a game
        // sprite nor one of the 169 ...`, on a file for a test he has not run
        // yet. The name is wrong by one character, the asset is there, and the
        // warning correctly reports a miss while repeating the very convention
        // that caused it.
        //
        // > A DIAGNOSTIC THAT RESTATES A CONVENTION IS TEACHING IT. If the
        // > convention is wrong, the warning is not a safety net - it is the
        // > bug, printed once per reload, in the most authoritative voice the
        // > mod has.
        //
        // So: accept the padded form, resolve it, and say what the real name
        // is. Only ever a RETRY after the exact name has missed, so a genuine
        // asset called `..._07` would still win, and only leading zeros in the
        // trailing number group are touched.
        private static Sprite PaddedRetry(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            int cut = name.LastIndexOf('_');

            if (cut < 0 || cut >= name.Length - 1)
                return null;

            string head = name.Substring(0, cut + 1);
            string tail = name.Substring(cut + 1);

            for (int i = 0; i < tail.Length; i++)
            {
                if (!char.IsDigit(tail[i]))
                    return null;
            }

            string trimmed = tail.TrimStart('0');

            if (trimmed.Length == 0)
                trimmed = "0";

            if (trimmed == tail)
                return null;

            var hit = FindAsset(typeof(Sprite), head + trimmed) as Sprite;

            if (hit == null)
                return null;

            Log.LogWarning(
                "Icon '" + name + "' has a leading zero the game's own art " +
                "does not use - the asset is called '" + head + trimmed +
                "'. Using it. Stock icons are numbered without padding: " +
                "HUD_GridTiles_0 to _44 and HUD_Modules_0 to _35, so it is " +
                "_7 and not _07. Worth correcting in your file; some of this " +
                "mod's own docs said \"HUD_GridTiles_NN\", which is where " +
                "the padding comes from.");

            return hit;
        }

        // Stock is tried FIRST: every module written before custom icons
        // existed names a stock sprite, and a same-named PNG quietly
        // replacing one would be horrible to debug. A collision is reported
        // instead.
        public static Sprite ResolveIcon(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            name = name.Trim();

            var stock = StockIcon(name);
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
                    "icons are called HUD_GridTiles_0 to _44 and " +
                    "HUD_Modules_0 to _35, with NO leading zero - _7, not " +
                    "_07). If you meant your " +
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
                    "HUD_GridTiles_0 to _44 and HUD_Modules_0 to _35, with " +
                    "NO leading zero - _7, not _07. Your own art is named by " +
                    "the PNG file (or by the name in its sheet .json). The " +
                    "module keeps the template's icon.");
            }

            return null;
        }

        // ONE OF THE GAME'S OWN SOUNDS, BY NAME - "Cells/Fuel",
        // "Weapons/Popper/Shoot", "UI/OK". Returns the Sfx guid, which is
        // what a sound field actually holds.
        //
        // NATIVE HERE RATHER THAN BORROWED, and the line is the same one
        // ResolveIcon and ResolveColor already draw: **custom-file loading is
        // Weapon Forge's pipeline and is borrowed; looking a name up in an
        // asset the GAME owns is not a decoder and has to work standalone.**
        // Without this, a Module Forge user with only this mod installed could
        // reach the game's 259 sounds solely by typing a raw guid, because
        // WeaponForge.ForgeSfxRegistry matches on guid alone.
        //
        // Kept in step with WeaponForge.ForgeSfxRegistry.StockGuid, including
        // the empty-entry warning - 70 of the 259 are declared with no clip at
        // all and resolve perfectly while playing nothing. See SOUNDS.txt.
        public static string StockSound(string name, string fileName)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            name = name.Trim();

            AudioDatabase db = Database();

            if (db == null || db.sfxs == null)
                return null;

            foreach (Sfx s in db.sfxs)
            {
                if (s == null || string.IsNullOrEmpty(s.name))
                    continue;

                if (!string.Equals(s.name, name,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (s.audioClips == null || s.audioClips.Items == null ||
                    s.audioClips.Items.Count == 0)
                {
                    Log.LogWarning(
                        fileName + ": '" + s.name + "' is a real sound in " +
                        "the game's audio database but it has NO CLIP - 70 " +
                        "of the 259 entries are declared and empty like " +
                        "this, so it will be silent. See SOUNDS.txt, which " +
                        "marks every empty one.");
                }

                return s.guid;
            }

            return null;
        }

        private static AudioDatabase _audioDb;
        private static bool _audioSearched;

        private static AudioDatabase Database()
        {
            if (_audioSearched)
                return _audioDb;

            _audioSearched = true;

            // Prefer the fullest one: FindObjectsOfTypeAll can turn up an
            // empty placeholder asset alongside the real database. Same guard
            // Weapon Forge's copy uses.
            UnityEngine.Object[] all =
                Resources.FindObjectsOfTypeAll(typeof(AudioDatabase));

            for (int i = 0; i < all.Length; i++)
            {
                var db = all[i] as AudioDatabase;

                if (db == null || db.sfxs == null)
                    continue;

                if (_audioDb == null || db.sfxs.Count > _audioDb.sfxs.Count)
                    _audioDb = db;
            }

            return _audioDb;
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
