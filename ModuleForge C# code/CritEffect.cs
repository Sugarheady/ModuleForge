using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // A chance for your hits to do multiplied damage.
    //
    // SHIP grid vs WEAPON grid falls out of the game's own architecture rather
    // than needing a flag, exactly as it does for KillRewardEffect.
    // `WeaponFactory.Create` walks only the modules in a WEAPON's cluster and
    // calls `IWeaponModifier.Modify` on their effects, so:
    //
    //     Modify() was called  ->  we are in a weapon's grid, and `_weapon`
    //                              records which one. Only that gun crits.
    //     Modify() never came  ->  we are on the ship, `_weapon` stays null,
    //                              and EVERY weapon crits.
    //
    // WHY THE MULTIPLIER IS A "BONUS" AND NOT AN ABSOLUTE. A crit already does
    // `ModuleForgeCrit.BaseMultiplier` (x2) damage; this module adds to that. So
    // `multiplierBonus: 0.5` means x2.5 and `0` means a plain double-damage
    // crit. It has to work that way for the numbers to mean the same thing when
    // Weapon Forge is installed: there, the WEAPON's own `crit.multiplier` is
    // the absolute and module contributions are added on top of it. Two
    // different words for the two things, on purpose.
    //
    // The card line is IHasDescriptionForUnit and NOT IHasDescriptionForWeapon,
    // and that is not a coin toss: `HoveredModuleInfo` calls BOTH interfaces if
    // an effect implements both, so implementing both would print every line
    // twice. ForUnit is called for a module in either grid; ForWeapon is called
    // for a ship-grid module too (it falls back to the primary weapon), so it is
    // not the "am I on a weapon?" test it looks like. `_weapon` is that test.
    [Serializable]
    public class CritEffect
        : ModuleEffect, IWeaponModifier, IHasDescriptionForUnit
    {
        // ---- the numbers ------------------------------------------------
        // 0.15 = 15%. Series so a level can buy reliability. Values over 1 are
        // meaningful when `overflow` is on - see ModuleForgeCrit.Stacks.
        public FloatSeries chance;

        // Added to the x2 a crit already does.
        public FloatSeries multiplierBonus;

        // ---- behaviour and cues ------------------------------------------
        // Stored as the parsed values so nothing has to re-read a string per
        // hit. The whole set is handed to the engine as one Options object.
        public ModuleForgeCrit.Roll roll = ModuleForgeCrit.Roll.Hit;
        public bool overflow = true;
        public bool impact = true;
        public float impactScale = 2f;
        public string sfxGuid = "";
        public bool hasColor;
        public Color color = Color.white;
        public string colorLabel = "";
        public float colorSeconds = 0.25f;
        public float colorPulse;

        // 0..1, how hard the crit flash comes through. See
        // ModuleForgeUnitTint; 1 is the old look.
        public float tintStrength = 1f;

        public bool shake;

        // ---- runtime -----------------------------------------------------
        [NonSerialized] private WeaponBase _weapon;
        [NonSerialized] private bool _installed;
        [NonSerialized] private ModuleForgeCrit.Options _options;

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        // FloatSeries is a STRUCT in this game, so there is no null to check
        // for - an unspecified one is simply all zeroes, and EffectBuilder is
        // what supplies a non-zero default.
        private float Chance
        {
            get
            {
                float v = chance.GetElement(Level - 1);
                return (v < 0f) ? 0f : v;
            }
        }

        // Negative is allowed here and deliberately so: a downside module
        // ("+40% crit chance, but crits only do x1.5") is a legitimate design,
        // and the engine's own `multiplier > 1` gate stops it going absurd.
        private float MultiplierBonus
        {
            get { return multiplierBonus.GetElement(Level - 1); }
        }

        private float Multiplier
        {
            get { return ModuleForgeCrit.BaseMultiplier + MultiplierBonus; }
        }

        // Built once and reused, because ModuleForgeCrit.ResolveLook compares
        // Options by REFERENCE to tell two modules apart. A fresh object per
        // push would make one module look like a crowd of disagreeing ones.
        private ModuleForgeCrit.Options Options
        {
            get
            {
                if (_options == null)
                {
                    _options = new ModuleForgeCrit.Options
                    {
                        roll = roll,
                        overflow = overflow,
                        impact = impact,
                        impactScale = impactScale,
                        sfxGuid = sfxGuid ?? "",
                        hasColor = hasColor,
                        color = color,
                        colorSeconds = colorSeconds,
                        colorPulse = colorPulse,
                        tintStrength = tintStrength,
                        shake = shake
                    };
                }

                return _options;
            }
        }

        // ---- lifecycle ---------------------------------------------------

        public override void OnInstalled(Unit.Data unit)
        {
            _installed = true;
            Push();
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            _installed = false;
            _weapon = null;
            ModuleForgeCritCompat.Clear(this);
        }

        // Only ever called for a module in a WEAPON's cluster - that is the
        // whole scoping mechanism. Called again whenever the weapon is rebuilt
        // (a fresh WeaponBase instance), so it re-captures rather than assuming
        // the first one holds, and Push moves the contribution across.
        public void Modify(WeaponBase weaponBase)
        {
            _weapon = weaponBase;
            Push();
        }

        // A BoosterCore beside this module raises Level with no install or
        // uninstall, so OnInstalled never re-runs and the chance would be frozen
        // at the level it was placed. Re-pushing here is what makes a crit
        // module answer to levelling at all - the same reason
        // BurnRateModuleEffect and PierceModuleEffect override this.
        public override void OnRecalculateUnitStats(Unit.Data unit)
        {
            if (!_installed)
                return;

            Push();
        }

        // Keyed on `this`, so pushing again is idempotent - which it has to be,
        // because OnRecalculateUnitStats runs on every stat recalculation. An
        // ADD-style API here would drift a weapon to a permanent 100%.
        private void Push()
        {
            ModuleForgeCritCompat.Set(
                _weapon, this, Chance, MultiplierBonus, Options);
        }

        // ---- the module's own card ---------------------------------------

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            float ch = Chance;

            properties.Add(new DisplayableProperty(
                TextFormatter.ColoredText(TextFormatter.capsColor, "CRIT"),
                Pct(ch) + " for x" + Fmt(Multiplier)));

            // Over 100% is not a mistake and is not obvious, so say what it
            // buys rather than leaving a number that looks broken.
            if (ch > 1f)
            {
                properties.Add(new DisplayableProperty(
                    "Always crits",
                    overflow
                        ? ("plus " + Pct(ch - 1f) + " for a second one")
                        : "excess wasted (overflow off)"));
            }

            if (MultiplierBonus != 0f)
            {
                properties.Add(new DisplayableProperty(
                    "Crit damage",
                    (MultiplierBonus > 0f ? "+" : "") +
                    Fmt(MultiplierBonus) + " on the base x" +
                    Fmt(ModuleForgeCrit.BaseMultiplier)));
            }

            // Worth spelling out - which grid a module sits in is the whole
            // difference between "this gun" and "all of them", and a player
            // cannot guess that from the card.
            properties.Add(new DisplayableProperty(
                "Applies to",
                (_weapon != null) ? "this weapon" : "every weapon"));

            properties.Add(new DisplayableProperty("Rolled", RollWord));

            // The cues. His standing rule: anything settable in JSON shows on
            // the card, presentation included - the card is the only place a
            // player can learn what a module does.
            List<string> cues = new List<string>();

            if (impact)
                cues.Add("bigger impact");

            if (!string.IsNullOrEmpty(sfxGuid))
                cues.Add("sound");

            if (hasColor)
            {
                cues.Add(string.IsNullOrEmpty(colorLabel)
                    ? "colour" : ("colour " + colorLabel));
            }

            if (shake)
                cues.Add("shake");

            properties.Add(new DisplayableProperty(
                "Crit shows as",
                (cues.Count > 0)
                    ? string.Join(" + ", cues.ToArray())
                    : "nothing (the game has no damage numbers)"));
        }

        private string RollWord
        {
            get
            {
                switch (roll)
                {
                    case ModuleForgeCrit.Roll.Shot:
                        return "per shot (a pellet crits whole)";

                    case ModuleForgeCrit.Roll.Pull:
                        return "per trigger pull (all or nothing)";

                    default:
                        return "per hit";
                }
            }
        }

        private static string Pct(float v)
        {
            return (v * 100f).ToString("0.#") + "%";
        }

        private static string Fmt(float v)
        {
            return v.ToString("0.##");
        }

        // EVERY FIELD, or the game silently runs a half-configured copy:
        // Module's constructor Clones each effect off the ModuleData, so the
        // clone is what actually runs. A missing line here is invisible in the
        // log and shows up as "the module does nothing".
        public override ModuleEffect Clone()
        {
            return new CritEffect
            {
                chance = chance,
                multiplierBonus = multiplierBonus,
                roll = roll,
                overflow = overflow,
                impact = impact,
                impactScale = impactScale,
                sfxGuid = sfxGuid,
                hasColor = hasColor,
                color = color,
                colorLabel = colorLabel,
                colorSeconds = colorSeconds,
                colorPulse = colorPulse,
                tintStrength = tintStrength,
                shake = shake
            };
        }
    }
}
