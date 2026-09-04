using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // A module that makes your weapon TERRAFORM - it rewrites the terrain it
    // hits from one cell type into another.
    //
    // ================================================================
    // THE COMPLETE LIST OF CELL TYPES, because it is not discoverable
    // ================================================================
    //
    //   name                 id  collider     pass through?  notes
    //   -------------------  --  -----------  -------------  ---------------
    //   CellType Bark         1  NonTrigger   no             burns 4s
    //   CellType Gold         2  NonTrigger   no             the ore
    //   CellType_Blob         3  NonTrigger   no             burns 2s
    //   CellType_Hazard       4  NonTrigger   no             1 CONTACT DAMAGE,
    //                                                        10 pushback
    //   CellType_Moss         5  NonTrigger   no             burns 2s
    //   CellType_Night        6  NonTrigger   no             burns 3s
    //   CellType_Shard        7  NonTrigger   no
    //   CellType_Slime        8  Trigger      YES            passable goo
    //   CellType_Solid        9  NonTrigger   no             burns 15s
    //   CellType_Tuff        10  NonTrigger   no
    //   CellType_Panel       11  NonTrigger   no
    //   CellType_Obsidian    12  NonTrigger   no
    //   CellType_Mud         13  NonTrigger   no
    //   CellType Fog Mist    14  None         YES            no collider
    //   CellType Fog Musk    15  None         YES            no collider
    //   CellType Fog Steam   16  None         YES            no collider
    //
    // `Empty` is id 0 and a CONST on the class rather than an asset, so it
    // cannot be named here - and it does not need to be. A Fog has no collider
    // at all, so converting rock to Fog cuts a genuine tunnel.
    //
    // THE ONLY STOCK CONVERTER IN THE GAME is `Weapon Crawler Laser`: ten types
    // to Hazard, radius 4. An enemy weapon whose job is making the room hostile.
    //
    // BEAM-ONLY WITHOUT WEAPON FORGE, and that is the game's own limit rather
    // than a choice. `HitscanWeapon` is the sole caller of `Level.ConvertCells`
    // - the field sits on every weapon and nothing else reads it. Weapon Forge
    // extends it to projectiles and lobbed shots; with that mod absent this
    // still works on beams, which is exactly what the base game does.
    [Serializable]
    public class CellConvertEffect : ModuleEffect, IWeaponModifier,
                                     IHasDescriptionForWeapon
    {
        // What the terrain becomes. Fog Mist by default, which has no collider
        // - so out of the box this is a DIGGING module.
        public CellType result;

        // Which cells may be eaten. Empty means "every solid type", resolved at
        // Modify time, which is the useful default: a digging module that
        // refused to dig one particular rock would be baffling.
        public List<CellType> convertable = new List<CellType>();

        public FloatSeries radius;

        [NonSerialized] private static List<CellType> _allSolid;

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        private float Radius
        {
            get
            {
                float v = radius.GetElement(Level - 1);
                return (v <= 0f) ? 2f : v;
            }
        }

        // Every cell type with a real collider - i.e. everything that is
        // actually in your way. Cached because it walks the loaded assets.
        //
        // Deliberately EXCLUDES Slime and the three Fogs: those are already
        // passable, so a digging module converting them would burn its radius
        // on cells that were never blocking anything.
        private static List<CellType> AllSolid()
        {
            if (_allSolid != null)
                return _allSolid;

            _allSolid = new List<CellType>();

            try
            {
                foreach (UnityEngine.Object o in
                         Resources.FindObjectsOfTypeAll(typeof(CellType)))
                {
                    var c = o as CellType;

                    if (c == null || c.id == 0)
                        continue;

                    if (c.colliderType == CellType.ColliderType.NonTrigger)
                        _allSolid.Add(c);
                }
            }
            catch (Exception)
            {
                // A missing asset is not a reason to break the module.
            }

            return _allSolid;
        }

        // WeaponFactory.Create calls this for every module in the weapon's
        // cluster, before the weapon is equipped - so writing the weapon's
        // live CellConvertData here is exactly the right moment, and it is
        // redone from the template on every cluster refresh.
        public void Modify(WeaponBase weaponBase)
        {
            if (weaponBase == null || result == null)
                return;

            try
            {
                // A CLASS, not a struct - so this must be a FRESH instance
                // rather than a mutation. The weapon copies the reference
                // straight off its template (`CellConvertData = data.cellConvertData`),
                // so editing it in place would rewrite the shared ASSET and
                // leak into every other weapon built from that template - and
                // survive until the game was restarted.
                var cfg = new CellConvertData
                {
                    enabled = true,
                    radius = Radius,
                    resultCellType = result,
                    convertableCells =
                        (convertable != null && convertable.Count > 0)
                            ? new List<CellType>(convertable)
                            : new List<CellType>(AllSolid())
                };

                // Never convert terrain INTO the thing being converted, or the
                // radius is spent rewriting cells to what they already were.
                cfg.convertableCells.Remove(result);

                weaponBase.CellConvertData = cfg;
            }
            catch (Exception)
            {
                // Same rule as everywhere else here: a bad module does nothing,
                // it does not take the run down.
            }
        }

        // Every field - Module's constructor Clones each effect off the
        // ModuleData, so the clone is what actually runs.
        public override ModuleEffect Clone()
        {
            return new CellConvertEffect
            {
                result = result,
                convertable = (convertable != null)
                    ? new List<CellType>(convertable)
                    : new List<CellType>(),
                radius = radius
            };
        }

        public void GetDescription(
            WeaponBase weapon, bool isInstalled,
            List<DisplayableProperty> properties)
        {
            if (properties == null || result == null)
                return;

            string label = TextFormatter.ColoredText(
                TextFormatter.capsColor, "TERRAFORM");

            // The asset names are not presentable - "CellType_Fog Mist" on a
            // card is noise. Strip the prefix and the underscore.
            string name = result.name
                .Replace("CellType_", "")
                .Replace("CellType ", "");

            properties.Add(new DisplayableProperty(
                label, name + " r" + Radius.ToString("0.##")));
        }
    }
}
