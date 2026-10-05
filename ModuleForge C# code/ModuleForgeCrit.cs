using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // Critical hits, contributed by grid modules.
    //
    // THIS IS A SECOND COPY OF A MECHANIC WEAPON FORGE ALSO HAS, and that was a
    // deliberate call rather than an oversight: a crit module has to work with
    // Weapon Forge uninstalled, and a damage-path patch is not something two
    // mods can share. So there are two engines and EXACTLY ONE OWNER at a time -
    // see ModuleForgeCritCompat. With Weapon Forge installed, this mod stops
    // patching and pushes its numbers into that mod's engine instead; the
    // pierce cap already works that way round.
    //
    // WHY THE DAMAGE PATH AND NOT THE SHOT. Every damage route in the game
    // funnels into ONE method - `DamagableResource.Damage(float)`.
    // `ProjectileCollided` -> `TakeDamage(Damage)` -> `Damage(amount)`;
    // `OnExplosion` -> `TakeDamage(IReadOnlyList<Damage>)` -> `Damage(amount)`;
    // `OnHitByHitscanWeapon` likewise. So a single prefix covers projectiles,
    // beams AND explosions with no per-weapon-type work.
    //
    // AND WHICH WEAPON IS RESPONSIBLE IS ALREADY SOLVED HERE.
    // `ModuleForgeKills.Credit` is set in a prefix and cleared in a finalizer
    // around all three of those routes, for the on-kill rewards. It is null for
    // a burn tick, a gas cloud or a cell collision - which is exactly the gate
    // crit wants, so damage-over-time never crits for free rather than by a rule
    // anyone had to write.
    //
    // THE GAME HAS NO FLOATING DAMAGE NUMBERS ANYWHERE (no DamageNumber, no
    // FloatingText, no DamageText - only unrelated *Popup classes). So a crit is
    // genuinely invisible unless the module asks for a cue, which is why
    // `impact` defaults ON: it is the one cue that needs no authoring.
    public static class ModuleForgeCrit
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Crit");

        // When does the dice get rolled?
        public enum Roll
        {
            // Once per thing damaged. A piercing shot can crit enemy 1 and not
            // enemy 2. The default, and the only mode that means anything on a
            // beam or an explosion.
            Hit,

            // Once per projectile, at Shoot. If it crits, everything that shot
            // touches crits. A shotgun rolls per PELLET here, because each
            // pellet is its own projectile.
            Shot,

            // Once per trigger pull, at DoShoot - so a whole shotgun blast is
            // all-crit or no-crit.
            Pull
        }

        // How a crit BEHAVES and how it LOOKS. Separate from the numbers because
        // the numbers add up across modules and these do not - see ResolveLook.
        public class Options
        {
            public Roll roll = Roll.Hit;

            // Above 100%, the excess becomes a chance at a SECOND crit stack
            // rather than being thrown away: 1.5 always crits and half the time
            // crits twice.
            public bool overflow = true;

            // A second, larger copy of the weapon's own impact burst. ON by
            // default: the only cue that needs no extra authoring. Projectile
            // hits only - a beam's spark and an explosion's flash are not
            // per-hit prefabs to borrow.
            public bool impact = true;
            public float impactScale = 2f;

            // A distinct sound. Resolved to a guid at build time.
            public string sfxGuid = "";

            // A brief tint on whatever got crit, through ModuleForgeUnitTint so
            // it can never bake a colour in permanently.
            public bool hasColor;
            public Color color = Color.white;
            public float colorSeconds = 0.25f;
            public float colorPulse;

            // See ModuleForgeUnitTint - 0..1, 1 is the full colour.
            public float tintStrength = 1f;

            // multiply (the default) or replace. A multiply can only
            // darken, so a pale flash on a dark enemy barely shows.
            public int tintMode = ModuleForgeUnitTint.Multiply;

            // Camera kick, using the weapon's own ShakePreset.
            public bool shake;

            // "This module asked for nothing unusual." Used only to decide which
            // of two modules' looks wins, so a module that set no presentation
            // never overrides one that did.
            public bool IsPlain
            {
                get
                {
                    return roll == Roll.Hit && overflow && impact &&
                           impactScale == 2f &&
                           string.IsNullOrEmpty(sfxGuid) &&
                           !hasColor && !shake;
                }
            }

            // Compared by VALUE, not by reference, and that matters: every
            // Module CLONES its effects, so two copies of the SAME module carry
            // two distinct Options objects. A reference comparison would report
            // those as two modules disagreeing about the look and log a clash
            // that is not one - and a log that lies is worse than no log.
            public bool SameAs(Options other)
            {
                return other != null &&
                       roll == other.roll &&
                       overflow == other.overflow &&
                       impact == other.impact &&
                       impactScale == other.impactScale &&
                       (sfxGuid ?? "") == (other.sfxGuid ?? "") &&
                       hasColor == other.hasColor &&
                       color == other.color &&
                       colorSeconds == other.colorSeconds &&
                       colorPulse == other.colorPulse &&
                       tintStrength == other.tintStrength &&
                       shake == other.shake;
            }
        }

        // Shared, never mutated - used when every contributor is plain.
        private static readonly Options _plain = new Options();

        public static Options Plain { get { return _plain; } }

        // ---- the registry ---------------------------------------------------

        private class Contribution
        {
            public float chance;
            public float multiplierBonus;
            public Options options;
        }

        // Weapon -> contributor -> its numbers. KEYED, and that is the whole
        // point: an effect re-pushes on every stat recalculation (that is how it
        // answers to a BoosterCore), so an ADD-style API would pile the same
        // module's chance on over and over until every weapon sat at a permanent
        // 100%. Setting under a key is idempotent, and two crit modules still
        // stack because each is its own key.
        private static readonly
            Dictionary<WeaponBase, Dictionary<object, Contribution>> _byWeapon =
                new Dictionary<WeaponBase, Dictionary<object, Contribution>>();

        // Contributors with no weapon to key on: a module in the SHIP grid.
        // `WeaponFactory.Create` walks only the modules in a WEAPON's cluster
        // when it applies IWeaponModifier, so a ship-grid module never learns a
        // weapon - and "all your guns crit" is what it should mean.
        private static readonly Dictionary<object, Contribution> _global =
            new Dictionary<object, Contribution>();

        // True once anything wants crit, so the damage prefix costs one bool
        // test in the overwhelmingly common case. That prefix runs on EVERY
        // point of damage dealt in the game.
        public static bool Any { get; private set; }

        // `weapon` may be NULL, meaning every weapon.
        //
        // The contributor is removed from wherever it was BEFORE being set, and
        // that is not tidiness: a module's weapon is a fresh `WeaponBase`
        // instance every time its cluster is rebuilt, so "set here and nowhere
        // else" is the only semantics that cannot leave a stale contribution
        // crediting a weapon that no longer exists.
        public static void Set(
            WeaponBase weapon, object key, float chance, float multiplierBonus,
            Options options)
        {
            if (key == null)
                return;

            Clear(key);

            Dictionary<object, Contribution> byKey;

            if (weapon == null)
            {
                byKey = _global;
            }
            else if (!_byWeapon.TryGetValue(weapon, out byKey))
            {
                byKey = new Dictionary<object, Contribution>();
                _byWeapon[weapon] = byKey;
            }

            byKey[key] = new Contribution
            {
                chance = chance,
                multiplierBonus = multiplierBonus,
                options = options ?? _plain
            };

            Any = true;
        }

        // Withdraws a contributor from every weapon and from the global
        // channel. A module that is removed must take its chance with it.
        public static void Clear(object key)
        {
            if (key == null)
                return;

            _global.Remove(key);

            List<WeaponBase> emptied = null;

            foreach (var pair in _byWeapon)
            {
                if (!pair.Value.Remove(key) || pair.Value.Count > 0)
                    continue;

                if (emptied == null)
                    emptied = new List<WeaponBase>();

                emptied.Add(pair.Key);
            }

            if (emptied != null)
            {
                for (int i = 0; i < emptied.Count; i++)
                    _byWeapon.Remove(emptied[i]);
            }

            if (_global.Count == 0 && _byWeapon.Count == 0)
                Any = false;
        }

        // ---- resolving one weapon ------------------------------------------

        // The base multiplier a crit has before any module adds to it. Modules
        // contribute a BONUS to this rather than an absolute, so that the same
        // number means the same thing whether this engine or Weapon Forge's is
        // in force (that mod's weapon-level "crit.multiplier" IS the absolute,
        // and module bonuses are added onto it). Two different words on purpose.
        public const float BaseMultiplier = 2f;

        public static bool Resolve(
            WeaponBase weapon, out float chance, out float multiplier,
            out Options options)
        {
            chance = 0f;
            multiplier = BaseMultiplier;
            options = _plain;

            float bonus = 0f;
            bool found = false;

            // ★ Only this weapon's OWN ship's modules: before R19 a ship-grid
            // crit module made ENEMY guns crit you too. See ModuleForgeScope.
            foreach (var pair in _global)
            {
                if (!ModuleForgeScope.Applies(pair.Key, weapon))
                    continue;

                Contribution c = pair.Value;
                chance += c.chance;
                bonus += c.multiplierBonus;
                found = true;
            }

            Dictionary<object, Contribution> byKey;

            if (weapon != null && _byWeapon.TryGetValue(weapon, out byKey))
            {
                foreach (Contribution c in byKey.Values)
                {
                    chance += c.chance;
                    bonus += c.multiplierBonus;
                    found = true;
                }
            }

            if (!found)
                return false;

            multiplier = BaseMultiplier + bonus;
            options = ResolveLook(weapon);

            return chance > 0f && multiplier > 1f;
        }

        // Chances ADD; a look cannot. So the first contributor that asked for
        // anything wins, and a disagreement is said out loud once rather than
        // one module's colour silently disappearing.
        private static bool _saidLookClash;

        private static Options ResolveLook(WeaponBase weapon)
        {
            Options chosen = null;
            bool clash = false;

            Dictionary<object, Contribution> byKey;

            // The weapon's own modules first: a module wired to this gun is a
            // more specific statement than one sitting on the ship.
            if (weapon != null && _byWeapon.TryGetValue(weapon, out byKey))
                Consider(byKey, weapon, false, ref chosen, ref clash);

            Consider(_global, weapon, true, ref chosen, ref clash);

            if (clash && !_saidLookClash)
            {
                _saidLookClash = true;
                Log.LogInfo(
                    "Two crit modules ask for a different LOOK (roll mode, " +
                    "colour, sound, shake). Chances and multipliers add up, " +
                    "but there can only be one look, so the first one wins - " +
                    "a module wired to the weapon ahead of one on the ship " +
                    "grid. Said once per run.");
            }

            return chosen ?? _plain;
        }

        private static void Consider(
            Dictionary<object, Contribution> byKey, WeaponBase weapon,
            bool global, ref Options chosen, ref bool clash)
        {
            foreach (var pair in byKey)
            {
                Contribution c = pair.Value;

                if (global && !ModuleForgeScope.Applies(pair.Key, weapon))
                    continue;

                if (c.options == null || c.options.IsPlain)
                    continue;

                if (chosen == null)
                    chosen = c.options;
                else if (!chosen.SameAs(c.options))
                    clash = true;
            }
        }

        // ---- the roll -------------------------------------------------------

        // How many crit stacks this hit earned. 0 = ordinary hit. Deliberately
        // identical arithmetic to Weapon Forge's, so a player learns one model.
        public static int Stacks(float chance, bool overflow)
        {
            if (chance <= 0f)
                return 0;

            if (!overflow)
                return UnityEngine.Random.value < Mathf.Min(chance, 1f) ? 1 : 0;

            int guaranteed = Mathf.FloorToInt(chance);
            float remainder = chance - guaranteed;

            if (UnityEngine.Random.value < remainder)
                guaranteed++;

            // A pathological chance (a module stack gone wrong) should not
            // produce a 40x hit silently.
            return Mathf.Min(guaranteed, 8);
        }

        // Stacks add the bonus: x2 twice is x3, not x4. Multiplying would make
        // three stacks x8 and stop being tunable.
        public static float Factor(int stacks, float multiplier)
        {
            if (stacks <= 0)
                return 1f;

            return 1f + stacks * (multiplier - 1f);
        }

        // ---- per-pull and per-shot decisions -------------------------------
        //
        // The damage path knows the WEAPON but not the shot, so a decision made
        // earlier has to be findable from there. A pull is synchronous inside
        // Shooter.Shoot for its first shot and the burst tail is bounded, so a
        // weapon-keyed entry stamped with the frame is exact enough and
        // allocates nothing per hit.
        private class PullDecision
        {
            public int stacks;
            public int frame;
        }

        private static readonly Dictionary<WeaponBase, PullDecision> _pull =
            new Dictionary<WeaponBase, PullDecision>();

        public static void RollPull(WeaponBase weapon, int stacks)
        {
            if (weapon == null)
                return;

            PullDecision d;

            if (!_pull.TryGetValue(weapon, out d))
            {
                d = new PullDecision();
                _pull[weapon] = d;
            }

            d.stacks = stacks;
            d.frame = Time.frameCount;
        }

        public static bool TryGetPull(WeaponBase weapon, out int stacks)
        {
            stacks = 0;

            PullDecision d;

            if (weapon == null || !_pull.TryGetValue(weapon, out d))
                return false;

            stacks = d.stacks;
            return true;
        }

        // Rides the projectile for Roll.Shot. An int on a component, so it
        // survives an Instantiate that copies the shot - unlike a config
        // reference, which would arrive null.
        public class ShotMark : MonoBehaviour
        {
            [NonSerialized] public int stacks;
            [NonSerialized] public bool rolled;
        }

        // ---- feedback -------------------------------------------------------

        public static void Feedback(
            Options options, WeaponBase weapon, Component victim, int stacks)
        {
            if (options == null || victim == null)
                return;

            try
            {
                if (!string.IsNullOrEmpty(options.sfxGuid))
                {
                    AudioManager.PlaySfx(
                        options.sfxGuid, victim.transform.position);
                }

                if (options.hasColor)
                {
                    // DamagableResource keeps its own Unit private, so find it
                    // the way that class does - upward, because health sits on
                    // a child on some prefabs.
                    Unit hurt = victim.GetComponentInParent<Unit>();

                    if (hurt != null)
                    {
                        ModuleForgeUnitTint.Set(
                            hurt, ModuleForgeUnitTint.Crit, options.color,
                            Mathf.Max(0.05f, options.colorSeconds),
                            options.colorPulse, options.tintStrength,
                            options.tintMode);
                    }
                }

                if (options.shake && weapon != null)
                    Shake(weapon);
            }
            catch (Exception e)
            {
                // Feedback is decoration. It must never cost the damage that
                // earned it - the same rule the diagnostics follow.
                Log.LogWarning("crit feedback failed: " + e.Message);
            }
        }

        // The weapon's own preset, so a crit shakes like a heavier version of
        // that gun rather than like a generic thump.
        //
        // BOTH the preset read and the shake call go through reflection, and
        // that is not incidental: `ShakePreset` and `ProCamera2DShake` live in
        // ProCamera2D, a third-party assembly neither mod references. Naming the
        // type in a field access drags the reference back in.
        private static object _shaker;
        private static System.Reflection.MethodInfo _shakeMethod;
        private static bool _shakeLooked;
        private static System.Reflection.PropertyInfo _shakeInstance;

        private static void Shake(WeaponBase weapon)
        {
            try
            {
                WeaponData data = weapon.TemplateData;

                if (data == null)
                    return;

                var field = HarmonyLib.AccessTools.Field(
                    typeof(WeaponData), "shakePreset");

                if (field == null)
                    return;

                object preset = field.GetValue(data);

                if (preset == null)
                    return;

                if (!_shakeLooked)
                {
                    _shakeLooked = true;

                    Type t = HarmonyLib.AccessTools.TypeByName(
                        "Com.LuisPedroFonseca.ProCamera2D.ProCamera2DShake");

                    if (t != null)
                    {
                        _shakeInstance =
                            HarmonyLib.AccessTools.Property(t, "Instance");

                        // Resolve the overload by the preset's own type, since
                        // we cannot name it at compile time.
                        _shakeMethod = HarmonyLib.AccessTools.Method(
                            t, "Shake", new Type[] { preset.GetType() });
                    }

                    if (_shakeInstance == null || _shakeMethod == null)
                    {
                        Log.LogInfo(
                            "Camera shake is not reachable, so a crit " +
                            "module's \"shake\" does nothing. Everything " +
                            "else works.");
                    }
                }

                if (_shakeInstance == null || _shakeMethod == null)
                    return;

                // Re-read each time rather than latching the singleton: a
                // cached Instance that went null on a scene change would kill
                // shake for the rest of the session with nothing in the log.
                _shaker = _shakeInstance.GetValue(null, null);

                if (_shaker != null)
                    _shakeMethod.Invoke(_shaker, new object[] { preset });
            }
            catch (Exception)
            {
                // No camera in this scene, or no preset. Nothing to do.
            }
        }

        // The bigger impact burst. Projectile hits only.
        public static void BigImpact(
            Options options, Projectile shot, Vector3 at)
        {
            if (options == null || !options.impact || shot == null)
                return;

            try
            {
                GameObject prefab = shot.ImpactBehaviour.destroyEffect;

                if (prefab == null)
                    return;

                GameObject extra = UnityEngine.Object.Instantiate(
                    prefab, at, Quaternion.identity);

                float s = Mathf.Max(1.05f, options.impactScale);
                extra.transform.localScale *= s;

                // Particles ignore transform scale for their own quads - the
                // size lives on startSize - so scale the systems too or the
                // burst is the same size in a bigger empty object.
                ParticleSystem[] systems =
                    extra.GetComponentsInChildren<ParticleSystem>(true);

                for (int i = 0; i < systems.Length; i++)
                {
                    var main = systems[i].main;
                    main.startSizeMultiplier = main.startSizeMultiplier * s;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("crit impact burst failed: " + e.Message);
            }
        }

        // Modules are NOT uninstalled when the ship is destroyed, so a run
        // boundary has to drop everything and let them re-register - the same
        // reason ModuleForgeBurn resets there.
        public static void Reset()
        {
            _byWeapon.Clear();
            _global.Clear();
            _pull.Clear();
            _saidLookClash = false;
            Any = false;
        }
    }
}
