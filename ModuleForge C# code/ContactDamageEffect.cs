using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // Your SHIP damages what it touches.
    //
    // ---------------------------------------------------------------------
    // WHY THIS ONE IS SHIP-ONLY, unlike every other effect here
    // ---------------------------------------------------------------------
    //
    // Every other effect in this mod uses the free scoping trick: `Modify` being
    // called means a weapon's grid, never being called means the ship. This one
    // does not, and the reason is that **the thing being armed is the hull, not
    // a gun.** There is exactly one ship however many weapons are bolted to it,
    // so "only this weapon's hull is spiky" has no meaning.
    //
    // So a contact-damage module works identically in either grid, and the card
    // says so rather than leaving a player to test it. That is a deliberate
    // exception to the house pattern, written down because the NEXT reader will
    // otherwise assume the scoping was forgotten.
    //
    // Weapon Forge's version also has `who: "marked" | "enemies" | "all"` -
    // making the units a weapon TOUCHED dangerous, or every enemy in the game.
    // Those are not ported: `marked` needs a per-hit weapon path and is a
    // different feature, and `enemies` is a world rule that arms things the
    // player does not own, which is a downside card wanting its own design
    // rather than a flag on this one.
    [Serializable]
    public class ContactDamageEffect
        : ModuleEffect, IHasDescriptionForUnit, ModuleForgeLive.IGated
    {
        // ---- damage --------------------------------------------------------
        public FloatSeries damage;
        public Resource damageType;

        public bool scaleBySpeed;
        public float damagePerSpeed = 1f;
        public float minSpeed = 6f;
        public float maxDamage = 999f;

        // ---- shoving -------------------------------------------------------
        public float push;
        public float recoil;
        public float repeatDelay = 0.3f;

        // ---- how and when --------------------------------------------------
        public int detect = ModuleForgeContact.DetectSwept;
        public float radius;
        public bool whileBoosting;
        public bool ignoreSelfDamage;

        // ---- the tell ------------------------------------------------------
        public bool hasTint;
        public Color tint = Color.white;
        public float tintPulse;
        public float tintStrength = 1f;
        public int tintMode = ModuleForgeUnitTint.Multiply;

        // ---- runtime -------------------------------------------------------
        [NonSerialized] private Unit.Data _owner;
        [NonSerialized] private bool _installed;
        [NonSerialized] private ModuleForgeContact.Options _options;

        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Contact");

        [NonSerialized] private static bool _saidTwoContacts;

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        private float Damage
        {
            get
            {
                float v = damage.GetElement(Level - 1);
                return (v < 0f) ? 0f : v;
            }
        }

        private ModuleForgeContact.Options BuildOptions()
        {
            if (_options == null)
                _options = new ModuleForgeContact.Options();

            _options.damage = Damage;
            _options.damageType = damageType;
            _options.scaleBySpeed = scaleBySpeed;
            _options.damagePerSpeed = damagePerSpeed;
            _options.minSpeed = minSpeed;
            _options.maxDamage = maxDamage;
            _options.push = push;
            _options.recoil = recoil;
            _options.repeatDelay = repeatDelay;
            _options.detect = detect;
            _options.radius = radius;
            _options.whileBoosting = whileBoosting;
            _options.ignoreSelfDamage = ignoreSelfDamage;
            _options.hasTint = hasTint;
            _options.tint = tint;
            _options.tintPulse = tintPulse;
            _options.tintStrength = tintStrength;
            _options.tintMode = tintMode;

            return _options;
        }

        // ---- lifecycle -----------------------------------------------------

        public override void OnInstalled(Unit.Data unit)
        {
            _owner = unit;
            Engage();
            ModuleForgeLive.Track(this, unit);
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            ModuleForgeLive.Untrack(this);
            Disengage();
        }

        // ★ THE POWERED GATE IS WHAT KEEPS THIS FEATURE HONEST, and it matters
        // more here than on any other effect in the mod.
        //
        // Weapon Forge's twin shipped as a global rule armed by a file merely
        // existing - *"I loaded Kamikaze swarm and when I dash into enemies they
        // take damage now"* - because a build-time registration was mistaken for
        // live state. A module has no build-time registration; it has THIS, the
        // game's own "is this card connected and powered" answer. An unpowered
        // spiked hull is not a spiked hull.
        public void OnModuleLiveChanged(Unit.Data unit, bool live)
        {
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

        // DISARMING REVOKES rather than merely stopping. The grant has no
        // expiry, so a controller left on a ship whose module was pulled is a
        // module with no off switch - which is the other half of the bug the
        // Weapon Forge version had, beside the scoping.
        private void Disengage()
        {
            _installed = false;
            ModuleForgeContact.Disarm(this);
        }

        // A BoosterCore raises Level with no install or uninstall, so without
        // this the damage would freeze at the level the card was placed.
        public override void OnRecalculateUnitStats(Unit.Data unit)
        {
            if (!_installed)
                return;

            if (unit != null)
                _owner = unit;

            Push();
        }

        private void Push()
        {
            if (_owner == null)
                return;

            ModuleForgeContact.Arm(_owner, this, BuildOptions());

            if (!_saidTwoContacts &&
                ModuleForgeContact.CountFor(_owner) > 1)
            {
                _saidTwoContacts = true;

                Log.LogWarning(
                    "more than one CONTACT DAMAGE module is installed on the " +
                    "same ship. There is only one hull, so one set of numbers " +
                    "applies - the HIGHEST \"damage\" wins, and its push, " +
                    "recoil, radius, delay and tint come with it. They are not " +
                    "summed on purpose: combining them would mean choosing " +
                    "whose repeatDelay and whose radius too, which is a " +
                    "resolution pass for a case nobody asked for. Said once " +
                    "per run.");
            }
        }

        // ---- the module's own card -----------------------------------------

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            if (scaleBySpeed)
            {
                properties.Add(new DisplayableProperty(
                    TextFormatter.ColoredText(TextFormatter.capsColor, "RAM"),
                    Fmt(damagePerSpeed) + " per unit of speed"));

                properties.Add(new DisplayableProperty(
                    "Needs a speed of", Fmt(minSpeed) + " to hurt anything"));
            }
            else
            {
                properties.Add(new DisplayableProperty(
                    TextFormatter.ColoredText(TextFormatter.capsColor, "RAM"),
                    Fmt(Damage) + " on contact"));
            }

            // The one that decides whether this is a passive or a manoeuvre, and
            // a player cannot see it any other way.
            properties.Add(new DisplayableProperty(
                "Active",
                whileBoosting ? "only while boosting" : "always"));

            if (push != 0f)
            {
                properties.Add(new DisplayableProperty(
                    "Shoves them", Fmt(push)));
            }

            if (ignoreSelfDamage)
            {
                properties.Add(new DisplayableProperty(
                    "Crash damage", "suppressed while installed"));
            }

            // Said out loud because it is the exception to how every other
            // module here is scoped, and a player who has learned the rule would
            // otherwise reasonably expect the grid to matter.
            properties.Add(new DisplayableProperty(
                "Applies to", "your hull, in either grid"));
        }

        private static string Fmt(float v)
        {
            return v.ToString("0.##");
        }

        // Every field - `Module`'s constructor Clones each effect off the
        // ModuleData, so this is the copy the game actually runs.
        public override ModuleEffect Clone()
        {
            return new ContactDamageEffect
            {
                damage = this.damage,
                damageType = this.damageType,
                scaleBySpeed = this.scaleBySpeed,
                damagePerSpeed = this.damagePerSpeed,
                minSpeed = this.minSpeed,
                maxDamage = this.maxDamage,
                push = this.push,
                recoil = this.recoil,
                repeatDelay = this.repeatDelay,
                detect = this.detect,
                radius = this.radius,
                whileBoosting = this.whileBoosting,
                ignoreSelfDamage = this.ignoreSelfDamage,
                hasTint = this.hasTint,
                tint = this.tint,
                tintPulse = this.tintPulse,
                tintStrength = this.tintStrength,
                tintMode = this.tintMode
            };
        }
    }
}
