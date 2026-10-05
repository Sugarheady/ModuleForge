using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // Drives DamagableResource.IsInvincible - "cannot die", not "immune".
    //
    // WORTH BEING PRECISE ABOUT, because the name misleads. The game does:
    //
    //     if (this.IsInvincible && tank.Value <= 0f)   -> floor the tank at 1
    //
    // so damage still lands and the bar still drops; you just cannot be
    // finished. That is why a health-threshold trigger fits it so naturally: it
    // is a safety net rather than a shield, and it does nothing at all until
    // the moment it saves you.
    //
    // Contrast ModuleForgeInvuln, which uses `damageBlockers` and genuinely
    // stops damage from landing at all. Two different mechanics that sound
    // identical in a card title.
    public class ModuleForgeLastStand : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.LastStand");

        public class Spec
        {
            public bool onThreshold = true;
            public float threshold = 0.25f;   // fraction of max
            public bool onKill;
            public float seconds = 3f;
            public float cooldown = 15f;
        }

        private readonly Dictionary<object, Spec> _specs =
            new Dictionary<object, Spec>();

        private Unit _unit;
        private DamagableResource[] _pools;

        private float _activeUntil;
        private float _readyAt;
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
                Set(false);
        }

        private void Wire()
        {
            if (_wired)
                return;

            _wired = true;

            _unit = GetComponent<Unit>();
            _pools = GetComponentsInChildren<DamagableResource>(true);

            for (int i = 0; i < _pools.Length; i++)
                if (_pools[i] != null && _pools[i].onDamage != null)
                    _pools[i].onDamage.AddListener(OnHurt);
        }

        // The threshold trigger. Checked when damage lands rather than every
        // frame: that is exactly when the fraction can have crossed, and it
        // costs nothing the rest of the time.
        private void OnHurt()
        {
            if (_specs.Count == 0 || Time.time < _readyAt)
                return;

            float worst = LowestFraction();

            foreach (var kv in _specs)
            {
                if (!kv.Value.onThreshold)
                    continue;

                if (worst <= kv.Value.threshold)
                {
                    Arm(kv.Value);
                    return;
                }
            }
        }

        public void NoteKill()
        {
            if (_specs.Count == 0 || Time.time < _readyAt)
                return;

            foreach (var kv in _specs)
            {
                if (kv.Value.onKill)
                {
                    Arm(kv.Value);
                    return;
                }
            }
        }

        private void Arm(Spec s)
        {
            _activeUntil = Time.time + s.seconds;

            // The cooldown starts when the window OPENS, not when it closes, so
            // "3 seconds every 15" means what it says rather than 3 + 15.
            _readyAt = Time.time + s.cooldown;

            Set(true);
        }

        // The lowest fraction across every pool the unit can lose. Health is
        // usually the only one that matters, but a ship carrying a shield
        // resource has two, and "lowest" is the one that is about to kill you.
        private float LowestFraction()
        {
            float lowest = 1f;

            if (_unit == null || _unit.ComponentData == null)
                return lowest;

            var tanks = _unit.ComponentData.GetAllTanks();

            if (tanks == null)
                return lowest;

            for (int i = 0; i < tanks.Count; i++)
            {
                var t = tanks[i];

                if (t == null || t.Capacity <= 0f || t.isInfinite)
                    continue;

                float f = t.Value / t.Capacity;

                if (f < lowest)
                    lowest = f;
            }

            return lowest;
        }

        private void Update()
        {
            if (!_weSet)
                return;

            if (Time.time >= _activeUntil)
                Set(false);
        }

        private void Set(bool on)
        {
            try
            {
                if (_pools == null)
                    return;

                for (int i = 0; i < _pools.Length; i++)
                {
                    if (_pools[i] == null)
                        continue;

                    // Only clear what we set - the debug menu writes this same
                    // flag, and stomping it would look like the mod fighting
                    // the console.
                    if (on)
                        _pools[i].IsInvincible = true;
                    else if (_weSet)
                        _pools[i].IsInvincible = false;
                }

                _weSet = on;
            }
            catch (Exception e)
            {
                Log.LogError("Setting last stand failed: " + e);
            }
        }

        public static ModuleForgeLastStand For(Unit.Data unit)
        {
            Unit u = ModuleForgeUnits.Find(unit);

            if (u == null)
                return null;

            var s = u.GetComponent<ModuleForgeLastStand>();

            if (s == null)
                s = u.gameObject.AddComponent<ModuleForgeLastStand>();

            return s;
        }
    }
}
