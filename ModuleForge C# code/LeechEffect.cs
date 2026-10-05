using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // Your hits heal you.
    //
    // SHIP grid vs WEAPON grid falls out of the game's own architecture rather
    // than needing a flag, exactly as it does for KillRewardEffect and
    // CritEffect. `WeaponFactory.Create` walks only the modules in a WEAPON's
    // cluster and calls `IWeaponModifier.Modify` on their effects, so:
    //
    //     Modify() was called  ->  we are in a weapon's grid, and `_weapon`
    //                              records which one. Only that gun drains.
    //     Modify() never came  ->  we are on the ship, `_weapon` stays null,
    //                              and EVERY weapon drains.
    //
    // The card line is IHasDescriptionForUnit and NOT IHasDescriptionForWeapon,
    // because `HoveredModuleInfo` calls BOTH if an effect implements both and
    // every line would print twice. ForWeapon is also not the "am I on a
    // weapon?" test it looks like - for a module connected to neither weapon it
    // falls back to the primary anyway. `_weapon` is that test.
    [Serializable]
    public class LeechEffect
        : ModuleEffect, IWeaponModifier, IHasDescriptionForUnit,
          ModuleForgeLive.IGated
    {
        // ---- how much ----------------------------------------------------
        // Share of the damage actually dealt. 0.15 = 15%.
        public FloatSeries fraction;

        // Flat healing per hit whatever the damage was. Either alone is a
        // complete config.
        public FloatSeries flat;

        // ---- limits ------------------------------------------------------
        // A series, so a level can buy SUSTAIN rather than size - which is the
        // more interesting of the two on a leech module.
        public FloatSeries maxPerSecond;

        public float perEnemyDelay;

        // Null = the player's own health pool, found from their
        // DamagableResource rather than guessed by name.
        public Resource resource;

        // ---- behaviour and cues ------------------------------------------
        public ModuleForgeLeech.HealOn healOn = ModuleForgeLeech.HealOn.Arrival;
        public bool orb = true;
        public string orbSprite = "area_zero_particle_tuff";
        public float orbFps = 14f;
        public float orbScale = 1f;
        public float orbSpeed = 22f;
        public float orbArc = 0.7f;
        public float orbSpin;
        public float orbLifetime = 2.5f;
        public bool orbHasColor;
        public Color orbColor = Color.white;
        public string orbColorLabel = "";
        public int maxOrbs = 14;
        public string sfxGuid = "";

        // ---- runtime -----------------------------------------------------
        [NonSerialized] private WeaponBase _weapon;
        [NonSerialized] private bool _installed;
        [NonSerialized] private ModuleForgeLeech.Options _options;

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        // FloatSeries is a STRUCT in this game, so there is no null to check
        // for - an unspecified one is all zeroes, and EffectBuilder is what
        // supplies a non-zero default.
        private float Val(FloatSeries s)
        {
            float v = s.GetElement(Level - 1);
            return (v < 0f) ? 0f : v;
        }

        private float Fraction { get { return Val(fraction); } }
        private float Flat { get { return Val(flat); } }
        private float Cap { get { return Val(maxPerSecond); } }

        // Built once and reused, because ResolveLook compares Options to tell
        // two modules apart. A fresh object per push would make one module look
        // like a crowd of disagreeing ones.
        private ModuleForgeLeech.Options Options
        {
            get
            {
                if (_options == null)
                {
                    _options = new ModuleForgeLeech.Options
                    {
                        healOn = healOn,
                        orb = orb,
                        orbSprite = orbSprite,
                        orbFps = orbFps,
                        orbScale = orbScale,
                        orbSpeed = orbSpeed,
                        orbArc = orbArc,
                        orbSpin = orbSpin,
                        orbLifetime = orbLifetime,
                        orbHasColor = orbHasColor,
                        orbColor = orbColor,
                        maxOrbs = maxOrbs,
                        sfxGuid = sfxGuid ?? ""
                    };

                    // Resolved here rather than in the builder because a Module
                    // CLONES each effect, so this instance is the copy that
                    // actually runs and its Options is built on demand. The
                    // lookup is cached in ForgeAssets, so several clones cost
                    // one scan.
                    if (orb)
                    {
                        _options.orbFrames =
                            ForgeAssets.ResolveSpriteFrames(orbSprite);

                        // A named fallback rather than "no orb": part_cyrcle_12
                        // is the orb that pops out of a white block, so a leech
                        // whose art went missing still looks like something
                        // being collected. Falling through to no orb would
                        // silently turn "heal on arrival" into "heal on hit".
                        if (_options.orbFrames == null ||
                            _options.orbFrames.Length == 0)
                        {
                            _options.orbFrames =
                                ForgeAssets.ResolveSpriteFrames("part_cyrcle_12");
                        }
                    }
                }

                return _options;
            }
        }

        // ---- lifecycle ---------------------------------------------------

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
        // Healing off a card the player can see is dark would be the least
        // forgivable of the twelve.
        //
        // `_weapon` is dropped on the way out for the same reason as crit's: a
        // weapon rebuilt without this module never calls `Modify` again, so the
        // capture would outlive the `WeaponBase` it names.
        public void OnModuleLiveChanged(Unit.Data unit, bool live)
        {
            ModuleForgeScope.SetOwner(this, unit);

            if (live)
                Engage();
            else
                Disengage();
        }

        // The one owner of each direction, so install, uninstall and the
        // powered gate cannot drift apart.
        private void Engage()
        {
            _installed = true;
            Push();
        }

        private void Disengage()
        {
            _installed = false;
            _weapon = null;
            ModuleForgeLeechCompat.Clear(this);
        }

        // Only ever called for a module in a WEAPON's cluster - that is the
        // whole scoping mechanism. Called again whenever the weapon is rebuilt
        // (a fresh WeaponBase instance), and Push moves the contribution across.
        public void Modify(WeaponBase weaponBase)
        {
            _weapon = weaponBase;
            Push();
        }

        // A BoosterCore beside this module raises Level with no install or
        // uninstall, so OnInstalled never re-runs and the share would be frozen
        // at the level it was placed. Re-pushing here is what makes a leech
        // module answer to levelling at all.
        public override void OnRecalculateUnitStats(Unit.Data unit)
        {
            if (!_installed)
                return;

            ModuleForgeScope.SetOwner(this, unit);
            Push();
        }

        // Keyed on `this`, so pushing again is idempotent - which it must be,
        // because OnRecalculateUnitStats runs on every stat recalculation.
        private void Push()
        {
            ModuleForgeLeechCompat.Set(
                _weapon, this, Fraction, Flat, Cap, perEnemyDelay, resource,
                Options);
        }

        // ---- the module's own card ---------------------------------------

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            string label = TextFormatter.ColoredText(
                TextFormatter.capsColor, "LEECH");

            if (Fraction > 0f)
            {
                properties.Add(new DisplayableProperty(
                    label, Pct(Fraction) + " of damage dealt"));
            }

            if (Flat > 0f)
            {
                properties.Add(new DisplayableProperty(
                    (Fraction > 0f) ? "Plus, per hit" : label,
                    "+" + Fmt(Flat)));
            }

            if (Fraction <= 0f && Flat <= 0f)
            {
                properties.Add(new DisplayableProperty(
                    label, "nothing yet - needs a share or a flat amount"));
            }

            // Which grid a module sits in is the whole difference between "this
            // gun" and "all of them", and a player cannot guess it from the
            // grid. Same line KillRewardEffect prints, for the same reason.
            properties.Add(new DisplayableProperty(
                "Applies to",
                (_weapon != null) ? "this weapon" : "every weapon"));

            properties.Add(new DisplayableProperty(
                "Heals",
                (resource != null) ? resource.SpriteTag : "your health"));

            // The cap is the number that decides whether this is balanced, so it
            // is never hidden - including when it is absent.
            properties.Add(new DisplayableProperty(
                "Max per second",
                (Cap > 0f) ? Fmt(Cap) : "no cap"));

            if (perEnemyDelay > 0f)
            {
                properties.Add(new DisplayableProperty(
                    "Same enemy, at most",
                    "once every " + Fmt(perEnemyDelay) + "s"));
            }

            properties.Add(new DisplayableProperty(
                "Arrives",
                (Options.EffectiveHealOn == ModuleForgeLeech.HealOn.Arrival)
                    ? "when the bubble reaches you"
                    : "the instant you hit"));

            // His standing rule: anything settable in JSON shows on the card,
            // presentation included - the card is the only place a player can
            // learn what a module does.
            List<string> cues = new List<string>();

            if (orb)
                cues.Add("a bubble flies back");

            if (!string.IsNullOrEmpty(sfxGuid))
                cues.Add("sound");

            if (orbHasColor)
            {
                cues.Add(string.IsNullOrEmpty(orbColorLabel)
                    ? "colour" : ("colour " + orbColorLabel));
            }

            properties.Add(new DisplayableProperty(
                "Shows as",
                (cues.Count > 0)
                    ? string.Join(" + ", cues.ToArray())
                    : "nothing (the game has no damage numbers)"));
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
            return new LeechEffect
            {
                fraction = fraction,
                flat = flat,
                maxPerSecond = maxPerSecond,
                perEnemyDelay = perEnemyDelay,
                resource = resource,
                healOn = healOn,
                orb = orb,
                orbSprite = orbSprite,
                orbFps = orbFps,
                orbScale = orbScale,
                orbSpeed = orbSpeed,
                orbArc = orbArc,
                orbSpin = orbSpin,
                orbLifetime = orbLifetime,
                orbHasColor = orbHasColor,
                orbColor = orbColor,
                orbColorLabel = orbColorLabel,
                maxOrbs = maxOrbs,
                sfxGuid = sfxGuid
            };
        }
    }
}
