using System;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // The live debuff, riding on the unit it slowed.
    //
    // Everything it changes is stored as an ORIGINAL on the way in and written
    // back on the way out, and every re-application recomputes from that
    // original rather than compounding - so a hundred hits cannot drift the
    // numbers and expiry always lands exactly where the unit started.
    //
    // **This component only ever exists when Weapon Forge is ABSENT.** With it
    // installed, `ModuleForgeSlowCompat` hands the slow to that mod's
    // `ForgeSlow.Apply` and this class is never added - because two components
    // each capturing "the original" is the permanent-debuff ratchet written up
    // at the top of ModuleForgeSlow.
    public class ModuleForgeSlowed : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Slow");

        // A DISTINCT CONTRIBUTOR KEY from Weapon Forge's "slow", and the choice
        // is about which way this fails rather than about tidiness. The stat
        // table MULTIPLIES contributions under different keys and OVERWRITES
        // one under the same key - so if the two ever became live at once (they
        // cannot today, but "cannot" has been wrong here before), two distinct
        // keys give a slow that is too strong for a moment, while one shared
        // key gives a withdrawal that restores a rate the other mod still
        // wants. Too strong is visible; a stuck fire rate is not.
        private const string StatKey = "moduleslow";

        private Rigidbody2D _rb;
        private Shooter _shooter;
        private WeaponBase _weapon;

        private float _damping;
        private float _angularDamping;
        private float _mass;

        private ModuleForgeSlow.Options _cfg;
        private int _stacks = 1;
        private float _endsAt;
        private bool _applied;
        private float _lastHitAt = -999f;

        public void Begin(Unit victim, ModuleForgeSlow.Options cfg)
        {
            _cfg = cfg;
            _stacks = 1;
            _endsAt = Time.time + cfg.duration;
            _lastHitAt = Time.time;

            _rb = victim.GetComponent<Rigidbody2D>();

            if (_rb == null)
                _rb = victim.GetComponentInChildren<Rigidbody2D>();

            _shooter = victim.GetComponentInChildren<Shooter>(true);

            if (_rb != null)
            {
                _damping = _rb.linearDamping;
                _angularDamping = _rb.angularDamping;
                _mass = _rb.mass;
            }

            // No fire-rate capture here: `ModuleForgeWeaponStats` owns the
            // baseline and captures once per weapon rather than once per
            // debuff, which is the part a second slow landing on the same enemy
            // would otherwise get wrong.
            if (_shooter != null && _shooter.Weapon != null)
                _weapon = _shooter.Weapon;

            Push();
        }

        public void Again(ModuleForgeSlow.Options cfg)
        {
            // A different module's slow taking over is fine; the newly resolved
            // settings simply become the live ones.
            _cfg = cfg;

            if (cfg.cooldown > 0f && Time.time - _lastHitAt < cfg.cooldown)
                return;

            _lastHitAt = Time.time;

            if (cfg.mode == ModuleForgeSlow.Ignore)
                return;

            if (cfg.mode == ModuleForgeSlow.Stack)
                _stacks = Mathf.Min(_stacks + 1, Mathf.Max(1, cfg.maxStacks));

            _endsAt = Time.time + cfg.duration;
            Push();
        }

        // How slow is this unit right now, as a fraction of normal? 1 means
        // "not slowed", which is also the answer for a unit with no debuff.
        //
        // Exists so the shot hook can ask a question of the SHOOTER rather than
        // keep its own bookkeeping. `ApplyLocally` puts the component on the
        // Unit's own GameObject and `Projectile.Owner` is that Unit, so this is
        // one GetComponent on a path that already has one.
        public static float FactorFor(Unit unit)
        {
            if (unit == null)
                return 1f;

            var live = unit.gameObject.GetComponent<ModuleForgeSlowed>();

            if (live == null || live._cfg == null || !live._cfg.slowShots)
                return 1f;

            // Expired but not yet cleaned up - Update removes it, and a shot
            // fired in between should not be slowed.
            if (Time.time >= live._endsAt)
                return 1f;

            return live.Factor();
        }

        // The one factor everything is derived from. Recomputed from scratch
        // every time, never accumulated.
        private float Factor()
        {
            float f = Mathf.Clamp(_cfg.amount, 0.01f, 1f);

            if (_cfg.mode == ModuleForgeSlow.Stack && _stacks > 1)
                f = Mathf.Pow(f, _stacks);

            return Mathf.Max(f, Mathf.Clamp(_cfg.floor, 0.01f, 1f));
        }

        private void Push()
        {
            float f = Factor();

            if (_rb != null)
            {
                // Terminal speed under a constant force is force/damping, so
                // dividing damping by the factor multiplies the speed by it. A
                // unit with no damping at all has no terminal speed to lower,
                // so give it some.
                if (_cfg.slowMove)
                {
                    float baseline = _damping > 0.01f ? _damping : 1f;
                    _rb.linearDamping = baseline / f;
                }

                if (_cfg.slowTurn)
                {
                    float baseline =
                        _angularDamping > 0.01f ? _angularDamping : 1f;
                    _rb.angularDamping = baseline / f;
                }

                // Lighter, so YOUR shots and blasts throw it further. Its own
                // locomotion is unaffected: PushMovement multiplies its force
                // by mass, so that cancels out exactly.
                if (_cfg.slowKnockback)
                    _rb.mass = Mathf.Max(0.05f, _mass * f);
            }

            // PUBLISHED, NOT ASSIGNED. `WeaponBase.FireRate` is a plain public
            // setter and this mod has already been bitten once by writing it
            // from a private captured base (see ModuleForgeWeaponStats): a
            // per-frame writer in the other mod turns "it moved, so re-capture"
            // into a ratchet. This only ever touches an ENEMY'S weapon, so it
            // would not have collided in play - but leaving one direct writer
            // standing is how the wrong example gets copied next time.
            if (_cfg.slowFire && _weapon != null)
            {
                ModuleForgeWeaponStats.Set(
                    _weapon, ModuleForgeWeaponStats.FireRate, StatKey, f);
            }

            if (_cfg.hasTint && !_applied)
                Tint();

            _applied = true;
        }

        private void Update()
        {
            if (Time.time < _endsAt)
                return;

            Restore();
            Destroy(this);
        }

        private void OnDestroy()
        {
            Restore();
        }

        private void Restore()
        {
            if (!_applied)
                return;

            _applied = false;

            try
            {
                if (_rb != null)
                {
                    if (_cfg.slowMove)
                        _rb.linearDamping = _damping;

                    if (_cfg.slowTurn)
                        _rb.angularDamping = _angularDamping;

                    if (_cfg.slowKnockback)
                        _rb.mass = _mass;
                }

                // The "is this still the same weapon?" check a hand-rolled
                // version would need is free here: the stat table keys on the
                // `WeaponBase` INSTANCE, so a cluster refresh is a key it has
                // never seen and a withdrawal against the old one cannot reach
                // the new gun.
                if (_cfg.slowFire && _weapon != null)
                {
                    ModuleForgeWeaponStats.Clear(
                        _weapon, ModuleForgeWeaponStats.FireRate, StatKey);
                }

                Untint();
            }
            catch (Exception e)
            {
                Log.LogError("Un-slowing a unit failed: " + e);
            }
        }

        // GOES THROUGH ModuleForgeUnitTint RATHER THAN PAINTING DIRECTLY, and
        // that is not cosmetic. Remembering each `sr.color` and writing it back
        // is correct for exactly one tint at a time: a second feature tinting
        // the same unit remembers the ALREADY-SLOWED colour and bakes it in
        // permanently when it expires. The tint owner keeps one captured base
        // per renderer for exactly this.
        private void Tint()
        {
            Unit unit = GetComponent<Unit>();

            if (unit == null)
                unit = GetComponentInParent<Unit>();

            // Duration is left open and cleared on Restore rather than timed
            // here: a slow can be refreshed or re-stacked, and a layer expiring
            // on its own schedule would flicker off mid-debuff.
            ModuleForgeUnitTint.Set(
                unit, ModuleForgeUnitTint.Slow, _cfg.tint, 0f, 0f,
                _cfg.tintStrength, _cfg.tintMode);
        }

        private void Untint()
        {
            Unit unit = GetComponent<Unit>();

            if (unit == null)
                unit = GetComponentInParent<Unit>();

            ModuleForgeUnitTint.Clear(unit, ModuleForgeUnitTint.Slow);
        }
    }
}
