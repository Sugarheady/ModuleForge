using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // The weapon stats the game's own ModifyWeaponProperty cannot reach.
    //
    // WHY THIS EXISTS AT ALL. `ModifyWeaponProperty.TargetProperty` is a fixed
    // enum of twelve, baked into the game's assembly - so a module simply
    // cannot ask for anything outside it. `WeaponBase` exposes 33 settable
    // properties. This class is the same idea with a different table, and it
    // deliberately copies the stock effect's ARITHMETIC exactly (operation,
    // delta mode, level series) so authors only learn one model.
    //
    // WHAT IT ADDS TODAY:
    //
    //   PushForce   the shove applied to WHAT YOU HIT. Not to be confused with
    //               KnockbackForce, which is the recoil on your own ship and is
    //               one of the stock twelve. This is the one people mean by
    //               "knockback" and it was unreachable.
    //   WarmupTime  the minigun spin-up before a held weapon starts firing.
    //
    // Both are plain floats on WeaponBase, so adding a third later is a line in
    // the table plus a label.
    [Serializable]
    public class ExtraWeaponStatEffect
        : ModuleEffect, IWeaponModifier, IHasDescriptionForWeapon
    {
        public const string PushForce = "pushforce";
        public const string WarmupTime = "warmuptime";

        // How far in front of the ship a shot is born. Small, and very real
        // on a shotgun: every pellet spawns at the same point, so a longer
        // barrel pushes the whole cone forward and past your own hull.
        public const string BarrelLength = "barrellength";

        // Stored lowercased by the builder so the switch below never has to
        // care about spelling.
        public string targetProperty = PushForce;

        // Mirrors ModifyWeaponProperty.Operation / DeltaCalculationMode rather
        // than inventing new words. Kept as strings because the game's enums
        // are private to its own effect and duplicating them here would only
        // add a conversion.
        public bool multiply;                 // false = Add
        public string deltaMode = "constant"; // constant | fromoriginal | fromcurrent

        public FloatSeries value;

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

        // ---- the property table -------------------------------------------
        //
        // Current value off the LIVE weapon, base value off its TEMPLATE. Both
        // are needed: the stock effect's "fromOriginal" mode scales against the
        // template so two copies of a module each give the same +50%, while
        // "fromCurrent" compounds.
        private static float Current(WeaponBase w, string prop)
        {
            if (w == null)
                return 0f;

            switch (prop)
            {
                case WarmupTime:   return w.WarmupTime;
                case BarrelLength: return w.BarrelLength;
                default:           return w.PushForce;
            }
        }

        private static float Base(WeaponData d, string prop)
        {
            if (d == null)
                return 0f;

            switch (prop)
            {
                case WarmupTime:   return d.warmupTime;
                case BarrelLength: return d.barrelLength;
                default:           return d.pushForce;
            }
        }

        private static void Assign(WeaponBase w, string prop, float v)
        {
            if (w == null)
                return;

            // Neither stat means anything below zero: a negative warmup would
            // be read as "already warmed" and a negative push would suck the
            // victim toward the shot, which is a different feature entirely.
            if (v < 0f)
                v = 0f;

            switch (prop)
            {
                case WarmupTime:   w.WarmupTime = v; break;
                case BarrelLength: w.BarrelLength = v; break;
                default:           w.PushForce = v; break;
            }
        }

        public static string Label(string prop)
        {
            switch (prop)
            {
                case WarmupTime:   return "SPIN-UP";
                case BarrelLength: return "BARREL";
                default:           return "PUSH";
            }
        }

        // ---- the effect ----------------------------------------------------
        public void Modify(WeaponBase weaponBase)
        {
            if (weaponBase == null)
                return;

            float current = Current(weaponBase, targetProperty);
            float delta = Delta(weaponBase);

            Assign(weaponBase, targetProperty,
                   multiply ? current * delta : current + delta);
        }

        private float Delta(WeaponBase weapon)
        {
            float v = ValueForLevel;

            switch ((deltaMode ?? "").Trim().ToLowerInvariant())
            {
                case "fromoriginal":
                case "original":
                    return v * Base(
                        (weapon != null) ? weapon.TemplateData : null,
                        targetProperty);

                case "fromcurrent":
                case "current":
                    return v * Current(weapon, targetProperty);

                default:
                    return v;
            }
        }

        // EVERY FIELD, or the game silently runs a half-configured copy:
        // Module's constructor Clones each effect off the ModuleData, so the
        // clone is what actually runs. A missing line here is invisible in the
        // log and shows up as "the module does nothing".
        public override ModuleEffect Clone()
        {
            return new ExtraWeaponStatEffect
            {
                targetProperty = targetProperty,
                multiply = multiply,
                deltaMode = deltaMode,
                value = value
            };
        }

        // ---- the card line -------------------------------------------------
        public void GetDescription(
            WeaponBase weapon,
            bool isInstalled,
            List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            string label = TextFormatter.ColoredText(
                TextFormatter.capsColor, Label(targetProperty));

            float current = Current(weapon, targetProperty);
            float delta = Delta(weapon);

            // Percent for the two proportional modes, matching the stock
            // effect's own display (it prints 100 x value with a %). A value of
            // 0.5 in "fromOriginal" is +50%, and showing a bare "0.5" there
            // would read as half a unit of push.
            string change = multiply
                ? "x" + Fmt(ValueForLevel)
                : (ValueForLevel < 0f ? "" : "+") +
                  (IsPercent
                      ? Fmt(100f * ValueForLevel) + "%"
                      : Fmt(ValueForLevel));

            if (isInstalled)
            {
                // Installed: show where the weapon was and where it is now,
                // the same old > new shape the stock effect and BURN FREQ use.
                float without = multiply
                    ? (delta != 0f ? current / delta : current)
                    : current - delta;

                if (without < 0f)
                    without = 0f;

                properties.Add(new DisplayableProperty(
                    label, change, Fmt(without), Fmt(current)));

                return;
            }

            properties.Add(new DisplayableProperty(label, change));
        }

        private bool IsPercent
        {
            get
            {
                string m = (deltaMode ?? "").Trim().ToLowerInvariant();
                return !multiply && (m == "fromoriginal" || m == "original" ||
                                     m == "fromcurrent" || m == "current");
            }
        }

        private static string Fmt(float v)
        {
            return v.ToString("0.##");
        }
    }
}
