using System;
using System.Collections.Generic;

namespace ModuleForge
{
    // While equipped, turns piercing ON for the ship's projectiles and
    // gives it a cap of `pierceCap` enemies (caps stack across equipped
    // pierce modules). Optional per-pierce damage falloff and an explosion
    // on the final hit. Projectile weapons only.
    //
    // IHasDescriptionForUnit makes THIS module's own card show what it
    // contributes ("PIERCE +2"). The game's HoveredModuleInfo calls it for
    // any effect implementing the interface - no Harmony patch needed. (The
    // weapon's combined total is a separate line, see WeaponStatsPatch.)
    [Serializable]
    public class PierceModuleEffect : ModuleEffect, IHasDescriptionForUnit
    {
        public int pierceCap = 2;
        public float falloff;
        public bool explodeOnLimit;

        private bool _registered;
        private int _applied;
        private Unit.Data _owner;

        public override void OnInstalled(Unit.Data unit)
        {
            if (_registered) return;
            _owner = unit;
            _applied = pierceCap;
            ModuleForgeProjectile.AddPierce(unit, _applied, falloff, explodeOnLimit);
            _registered = true;
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            if (!_registered) return;
            ModuleForgeProjectile.RemovePierce(_owner ?? unit, _applied);
            _registered = false;
            _owner = null;
        }

        // Shows what THIS module contributes on its own card. When it's
        // actively installed, it shows the running total as an old > new
        // transition (like the BURN FREQ line), so you can see the module's
        // "+N" and where it takes the ship's total pierce.
        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            string label =
                TextFormatter.ColoredText(TextFormatter.capsColor, "PIERCE");

            if (_registered)
            {
                int total = ModuleForgeProjectile.PierceCapTotal;
                int without = total - _applied;
                if (without < 0) without = 0;

                properties.Add(new DisplayableProperty(
                    label, "+" + _applied, without.ToString(), total.ToString()));
            }
            else
            {
                properties.Add(new DisplayableProperty(label, "+" + pierceCap));
            }

            if (falloff > 0f)
            {
                properties.Add(new DisplayableProperty(
                    "Damage per pierce",
                    "-" + (falloff * 100f).ToString("0.#") + "%"));
            }

            if (explodeOnLimit)
                properties.Add(new DisplayableProperty("Explodes at the cap"));
        }

        public override ModuleEffect Clone()
        {
            return new PierceModuleEffect
            {
                pierceCap = this.pierceCap,
                falloff = this.falloff,
                explodeOnLimit = this.explodeOnLimit
            };
        }
    }
}
