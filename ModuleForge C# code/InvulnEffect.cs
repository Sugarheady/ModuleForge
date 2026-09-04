using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // "After you are hit, you cannot be hit again for N seconds."
    //
    // A different shape from the ship STATS - it is a window rather than a
    // number on a component - so it gets its own effect rather than a row in
    // ShipStatEffect's table.
    //
    // Deliberately DOES NOT STACK across modules: two one-second modules give
    // one second, not two. Immunity windows multiply in value far faster than
    // they add, and three cheap cards should not add up to permanent
    // invulnerability. The controller takes the longest request.
    [Serializable]
    public class InvulnEffect : ModuleEffect, IHasDescriptionForUnit
    {
        public FloatSeries seconds;

        [NonSerialized] private Unit.Data _owner;

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        private float Seconds
        {
            get
            {
                float v = seconds.GetElement(Level - 1);
                return (v < 0f) ? 0f : v;
            }
        }

        public override void OnInstalled(Unit.Data unit)
        {
            base.OnInstalled(unit);
            _owner = unit;
            Push(unit);
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            base.OnUninstalled(unit);

            var inv = ModuleForgeInvuln.For(unit ?? _owner);

            if (inv != null)
                inv.Release();

            _owner = null;
        }

        // Also the level-boost path, and the re-arm after a Release: a recalc
        // happens whenever modules change, so a second module being removed
        // re-publishes this one's window.
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

            var inv = ModuleForgeInvuln.For(unit);

            if (inv != null)
                inv.Request(Seconds);
        }

        public override ModuleEffect Clone()
        {
            return new InvulnEffect { seconds = seconds };
        }

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            string label = TextFormatter.ColoredText(
                TextFormatter.capsColor, "INVULN");

            properties.Add(new DisplayableProperty(
                label, Seconds.ToString("0.##") + "S AFTER A HIT"));
        }
    }
}
