using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // HOLDING THE TRIGGER MAKES THE GUN BETTER.
    //
    // The module half of Weapon Forge's `spinUp`, and **spelled `windup` here on
    // purpose** - `spinup` is already a live alias in this mod for the game's
    // flat `WarmupTime`. See the header on ModuleForgeWindup for the full note;
    // the short version is that the collision was the author's call and the
    // answer is a build warning rather than a silent re-interpretation.
    //
    // SHIP grid vs WEAPON grid falls out of the game's own architecture, exactly
    // as it does for SlowEffect, ExecuteEffect and CritEffect:
    //
    //     Modify() was called  ->  a weapon's grid; `_weapon` records which.
    //                              Only that gun winds up.
    //     Modify() never came  ->  the ship grid; `_weapon` stays null and
    //                              EVERY weapon winds up.
    //
    // ---------------------------------------------------------------------
    // ★ TWO THINGS THE WEAPON VERSION NEVER HAD TO ANSWER
    // ---------------------------------------------------------------------
    //
    // **1. A MODULE CANNOT REFUSE A GADGET.** Weapon Forge's `spinUp` refuses a
    // gadget weapon at build time and says so in the build log, because the
    // file names its own slot. A module applies to whatever the player slots it
    // beside, so there is no build-time moment at which the question can even
    // be asked. Gadgets call `weapon.Fire(...)` directly and never touch
    // `Shooter`, so a windup module beside one is inert.
    //
    // The answer is a RUNTIME warning instead, and it is exact rather than a
    // guess: `ModuleForgeKills.ShotFired` is raised from the `WeaponBase
    // .DoShoot` prefix, which every shot including a gadget's passes through -
    // so "this weapon fired and no Shooter is holding it" is checkable at the
    // moment it happens. Plus this card line, which says it without needing the
    // log at all.
    //
    // **2. THE CARD HAS NO SINGLE NUMBER TO PRINT.** A ramping reward is a
    // clock plus up to seventeen multipliers. So the card prints the CLOCK
    // (what it costs you to get there) and then names the rewards, rather than
    // trying to reduce a wind-up to one figure. The alternative - printing only
    // the fire-rate reward because it is the default - would describe a
    // different module every time somebody switched a reward on.
    //
    // NO MUZZLE TINT, and that is a stated omission. The weapon side tints the
    // muzzle flash as the meter fills, through a layer table (`ForgeMuzzleTint`)
    // that this mod does not have. Building one is its own job with its own
    // traps - the twin's version shipped a lerp-chain bug that made a fully
    // wound gun show no heat at all, and then an allocation regression that
    // reached a shipped feature. The buff HUD is this mod's own readout and is
    // arguably the better fit anyway: a module applies to every gun, so a
    // per-weapon muzzle colour would be the more ambiguous signal.
    [Serializable]
    public class WindupEffect
        : ModuleEffect, IWeaponModifier, IHasDescriptionForUnit,
          ModuleForgeLive.IGated
    {
        // ---- the clocks -----------------------------------------------------
        // Seconds of sustained fire to reach full. Series, because "gets there
        // faster" is the most legible thing a level can buy - and it is the one
        // series here whose per-level change is NEGATIVE, like slow's amount.
        public FloatSeries windUpTime;

        public float windDownTime = 1f;
        public float grace = 0.25f;

        public int ramp = ModuleForgeWindup.Smooth;
        public int steps = 3;

        // ---- the rewards ----------------------------------------------------
        public bool rate = true;
        public float fireRateAtFull = 2f;
        public bool accuracy = true;
        public float accuracyAtFull = 1f;
        public float spreadAtFull = 1f;
        public bool damage;
        public float damageAtFull = 1.5f;
        public bool pellets;
        public float pelletsAtFull = 2f;
        public bool cheaper;
        public float costAtFull = 0.5f;
        public bool speed;
        public float speedAtFull = 1.5f;
        public bool range;
        public float rangeAtFull = 1.5f;
        public bool size;
        public float sizeAtFull = 1.5f;
        public bool burst;
        public float burstAtFull = 2f;
        public float burstDelayAtFull = 1f;
        public bool burn;
        public float burnAtFull = 2f;
        public bool explosion;
        public float explosionAtFull = 1.5f;
        public bool push;
        public float pushAtFull = 2f;
        public float knockbackAtFull = 1f;
        public bool warmup;
        public float warmupAtFull;
        public bool lifetime;
        public float lifetimeAtFull = 1.5f;

        // ---- feedback -------------------------------------------------------
        public bool showOnHud = true;
        public string readySfxGuid = "";
        public string stepSfxGuid = "";
        public string lostSfxGuid = "";

        // ---- runtime --------------------------------------------------------
        [NonSerialized] private WeaponBase _weapon;
        [NonSerialized] private bool _installed;
        [NonSerialized] private ModuleForgeWindup.Options _options;

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        // FloatSeries is a STRUCT in this game, so there is no null to check
        // for - an unspecified one is all zeroes, and EffectBuilder supplies the
        // non-zero default. Floored rather than allowed to reach 0: a wind-up
        // time of zero is legitimate (instant) but a NEGATIVE one from an
        // over-eager per-level change would wind the meter down while firing.
        private float WindUpTime
        {
            get { return Mathf.Max(0f, windUpTime.GetElement(Level - 1)); }
        }

        // Rebuilt on each Push rather than cached, because the clock is
        // per-level and the meter reads this object every frame rather than
        // being handed fresh numbers. A cached one would freeze the ramp at the
        // level the module was placed - the invisible-booster trap from inside.
        private ModuleForgeWindup.Options BuildOptions()
        {
            if (_options == null)
                _options = new ModuleForgeWindup.Options();

            _options.windUpTime = WindUpTime;
            _options.windDownTime = windDownTime;
            _options.grace = grace;
            _options.ramp = ramp;
            _options.steps = steps;

            ModuleForgeRewards r = _options.rewards;

            r.rate = rate;
            r.fireRateAtFull = fireRateAtFull;
            r.accuracy = accuracy;
            r.accuracyAtFull = accuracyAtFull;
            r.spreadAtFull = spreadAtFull;
            r.damage = damage;
            r.damageAtFull = damageAtFull;
            r.pellets = pellets;
            r.pelletsAtFull = pelletsAtFull;
            r.cheaper = cheaper;
            r.costAtFull = costAtFull;
            r.speed = speed;
            r.speedAtFull = speedAtFull;
            r.range = range;
            r.rangeAtFull = rangeAtFull;
            r.size = size;
            r.sizeAtFull = sizeAtFull;
            r.burst = burst;
            r.burstAtFull = burstAtFull;
            r.burstDelayAtFull = burstDelayAtFull;
            r.burn = burn;
            r.burnAtFull = burnAtFull;
            r.explosion = explosion;
            r.explosionAtFull = explosionAtFull;
            r.push = push;
            r.pushAtFull = pushAtFull;
            r.knockbackAtFull = knockbackAtFull;
            r.warmup = warmup;
            r.warmupAtFull = warmupAtFull;
            r.lifetime = lifetime;
            r.lifetimeAtFull = lifetimeAtFull;

            _options.showOnHud = showOnHud;
            _options.readySfx = readySfxGuid ?? "";
            _options.stepSfx = stepSfxGuid ?? "";
            _options.lostSfx = lostSfxGuid ?? "";

            return _options;
        }

        // ---- lifecycle -------------------------------------------------------

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

        // Unpowered or disconnected is the same thing as not being there - see
        // ModuleForgeLive. `OnInstalled` fires for a card dropped anywhere in
        // the grid, powered or not, while every seam that RUNS a module takes
        // ConnectedAndPoweredModules.
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
            _weapon = null;
            ModuleForgeWindup.Clear(this);

            // THE METER IS NOT TORN OFF, and it does not need to be. It lives
            // on the Shooter and re-asks `Resolve` every frame; with the
            // registry entry gone it stops winding up and runs its wind-down,
            // which withdraws every published contribution on the way. So
            // pulling the card gives the gun back over `windDownTime` rather
            // than snapping - the kinder reading, and the one that costs no
            // extra machinery.
            //
            // This is the opposite call from `contactDamage`, which HAS to tear
            // its controller off - and the difference is that a contact grant
            // has no expiry at all, so nothing would ever end it.
        }

        // Only ever called for a module in a WEAPON's cluster - that is the
        // whole scoping mechanism. Called again whenever the weapon is rebuilt
        // (a fresh WeaponBase instance), so it re-captures.
        public void Modify(WeaponBase weaponBase)
        {
            _weapon = weaponBase;
            Push();
        }

        // A BoosterCore beside this module raises Level with NO install or
        // uninstall, so OnInstalled never re-runs and the clock would be frozen
        // at the level it was placed.
        public override void OnRecalculateUnitStats(Unit.Data unit)
        {
            if (!_installed)
                return;

            ModuleForgeScope.SetOwner(this, unit);
            Push();
        }

        private void Push()
        {
            ModuleForgeWindup.Set(_weapon, this, BuildOptions());
        }

        // ---- the module's own card -------------------------------------------

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            // AT ZERO IT IS A DIFFERENT MODULE AND THE CARD SAYS SO. Reachable
            // by an ordinary per-level change rather than by typing 0 - a card
            // that buys a shorter wind-up per level eventually buys all of it -
            // so "0s of firing" would be technically true and read as a bug.
            float up = WindUpTime;

            properties.Add(new DisplayableProperty(
                TextFormatter.ColoredText(TextFormatter.capsColor, "WIND UP"),
                (up <= 0.0001f)
                    ? "instant - full from the first shot"
                    : (Fmt(up) + "s of firing, " +
                       ModuleForgeWindup.RampWord(ramp, steps))));

            properties.Add(new DisplayableProperty(
                "At full", Rewards()));

            properties.Add(new DisplayableProperty(
                "Winds down in",
                Fmt(windDownTime) + "s" +
                (grace > 0f ? (", after " + Fmt(grace) + "s off the trigger")
                            : "")));

            // SAID ON THE CARD, not just in the log. A module applies to every
            // gun it is connected to, and this is the one gun type it cannot
            // reach - the player has no other way to learn that.
            properties.Add(new DisplayableProperty(
                "Needs", "a held trigger (not gadgets)"));

            // Which grid a module sits in is the whole difference between "this
            // gun" and "all of them", and it is not guessable from the card.
            properties.Add(new DisplayableProperty(
                "Applies to",
                (_weapon != null) ? "this weapon" : "every weapon"));
        }

        private string Rewards()
        {
            var bits = new List<string>();

            if (rate) bits.Add("x" + Fmt(fireRateAtFull) + " fire rate");
            if (accuracy && accuracyAtFull > 0f)
                bits.Add(Mathf.RoundToInt(accuracyAtFull * 100f) +
                         "% less scatter");
            if (accuracy && Mathf.Abs(spreadAtFull - 1f) > 0.001f)
                bits.Add("x" + Fmt(spreadAtFull) + " spread");
            if (damage) bits.Add("x" + Fmt(damageAtFull) + " damage");
            if (pellets) bits.Add("x" + Fmt(pelletsAtFull) + " pellets");
            if (cheaper) bits.Add("x" + Fmt(costAtFull) + " cost");
            if (speed) bits.Add("x" + Fmt(speedAtFull) + " shot speed");
            if (range) bits.Add("x" + Fmt(rangeAtFull) + " range");
            if (size) bits.Add("x" + Fmt(sizeAtFull) + " shot size");
            if (burst) bits.Add("x" + Fmt(burstAtFull) + " burst");
            if (burn) bits.Add("x" + Fmt(burnAtFull) + " burn");
            if (explosion) bits.Add("x" + Fmt(explosionAtFull) + " explosion");
            if (push) bits.Add("x" + Fmt(pushAtFull) + " push");
            if (warmup) bits.Add("warmup to " + Fmt(warmupAtFull) + "s");
            if (lifetime) bits.Add("x" + Fmt(lifetimeAtFull) + " lifetime");

            return (bits.Count == 0)
                ? "nothing (no rewards set)"
                : string.Join(", ", bits.ToArray());
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
            return new WindupEffect
            {
                windUpTime = this.windUpTime,
                windDownTime = this.windDownTime,
                grace = this.grace,
                ramp = this.ramp,
                steps = this.steps,

                rate = this.rate,
                fireRateAtFull = this.fireRateAtFull,
                accuracy = this.accuracy,
                accuracyAtFull = this.accuracyAtFull,
                spreadAtFull = this.spreadAtFull,
                damage = this.damage,
                damageAtFull = this.damageAtFull,
                pellets = this.pellets,
                pelletsAtFull = this.pelletsAtFull,
                cheaper = this.cheaper,
                costAtFull = this.costAtFull,
                speed = this.speed,
                speedAtFull = this.speedAtFull,
                range = this.range,
                rangeAtFull = this.rangeAtFull,
                size = this.size,
                sizeAtFull = this.sizeAtFull,
                burst = this.burst,
                burstAtFull = this.burstAtFull,
                burstDelayAtFull = this.burstDelayAtFull,
                burn = this.burn,
                burnAtFull = this.burnAtFull,
                explosion = this.explosion,
                explosionAtFull = this.explosionAtFull,
                push = this.push,
                pushAtFull = this.pushAtFull,
                knockbackAtFull = this.knockbackAtFull,
                warmup = this.warmup,
                warmupAtFull = this.warmupAtFull,
                lifetime = this.lifetime,
                lifetimeAtFull = this.lifetimeAtFull,

                showOnHud = this.showOnHud,
                readySfxGuid = this.readySfxGuid,
                stepSfxGuid = this.stepSfxGuid,
                lostSfxGuid = this.lostSfxGuid
            };
        }
    }
}
