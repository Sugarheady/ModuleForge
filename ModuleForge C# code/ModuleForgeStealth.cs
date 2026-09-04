using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // Drives Unit.Data.IsInvisible for stealth modules.
    //
    // THE MECHANIC IS ALREADY IN THE GAME AND FULLY WORKING - it is simply not
    // reachable by anything except the debug menu. `Vision.IsVisible` is
    // literally `return !component.ComponentData.IsInvisible`, and `AIAgent`
    // CLEARS AND REBUILDS its visibleEnemies list from Vision every update. So
    // going invisible does not merely stop new aggro: every enemy currently
    // hunting you drops you from its target list on its next pass.
    //
    // That pass is `Vision.refreshDelay`, which is 0.25s on 66 of the 68
    // prefabs that have one. Fast enough to feel responsive, slow enough that
    // you can see it happen - worth knowing before tuning any duration under a
    // quarter second, which would be invisible in both senses.
    //
    // SHOOTING DOES NOT REVEAL YOU, because Vision only ever checks the flag.
    // That is the whole reason `revealOnFire` exists and defaults ON: without
    // it, a cloak is not a stealth mechanic, it is an off switch for the enemy
    // AI.
    public class ModuleForgeStealth : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge.Stealth");

        // What a module asks for. Passed by the effect rather than held on it,
        // so the controller never needs to know about ModuleEffect at all.
        public class Spec
        {
            public string trigger = StealthEffect.Dash;
            public float seconds = 1f;
            public float idleSeconds = 2f;
            public bool revealOnFire = true;
            public float revealSeconds = 1f;
        }

        private readonly Dictionary<object, Spec> _specs =
            new Dictionary<object, Spec>();

        private Unit _unit;

        // One timestamp per event, shared by every spec. A spec is a pure
        // function of these, so adding a trigger is a case label rather than
        // another piece of state to keep in sync.
        private float _lastDash = -999f;
        private float _lastKill = -999f;
        private float _lastHit = -999f;
        private float _lastShot = -999f;
        private bool _boosting;

        // Only ever clear the flag if WE set it. The debug menu writes the same
        // field, and stomping it would make the mod look like it was fighting
        // the developer console.
        private bool _weSet;

        private bool _wired;

        public void Request(object owner, Spec spec)
        {
            if (owner == null || spec == null)
                return;

            _specs[owner] = spec;
            Wire();
        }

        public void Release(object owner)
        {
            if (owner == null)
                return;

            _specs.Remove(owner);

            if (_specs.Count == 0)
                SetInvisible(false);
        }

        private void Wire()
        {
            if (_wired)
                return;

            _wired = true;

            _unit = GetComponent<Unit>();

            var move = GetComponentInChildren<ShipMovement>(true);

            if (move != null)
            {
                // Public Action fields - no patch needed for either.
                move.DashStarted += OnDash;
                move.BoostStarted += OnBoost;
            }

            var all = GetComponentsInChildren<DamagableResource>(true);

            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].onDamage != null)
                    all[i].onDamage.AddListener(OnHurt);

            ModuleForgeKills.ShotFired += OnShot;
        }

        private void OnDestroy()
        {
            ModuleForgeKills.ShotFired -= OnShot;

            var move = GetComponentInChildren<ShipMovement>(true);

            if (move != null)
            {
                move.DashStarted -= OnDash;
                move.BoostStarted -= OnBoost;
            }
        }

        private void OnDash() { _lastDash = Time.time; }
        private void OnBoost() { _boosting = true; }
        private void OnHurt() { _lastHit = Time.time; }

        private void OnShot(WeaponBase weapon)
        {
            // Every unit's shots come through here, so filter to ours or an
            // enemy firing would break the player's cloak.
            if (weapon == null || _unit == null || weapon.Owner != _unit)
                return;

            _lastShot = Time.time;
        }

        public void NoteKill() { _lastKill = Time.time; }

        private void Update()
        {
            if (_specs.Count == 0)
                return;

            // There is no BoostEnded event, so the held state has to be polled.
            var move = GetComponentInChildren<ShipMovement>(true);

            if (move != null)
                _boosting = move.IsBoosted;

            bool want = false;

            foreach (var kv in _specs)
            {
                if (Grants(kv.Value))
                {
                    want = true;
                    break;
                }
            }

            SetInvisible(want);
        }

        private bool Grants(Spec s)
        {
            // Firing reveals you, if this module says so. Checked first because
            // it overrides every trigger - including "always".
            if (s.revealOnFire && Time.time - _lastShot < s.revealSeconds)
                return false;

            switch (s.trigger)
            {
                case StealthEffect.Dash:
                    return Time.time - _lastDash < s.seconds;

                case StealthEffect.Sprint:
                    return _boosting;

                case StealthEffect.Idle:
                    // "Go to ground": hidden once you have not fired for a
                    // while. Note this one needs no reveal rule of its own -
                    // firing resets _lastShot and un-hides you by construction.
                    return Time.time - _lastShot >= s.idleSeconds;

                case StealthEffect.Kill:
                    return Time.time - _lastKill < s.seconds;

                case StealthEffect.Hit:
                    return Time.time - _lastHit < s.seconds;

                case StealthEffect.Always:
                    return true;

                default:
                    return false;
            }
        }

        private void SetInvisible(bool on)
        {
            try
            {
                if (_unit == null)
                    _unit = GetComponent<Unit>();

                if (_unit == null || _unit.ComponentData == null)
                    return;

                if (on)
                {
                    if (!_unit.ComponentData.IsInvisible)
                    {
                        _unit.ComponentData.IsInvisible = true;
                        _weSet = true;
                    }

                    return;
                }

                if (_weSet && _unit.ComponentData.IsInvisible)
                    _unit.ComponentData.IsInvisible = false;

                _weSet = false;
            }
            catch (Exception e)
            {
                Log.LogError("Setting stealth failed: " + e);
            }
        }

        public static ModuleForgeStealth For(Unit.Data unit)
        {
            Unit u = ModuleForgeUnits.Find(unit);

            if (u == null)
                return null;

            var s = u.GetComponent<ModuleForgeStealth>();

            if (s == null)
                s = u.gameObject.AddComponent<ModuleForgeStealth>();

            return s;
        }
    }
}
