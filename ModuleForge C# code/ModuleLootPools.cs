using System;
using System.Collections.Generic;

namespace ModuleForge
{
    // The game's module drop pools, and the friendly names a module file uses
    // to pick between them.
    //
    // Mapped from the assets: a crate's DropTable rolls exactly one
    // DropTableWeightedGroup, and those groups are already split by element -
    // which is what makes per-crate targeting possible at all. Only FIVE pools
    // are ever actually rolled by the game; two more exist but are referenced
    // with useGroup false, so nothing draws from them.
    //
    // Deliberately a copy of WeaponForge.ForgeLootPools rather than a
    // reflection call into it: this mod must work with WeaponForge absent, and
    // a table of constant asset names is not worth a cross-mod dependency.
    // The one string that MUST agree between the two mods is Money - see the
    // comment on it.
    public static class ModuleLootPools
    {
        public const string White = "DropGroup Modules Crate White";
        public const string Caps = "DropGroup Modules Crate Caps";
        public const string Purple = "DropGroup Modules Crate Purple";
        public const string Tech = "DropGroup Modules Crate Tech";
        public const string Generic = "DropGroup Modules Crate";

        // Referenced by a DropTable but with useGroup FALSE, so the game never
        // draws from them on its own.
        public const string Level2 = "DropGroup Modules Crate Level 2";
        public const string Box = "DropGroup Box";

        // Crate Money has no module pool of ANY kind, so there is nothing to
        // revive - the pool itself is created at runtime and grafted on.
        //
        // *** This name is shared with WeaponForge on purpose. *** Both mods
        // create-or-find a pool by this name, so when both are installed they
        // fill ONE pool that the crate rolls once. Rename it here and a player
        // with both mods gets two module drops out of every Money crate.
        public const string Money = "Forge Modules Crate Money";

        // Pools the game will not roll by itself, and the DropTable that has to
        // gain an extra item before they can drop. Grafting is ADDITIVE: a new
        // DropTableItem is appended and nothing already in the table is
        // touched, so these crates keep every one of their normal contents and
        // simply gain a module on top.
        private static readonly Dictionary<string, string> _graftInto =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { Level2, "DropTable Crate Level2" },
                { Money, "DropTable Crate Money" }
            };

        public static bool NeedsGraft(string pool, out string tableName)
        {
            tableName = null;

            return pool != null &&
                   _graftInto.TryGetValue(pool, out tableName);
        }

        // Which pool should this table gain, if any module asked for it?
        public static string GraftPoolFor(string tableName)
        {
            foreach (var pair in _graftInto)
            {
                if (string.Equals(
                        pair.Value, tableName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Key;
                }
            }

            return null;
        }

        // Is this a pool that only exists because we grafted it? Such a pool
        // can legitimately be EMPTY of stock module entries, so the injector
        // must not mistake it for a resource-only pool and skip it.
        //
        // Asked by name rather than "did I create it", because with both mods
        // installed the other one may have created it first.
        public static bool IsGraftPool(string poolName)
        {
            string ignored;
            return NeedsGraft(poolName, out ignored);
        }

        // friendly name -> canonical asset name
        private static readonly Dictionary<string, string> _alias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "white", White },
                { "stamina", White },
                { "caps", Caps },
                { "orange", Caps },
                { "purple", Purple },
                { "gel", Purple },
                { "tech", Tech },
                { "generic", Generic },
                { "queen", Generic },
                { "level2", Level2 },
                { "level 2", Level2 },
                { "money", Money },
                { "box", Box },
            };

        public static readonly string[] Live =
            { White, Caps, Purple, Tech, Generic };

        public static bool IsLive(string canonical)
        {
            for (int i = 0; i < Live.Length; i++)
            {
                if (string.Equals(
                        Live[i], canonical, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // Usable as a lootFrom target: either the game already rolls it, or we
        // can graft it on.
        public static bool IsSupported(string canonical)
        {
            string ignored;
            return IsLive(canonical) || NeedsGraft(canonical, out ignored);
        }

        // Accepts a friendly name ("white") or the full asset name
        // ("DropGroup Modules Crate White").
        public static bool TryResolve(string text, out string canonical)
        {
            canonical = null;

            if (string.IsNullOrEmpty(text))
                return false;

            string key = text.Trim();

            if (_alias.TryGetValue(key, out canonical))
                return true;

            if (key.StartsWith("DropGroup", StringComparison.OrdinalIgnoreCase))
            {
                canonical = key;
                return true;
            }

            return false;
        }

        public static string FriendlyList()
        {
            return "white, caps, purple, tech, queen (the Queen's own pool), " +
                   "money or level2 (both get a module roll added), " +
                   "or \"all\"";
        }
    }
}
