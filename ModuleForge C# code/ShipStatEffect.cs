using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // Changes how the SHIP handles - speed, dash, i-frames, boost economy.
    //
    // An entire surface the game's own module system cannot touch: not one of
    // its eleven effect classes references ShipMovement or DamagableResource.
    // The reason is plumbing rather than intent - a ModuleEffect is handed
    // Unit.Data and never the component - which is what ModuleForgeUnits
    // solves.
    //
    // Same arithmetic model as the weapon effects on purpose (Add/Multiply,
    // Constant/FromOriginal, a FloatSeries for levels), so there is one thing
    // to learn across the whole mod.
    //
    // WRITES GO THROUGH ModuleForgeShipStats, NEVER DIRECTLY. Ship fields are
    // live and permanent - nothing rebuilds them the way a weapon is rebuilt
    // from its template - so a direct write would compound on every stat
    // recalculation and would never come back when the module was removed.
    [Serializable]
    public class ShipStatEffect
        : ModuleEffect, IHasDescriptionForUnit, ModuleForgeLive.IGated
    {
        public string targetProperty = ModuleForgeShipStats.MaxSpeed;

        public bool multiply;
        public string deltaMode = "constant";   // constant | fromoriginal

        public FloatSeries value;

        [NonSerialized] private Unit.Data _owner;

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        private float ValueForLevel
        {
            get { return value.GetElement(Level - 1); }
        }

        private bool FromOriginal
        {
            get
            {
                string m = (deltaMode ?? "").Trim().ToLowerInvariant();
                return m == "fromoriginal" || m == "original";
            }
        }

        public override void OnInstalled(Unit.Data unit)
        {
            base.OnInstalled(unit);
            _owner = unit;
            Push(unit);
            ModuleForgeLive.Track(this, unit);
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            base.OnUninstalled(unit);
            ModuleForgeLive.Untrack(this);
            Release(unit);
        }

        // Unpowered or disconnected is the same thing as not being there, which
        // is what the game does with every stock module - see ModuleForgeLive.
        // This is the one a player would notice first: a speed or dash stat off
        // a card sitting dark in the corner of the grid.
        public void OnModuleLiveChanged(Unit.Data unit, bool live)
        {
            if (live)
            {
                _owner = unit;
                Push(unit);
            }
            else
            {
                Release(unit);
            }
        }

        // The one owner of the teardown, so uninstall and the powered gate
        // cannot drift apart.
        private void Release(Unit.Data unit)
        {
            // Withdraw rather than "subtract what we added": the accumulator
            // recomputes from the captured base, so removal restores the exact
            // factory value even when several modules touched the same stat.
            ModuleForgeShipStats.Withdraw(unit ?? _owner, this);
            _owner = null;
        }

        // A BoosterCore raises Module.Level with no install/uninstall, so this
        // is the only place a level change can be noticed. Re-publishing at the
        // new level replaces our previous contribution (the accumulator keys by
        // effect instance) rather than stacking a second one.
        public override void OnRecalculateUnitStats(Unit.Data unit)
        {
            base.OnRecalculateUnitStats(unit);
            _owner = unit;
            Push(unit);
        }

        private void Push(Unit.Data unit)
        {
            if (unit == null)
                return;

            float v = ValueForLevel;

            if (multiply)
            {
                // Multiply is the natural way to say "+50% speed" without
                // knowing the ship's base number. 0 would wipe the stat out, so
                // it is treated as "no change" rather than "delete the stat" -
                // a module that silently stopped the ship moving is never what
                // anyone meant.
                ModuleForgeShipStats.Publish(
                    unit, this, targetProperty, 0f, (v == 0f) ? 1f : v);

                return;
            }

            if (FromOriginal)
            {
                // A share of the ship's own base, so 0.25 is "+25%" and two
                // copies of a module each give the same amount.
                float bas = ModuleForgeShipStats.BaseOf(unit, targetProperty);

                ModuleForgeShipStats.Publish(
                    unit, this, targetProperty, v * bas, 1f);

                return;
            }

            ModuleForgeShipStats.Publish(unit, this, targetProperty, v, 1f);
        }

        // Every field - Module's constructor Clones each effect off the
        // ModuleData, so the clone is what actually runs and a missing line
        // here is an invisible half-configured module.
        public override ModuleEffect Clone()
        {
            return new ShipStatEffect
            {
                targetProperty = targetProperty,
                multiply = multiply,
                deltaMode = deltaMode,
                value = value
            };
        }

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            string label = TextFormatter.ColoredText(
                TextFormatter.capsColor,
                ModuleForgeShipStats.Label(targetProperty));

            float v = ValueForLevel;

            string change = multiply
                ? "x" + Fmt(v)
                : (v < 0f ? "" : "+") +
                  (FromOriginal ? Fmt(100f * v) + "%" : Fmt(v));

            Unit.Data data =
                (unit != null) ? unit.ComponentData : _owner;

            if (isInstalled && data != null)
            {
                float now = ModuleForgeShipStats.Current(data, targetProperty);
                float bas = ModuleForgeShipStats.BaseOf(data, targetProperty);

                properties.Add(new DisplayableProperty(
                    label, change, Fmt(bas), Fmt(now)));

                return;
            }

            properties.Add(new DisplayableProperty(label, change));
        }

        private static string Fmt(float v)
        {
            return v.ToString("0.##");
        }
    }
}
