using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // Finish anything your hits leave under a threshold.
    //
    // SHIP grid vs WEAPON grid falls out of the game's own architecture rather
    // than needing a flag, exactly as it does for CritEffect and
    // KillRewardEffect. `WeaponFactory.Create` walks only the modules in a
    // WEAPON's cluster and calls `IWeaponModifier.Modify` on their effects, so:
    //
    //     Modify() was called  ->  a weapon's grid; `_weapon` records which.
    //                              Only that gun executes.
    //     Modify() never came  ->  the ship grid; `_weapon` stays null and
    //                              EVERY weapon executes.
    //
    // The card line is IHasDescriptionForUnit and NOT IHasDescriptionForWeapon:
    // `HoveredModuleInfo` calls BOTH if an effect implements both, so
    // implementing both prints every line twice. ForWeapon is also not the "am I
    // on a weapon?" test it looks like - it falls back to the primary weapon for
    // a ship-grid module. `_weapon` is that test.
    [Serializable]
    public class ExecuteEffect
        : ModuleEffect, IWeaponModifier, IHasDescriptionForUnit,
          ModuleForgeLive.IGated
    {
        // ---- the thresholds ----------------------------------------------
        // A FRACTION of the victim's tank capacity. Series so a level can buy a
        // wider threshold, which is the interesting dial here.
        public FloatSeries percent;

        // An ABSOLUTE value. Whichever is satisfied first wins, so a module can
        // say "under 15% OR under 20 health" and catch the chaff and the big
        // one with one card.
        public FloatSeries flat;

        // Skip anything whose tank capacity is above this - the boss guard.
        // NOT a series: a cap that moves with level is a cap that quietly stops
        // protecting you at the level you were relying on it.
        public float maxTargetHealth;

        // ---- stacking (R20 252, his design) --------------------------------
        // Added to the threshold for EACH copy of this same module past the
        // first, anywhere it is live for the gun that hit. 0 = copies do not
        // stack (the strongest one counts), which is how every file written
        // before this reads. See ModuleForgeExecute.Groups.
        public float stackPercent;
        public float stackFlat;

        // ---- cues ---------------------------------------------------------
        public string sfxGuid = "";
        public bool hasColor;
        public Color color = new Color(1f, 0.3f, 0.3f, 1f);
        public float colorSeconds = 0.25f;
        public float colorPulse;
        public float tintStrength = 1f;
        public int tintMode = ModuleForgeUnitTint.Multiply;

        // ---- runtime -------------------------------------------------------
        [NonSerialized] private WeaponBase _weapon;
        [NonSerialized] private bool _installed;
        [NonSerialized] private ModuleForgeExecute.Options _options;

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        // FloatSeries is a STRUCT in this game, so there is no null to check
        // for - an unspecified one is all zeroes, and EffectBuilder supplies
        // the non-zero default.
        private float Percent
        {
            get
            {
                float v = percent.GetElement(Level - 1);
                return (v < 0f) ? 0f : v;
            }
        }

        private float Flat
        {
            get
            {
                float v = flat.GetElement(Level - 1);
                return (v < 0f) ? 0f : v;
            }
        }

        // Which copies stack together: copies of the same module FILE. The
        // ModuleData asset is the file (one per JSON, named for it), and a
        // second card of it on the grid is another Module of the same data.
        private string Group
        {
            get
            {
                try
                {
                    return (base.Module != null && base.Module.Data != null)
                        ? base.Module.Data.name
                        : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        // Rebuilt on each Push rather than cached like CritEffect's, because
        // BOTH thresholds are per-level and the engine reads the Options object
        // at damage time rather than being handed fresh numbers. A cached one
        // would freeze the thresholds at the level the module was placed, which
        // is the invisible-booster trap from the inside.
        private ModuleForgeExecute.Options BuildOptions()
        {
            if (_options == null)
                _options = new ModuleForgeExecute.Options();

            _options.percent = Percent;
            _options.flat = Flat;
            _options.maxTargetHealth = maxTargetHealth;
            _options.group = Group;
            _options.stackPercent = Mathf.Max(0f, stackPercent);
            _options.stackFlat = Mathf.Max(0f, stackFlat);
            _options.sfxGuid = sfxGuid ?? "";
            _options.hasColor = hasColor;
            _options.color = color;
            _options.colorSeconds = colorSeconds;
            _options.colorPulse = colorPulse;
            _options.tintStrength = tintStrength;
            _options.tintMode = tintMode;

            return _options;
        }

        // ---- lifecycle -----------------------------------------------------

        public override void OnInstalled(Unit.Data unit)
        {
            // Recorded before anything is pushed: a ship-grid contribution
            // reaches only the guns of the unit carrying it.
            ModuleForgeScope.SetOwner(this, unit);
            Engage();
            ModuleForgeLive.Track(this, unit);
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            ModuleForgeLive.Untrack(this);
            Disengage();
        }

        // Unpowered or disconnected is the same thing as not being there, which
        // is what the game does with every stock module - see ModuleForgeLive.
        // `OnInstalled` fires for a card dropped anywhere in the grid, powered
        // or not, while every seam that RUNS a module takes
        // ConnectedAndPoweredModules. Without this an unpowered execute module
        // would still delete things.
        public void OnModuleLiveChanged(Unit.Data unit, bool live)
        {
            ModuleForgeScope.SetOwner(this, unit);

            if (live)
                Engage();
            else
                Disengage();
        }

        private void Engage()
        {
            _installed = true;
            Push();
        }

        private void Disengage()
        {
            _installed = false;

            // Dropped on the way out on purpose: an unpowered module is no
            // longer in ConnectedAndPoweredModules, so the weapon the cluster
            // rebuilds without it never calls Modify again and the capture
            // would be a WeaponBase that no longer exists.
            _weapon = null;
            ModuleForgeExecute.Clear(this);
        }

        // Only ever called for a module in a WEAPON's cluster - that is the
        // whole scoping mechanism. Called again whenever the weapon is rebuilt
        // (a fresh WeaponBase instance), so it re-captures rather than assuming
        // the first one holds.
        public void Modify(WeaponBase weaponBase)
        {
            _weapon = weaponBase;
            Push();
        }

        // A BoosterCore beside this module raises Level with NO install or
        // uninstall, so OnInstalled never re-runs and the thresholds would be
        // frozen at the level it was placed. Re-pushing here is what makes an
        // execute module answer to levelling at all.
        public override void OnRecalculateUnitStats(Unit.Data unit)
        {
            if (!_installed)
                return;

            ModuleForgeScope.SetOwner(this, unit);
            Push();
        }

        // Keyed on `this`, so pushing again is idempotent - which it has to be,
        // because OnRecalculateUnitStats runs on every stat recalculation.
        private void Push()
        {
            ModuleForgeExecute.Set(_weapon, this, BuildOptions());
        }

        // ---- the module's own card -----------------------------------------

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            float pc = Percent;
            float fl = Flat;

            if (pc > 0f)
            {
                properties.Add(new DisplayableProperty(
                    TextFormatter.ColoredText(TextFormatter.capsColor, "EXECUTE"),
                    "below " + Pct(pc) + " health"));
            }

            if (fl > 0f)
            {
                properties.Add(new DisplayableProperty(
                    pc > 0f ? "Or below" : "EXECUTE",
                    Fmt(fl) + " health"));
            }

            // ★ WHAT A SECOND COPY DOES, and what the stack is worth right now.
            // R20 252: "if you stack multiples of those, does it increase the
            // cap ... can you update the description ... the execute range
            // that it's at?" It did not, and the card did not say so.
            int copies = 0;

            try
            {
                copies = ModuleForgeExecute.CopiesOn(
                    Group, unit != null ? unit.ComponentData : null);
            }
            catch (Exception)
            {
            }

            float sp = Mathf.Max(0f, stackPercent);
            float sf = Mathf.Max(0f, stackFlat);

            if (sp > 0f || sf > 0f)
            {
                string per = (sp > 0f ? "+" + Pct(sp) : "") +
                             (sp > 0f && sf > 0f ? " and " : "") +
                             (sf > 0f ? "+" + Fmt(sf) + " health" : "");

                properties.Add(new DisplayableProperty(
                    "Each extra copy", per));

                if (copies > 1)
                {
                    int extra = copies - 1;
                    string now = "";

                    if (pc > 0f || sp > 0f)
                        now = "below " + Pct(Mathf.Min(1f, pc + sp * extra)) + " health";

                    if (fl > 0f || sf > 0f)
                        now += (now.Length > 0 ? " or " : "below ") +
                               Fmt(fl + sf * extra) + " health";

                    properties.Add(new DisplayableProperty(
                        TextFormatter.ColoredText(TextFormatter.capsColor,
                            copies + " COPIES"), now));
                }
            }
            else if (copies > 1)
            {
                properties.Add(new DisplayableProperty(
                    copies + " copies", "do not add up - the strongest counts"));
            }

            // Said on the card and not only in the log, because it is the
            // difference between a finisher and a boss-melting module and a
            // player cannot see it any other way.
            properties.Add(new DisplayableProperty(
                "Skips targets over",
                maxTargetHealth > 0f
                    ? (Fmt(maxTargetHealth) + " max health")
                    : "nothing (no cap)"));

            // Which grid a module sits in is the whole difference between "this
            // gun" and "all of them", and it is not guessable from the card.
            properties.Add(new DisplayableProperty(
                "Applies to",
                (_weapon != null) ? "this weapon" : "every weapon"));
        }

        private static string Pct(float v)
        {
            return Mathf.RoundToInt(v * 100f) + "%";
        }

        private static string Fmt(float v)
        {
            return v.ToString("0.##");
        }

        // Every field, because `Module`'s constructor Clones each effect off the
        // ModuleData - so THIS is the copy the game actually runs, and anything
        // missing here silently reverts to the class default on every real
        // instance. `buffIndicator` was lost exactly that way once.
        public override ModuleEffect Clone()
        {
            return new ExecuteEffect
            {
                percent = this.percent,
                flat = this.flat,
                maxTargetHealth = this.maxTargetHealth,
                stackPercent = this.stackPercent,
                stackFlat = this.stackFlat,
                sfxGuid = this.sfxGuid,
                hasColor = this.hasColor,
                color = this.color,
                colorSeconds = this.colorSeconds,
                colorPulse = this.colorPulse,
                tintStrength = this.tintStrength,
                tintMode = this.tintMode
            };
        }
    }
}
