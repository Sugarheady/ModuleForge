using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // Attaches the wind-up meter to the Shooter that is firing.
    //
    // ★ THIS IS THE FIRST AND ONLY THING THIS MOD PATCHES ON `Shooter`, and
    // that is what put windup at Tier 3 on the port list. Verified by grep
    // 2026-09-18 before writing it: every other mention of `Shooter` in this
    // assembly is a comment or a `GetComponentsInChildren<Shooter>` read.
    //
    // A POSTFIX ON `Shooter.Shoot`, AND DELIBERATELY WITHOUT THE
    // `FireRatePassed` DISCRIMINATOR that a charging feature would need. That
    // check answers "did the shot really happen", which matters when you are
    // spending something per shot - Weapon Forge's overheat once charged 64
    // heat a second while firing nothing because a skipping prefix left its
    // postfix running.
    //
    // This postfix spends nothing. Its only job is to make sure the meter
    // EXISTS and knows its options; the meter then decides for itself, every
    // frame, whether the trigger is down, whether the owner can pay and whether
    // the gun is blocked. So attaching on a suppressed shot is harmless, and it
    // is actively useful - a weapon that fires nothing while you hold still
    // winds up.
    public static class ModuleForgeWindupPatch
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Windup");

        [HarmonyPatch(typeof(Shooter), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Shooter __instance)
            {
                if (!ModuleForgeWindup.Any || __instance == null)
                    return;

                try
                {
                    WeaponBase weapon = __instance.Weapon;

                    if (weapon == null)
                        return;

                    ModuleForgeWindup.Options o =
                        ModuleForgeWindup.Resolve(weapon);

                    if (o == null)
                        return;

                    ModuleForgeWound.For(__instance, o);
                }
                catch (Exception e)
                {
                    Log.LogError("Attaching the wind-up meter failed: " + e);
                }
            }
        }

        // ★ THE GADGET DETECTOR, and it is the only way to answer this at all.
        //
        // A module cannot refuse a gadget the way a weapon file can - the
        // player chose the slot. But `ModuleForgeKills.ShotFired` is raised
        // from the `WeaponBase.DoShoot` prefix, which every shot passes through
        // INCLUDING a gadget's, while a gadget never touches `Shooter`. So a
        // weapon that fires while carrying no meter, on a run where a windup
        // module is installed, is exactly the silent case - and it is worth one
        // line in the log rather than an evening wondering.
        //
        // Registered from the plugin rather than here, because it is an event
        // subscription and not a Harmony patch.
        public static void OnShotFired(WeaponBase weapon)
        {
            if (!ModuleForgeWindup.Any || weapon == null)
                return;

            try
            {
                // Only interesting if a windup module would otherwise apply to
                // this gun - otherwise every gadget in the game would warn.
                if (ModuleForgeWindup.Resolve(weapon) == null)
                    return;

                Unit owner = weapon.Owner;

                if (owner == null)
                    return;

                // Does ANY Shooter on this unit currently hold this weapon? A
                // gadget's weapon is held by no Shooter at all.
                foreach (Shooter s in owner.GetComponentsInChildren<Shooter>(true))
                {
                    if (s != null && ReferenceEquals(s.Weapon, weapon))
                        return;
                }

                ModuleForgeWindup.NoteShotWithoutShooter(weapon);
            }
            catch (Exception e)
            {
                Log.LogError("The windup gadget check failed: " + e);
            }
        }
    }

    // The wind-up itself. Named for the thing it is rather than for the block,
    // so it cannot be confused with `ModuleForgeWindup` (the registry) at a
    // glance in a stack trace.
    public class ModuleForgeWound : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Windup");

        private Shooter _shooter;
        private ModuleForgeWindup.Options _cfg;

        private float _spin;               // 0..1, the raw wind-up
        private float _lastFiringAt = -999f;

        private WeaponBase _weapon;        // whoever we last published to
        private int _lastStep = -1;
        private int _lastPellets = -1;
        private bool _wasFull;
        private bool _shown;

        // True while we are holding contributions. Without it an idle weapon
        // publishes-then-withdraws every frame for the rest of the run - an
        // Entry and a HUD line, torn down sixty times a second on a gun nobody
        // is firing. Weapon Forge shipped exactly that regression once.
        private bool _active;

        // "It has been fully wound since it was last empty." `lostSfx` cannot
        // ride `_wasFull`: that goes false on the frame the meter LEAVES full,
        // which is the frame the wind-down starts - so by the time it reaches
        // zero there is nothing left saying it had ever been full, and the
        // sound could never play. A real bug on the weapon side, ported with
        // its fix rather than its shape.
        private bool _reachedFull;

        public static ModuleForgeWound For(
            Shooter shooter, ModuleForgeWindup.Options cfg)
        {
            var w = shooter.gameObject.GetComponent<ModuleForgeWound>();

            if (w == null)
                w = shooter.gameObject.AddComponent<ModuleForgeWound>();

            w._shooter = shooter;
            w._cfg = cfg;

            return w;
        }

        // 0..1 after the ramp shape is applied.
        public float Fraction()
        {
            if (_cfg == null)
                return 0f;

            if (_cfg.ramp != ModuleForgeWindup.Stepped || _cfg.steps <= 1)
                return Mathf.Clamp01(_spin);

            // Floor, so a step is only awarded once fully reached - and at
            // exactly 1 this still lands on 1 (Floor(1 * 3) / 3 == 1).
            return Mathf.Floor(Mathf.Clamp01(_spin) * _cfg.steps) / _cfg.steps;
        }

        private void Update()
        {
            if (_cfg == null || _shooter == null)
                return;

            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Log.LogError("Wind-up tick failed: " + e);
            }
        }

        private void Tick()
        {
            WeaponBase weapon = _shooter.Weapon;

            // A CLUSTER REFRESH BUILDS A NEW `WeaponBase`, and a weapon swap
            // mid-run is a different gun entirely - so withdraw from the old
            // one and start again rather than leaving a contribution crediting
            // a weapon nobody is holding.
            if (!ReferenceEquals(weapon, _weapon))
            {
                Withdraw();
                _weapon = weapon;
                _spin = 0f;
                _lastStep = -1;
                _lastPellets = -1;
                _wasFull = false;
                _reachedFull = false;
            }

            if (weapon == null)
                return;

            // THE MODULE MAY HAVE BEEN PULLED, UNPOWERED OR MOVED. The meter
            // lives on the Shooter, not on the module, so nothing tears it off
            // - it has to re-ask. `Resolve` returning null is exactly the
            // "disconnected" case, and letting the wind-down run rather than
            // snapping to zero is the kinder reading of pulling a card.
            ModuleForgeWindup.Options live = ModuleForgeWindup.Resolve(weapon);

            if (live != null)
                _cfg = live;

            bool armed = live != null;

            // SUSTAINED FIRE, as the game itself defines firing: the trigger is
            // down, the owner can pay, and nothing is blocking the Shooter. An
            // empty gun does not wind up, and a jam or a stun costs you the
            // wind-up you had.
            bool firing =
                armed &&
                _shooter.IsShooting &&
                _shooter.OwnerHasResource &&
                !ModuleForgeWindup.IsBlocked(_shooter);

            // WHY IT STOPPED CLIMBING - said, not guessed at. R20 test 246:
            // "never gets to 100%". The code reaches 1 after `windUpTime` of
            // held trigger, so a meter that stalls short of full is one of the
            // two things this line names: the gun could not pay for its next
            // shot (and a wound-up gun spends faster, so one that cannot keep
            // up with its own new rate hovers below full) or the Shooter was
            // blocked. Once per gun per run; diagnostic, so it cannot throw.
            if (armed && _shooter.IsShooting && !firing)
                NoteStall(weapon);

            if (firing)
            {
                _lastFiringAt = Time.time;

                _spin = (_cfg.windUpTime > 0.0001f)
                    ? _spin + Time.deltaTime / _cfg.windUpTime
                    : 1f;
            }
            else if (Time.time - _lastFiringAt >= _cfg.grace)
            {
                _spin = (_cfg.windDownTime > 0.0001f)
                    ? _spin - Time.deltaTime / _cfg.windDownTime
                    : 0f;
            }

            _spin = Mathf.Clamp01(_spin);

            // A COLD, IDLE WEAPON DOES NOTHING AT ALL. `_active` is still true
            // on the frame the meter REACHES zero, which is what lets the
            // wind-down finish - the HUD line is released and `lostSfx` gets
            // its chance - before this starts skipping.
            if (_spin <= 0.0001f && !_active)
                return;

            // FEEDBACK FIRST, ABOVE EVERY GUARD BELOW IT. House rule, and it is
            // this exact subsystem that taught it: overheat's repaint sat below
            // an early return taken on every frame you were firing, so the ramp
            // colour only ever updated once you STOPPED. Announcing is a pure
            // read of `_spin`.
            Announce();
            Apply(weapon);
        }

        // ------------------------------------------------------------------
        // The rewards
        // ------------------------------------------------------------------

        private void Apply(WeaponBase weapon)
        {
            float f = Fraction();

            // Nothing left to give: hand the weapon back completely. This is
            // NOT the same as publishing a multiplier of 1 - withdrawing lets
            // the table forget its captured base, so a module augmentation that
            // legitimately changes the rate between engagements is picked up
            // next time instead of being overwritten by a stale number.
            if (f <= 0.0001f)
            {
                Withdraw();
                return;
            }

            _active = true;

            if (_cfg.rewards.Publish(weapon, ModuleForgeWindup.Key, f))
                Redress(weapon);
        }

        // CHANGING `ProjectileCount` AT RUNTIME IS COSMETIC ON A BULLET AND A
        // CRASH ON A BEAM. `WeaponBase.DoShoot` indexes
        // `muzzleParticleSystemInstances` and GUARDS it, so extra pellets
        // simply get no flash. **`HitscanWeapon.OnBarrelMoved` indexes
        // `visualsInstances[num]` with no guard at all**, every frame the barrel
        // moves - so a beam whose count rose without its visuals being rebuilt
        // throws sixty times a second.
        //
        // `InitializeVisuals` is public and `HitscanWeapon` overrides it to
        // resize exactly that list. Called on every change of the ROUNDED
        // count, in BOTH directions: downward matters as much, because
        // `OnBarrelMoved` only walks as far as the current count and orphaned
        // visuals above it keep their last state - a beam frozen in mid-air
        // with `Firing` stuck true.
        private void Redress(WeaponBase weapon)
        {
            if (weapon == null)
                return;

            int now = Mathf.RoundToInt(weapon.ProjectileCount);

            if (now == _lastPellets)
                return;

            _lastPellets = now;

            try
            {
                weapon.InitializeVisuals();
            }
            catch (Exception e)
            {
                Log.LogError("Rebuilding the weapon visuals failed: " + e);
            }
        }

        private void Withdraw()
        {
            if (_weapon == null)
                return;

            ModuleForgeWeaponStats.Clear(_weapon, ModuleForgeWindup.Key);

            // The count is back to the weapon's own, so the visual list has to
            // come back with it - see Redress for why downward is the direction
            // that leaves a beam stuck on screen.
            if (_cfg != null && _cfg.rewards.pellets && _lastPellets >= 0)
                Redress(_weapon);

            if (_shown)
            {
                _shown = false;
                ModuleForgeBuffHud.Clear(this);
            }

            _active = false;
        }

        private void OnDestroy()
        {
            Withdraw();
        }

        // ------------------------------------------------------------------
        // Feedback
        // ------------------------------------------------------------------

        private void Announce()
        {
            float f = Fraction();
            bool full = f >= 0.9999f;

            if (full)
                _reachedFull = true;

            // The HUD line. `Show` only repaints when the words change, so a
            // smooth ramp costs one line per whole percent rather than one per
            // frame - which matters because the status feed plays a sound on
            // every entry it draws.
            //
            // ★★ NOTHING IS SHOWN AT ZERO, and that one missing guard WAS the
            // R19 alert flood (tests 246 and 274, "spamming like crazy ...
            // filling the whole left side of my screen"). In stepped mode the
            // fraction reads 0 until the first step - the first 2/3 of a
            // second of every wind-up, and the last third of every wind-down.
            // For that whole stretch this posted a "WOUND UP +0%" line and
            // `Apply` then saw f == 0, withdrew, and CLEARED it - the same
            // frame. Next frame: a new entry, a new row, a new chirp, and the
            // old row still fading out because `LogEntry.Hide` is async. Sixty
            // rows a second, twice per trigger pull.
            //
            // Weapon Forge's spinUp - the feature this was ported FROM - has
            // exactly this guard in its `Say` and never flooded. The port kept
            // the shape and dropped the line that made the shape safe.
            if (_cfg.showOnHud)
            {
                if (f <= 0.0001f)
                {
                    if (_shown)
                    {
                        _shown = false;
                        ModuleForgeBuffHud.Clear(this);
                    }
                }
                else
                {
                    // The FRACTION, not a percentage: the HUD's own Compose
                    // turns an amount into "+N%" by multiplying by 100. This
                    // passed Round(f * 100), so a full wind-up read "+10000%".
                    ModuleForgeBuffHud.Show(
                        this, null, new Color(0.55f, 0.85f, 1f, 1f),
                        "WOUND UP", Mathf.Round(f * 100f) / 100f, 0, 0f, false);

                    _shown = true;
                }
            }

            // A STEP was crossed.
            if (_cfg.ramp == ModuleForgeWindup.Stepped && _cfg.steps > 1)
            {
                int step = Mathf.RoundToInt(f * _cfg.steps);

                if (step != _lastStep)
                {
                    if (step > _lastStep && _lastStep >= 0 && !full)
                        Play(_cfg.stepSfx);

                    _lastStep = step;
                }
            }

            if (full && !_wasFull)
            {
                Play(_cfg.readySfx);
                NoteFull();
            }

            _wasFull = full;

            // LOST IT - the meter came all the way back down having been full.
            // Driven by `_reachedFull`, not by `_wasFull`; see the field.
            if (!full && _reachedFull && f <= 0.0001f)
            {
                _reachedFull = false;
                Play(_cfg.lostSfx);
            }
        }

        // ------------------------------------------------------------------
        // The two log lines that answer "did it wind all the way up?"
        // ------------------------------------------------------------------
        //
        // Swept at run entry by ModuleForgeDiagnosticGates (the `_said`
        // prefix), so a second run in one session says them again.
        private static readonly HashSet<string> _saidStall = new HashSet<string>();
        private static readonly HashSet<string> _saidFull = new HashSet<string>();

        private static string GunName(WeaponBase weapon)
        {
            try
            {
                return (weapon != null && weapon.TemplateData != null)
                    ? weapon.TemplateData.name
                    : "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private void NoteStall(WeaponBase weapon)
        {
            try
            {
                if (Fraction() >= 0.9999f || !_saidStall.Add(GunName(weapon)))
                    return;

                string why;

                if (ModuleForgeWindup.IsBlocked(_shooter))
                {
                    why = "the gun was BLOCKED (a jam, a stun or an overheat " +
                          "lockout) while you held the trigger, and a blocked gun " +
                          "loses its wind-up";
                }
                else
                {
                    Resource r = weapon.ResourceUsed;
                    float have = (r != null && _shooter.Unit != null)
                        ? _shooter.Unit.GetResource(r)
                        : 0f;

                    why = "the gun could not pay for its next shot (" +
                          (r != null ? r.name : "its resource") + " at " +
                          have.ToString("0.#") + ", each shot costs " +
                          weapon.Cost.ToString("0.#") + "). An empty gun does " +
                          "not wind up - and winding up raises the fire rate, " +
                          "so the gun spends faster the further it gets. One " +
                          "whose ammo cannot keep up with its own wound-up " +
                          "rate hovers short of full";
                }

                Log.LogInfo(
                    "windup: '" + GunName(weapon) + "' stopped climbing at " +
                    Mathf.RoundToInt(Fraction() * 100f) + "% with the trigger " +
                    "held - " + why + ". Said once per gun per run.");
            }
            catch (Exception)
            {
                // A diagnostic must never cost the ramp it is describing.
            }
        }

        private void NoteFull()
        {
            try
            {
                if (!_saidFull.Add(GunName(_weapon)))
                    return;

                Log.LogInfo(
                    "windup: '" + GunName(_weapon) + "' wound up to FULL (" +
                    _cfg.windUpTime.ToString("0.##") + "s of held trigger, " +
                    ModuleForgeWindup.RampWord(_cfg.ramp, _cfg.steps) + "). If " +
                    "a wind-up never gets here, the line saying why is just " +
                    "above this one. Said once per gun per run.");
            }
            catch (Exception)
            {
            }
        }

        private void Play(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return;

            try
            {
                AudioManager.PlaySfx(guid, transform.position);
            }
            catch (Exception)
            {
                // A cue must never cost the ramp it is announcing.
            }
        }
    }
}
