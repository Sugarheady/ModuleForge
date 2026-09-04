using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // INFINITE AMMO, and the instant refill that goes with it.
    //
    // ================================================================
    // THE FIELD THIS IS BUILT ON IS GOD MODE, NOT INFINITE AMMO
    // ================================================================
    //
    // `Unit.Data.HasInfiniteResource` looks like exactly the right switch -
    // `Shooter.OwnerHasResource` reads it, and it is public. But its setter
    // does this:
    //
    //     foreach (ResourceTank t in GetAllTanks()) t.isInfinite = value;
    //
    // ...and `ResourceTank.Value`'s setter is:
    //
    //     if (this.isInfinite && this._value > value) return;
    //
    // i.e. an infinite tank REFUSES TO GO DOWN. Which is right for ammo - and
    // **HEALTH IS A ResourceTank TOO**. `DamagableResource.Damage` is literally
    // `tank.Value -= amount`, so that one line makes the ship completely
    // invulnerable. Shipping the obvious implementation would have shipped an
    // accidental god mode wearing an "infinite ammo" label.
    //
    // SO THE FLAG IS NEVER USED. This sets `isInfinite` per TANK, and skips
    // every tank that a `DamagableResource` guards - which is the game's own
    // definition of "a pool you can lose", found by asking the components
    // rather than by hardcoding Health. On the ship that means White, Fuel,
    // Caps, Tech, Purple and Money go infinite and Health does not.
    //
    // It still works, because `Shooter.OwnerHasResource` falls through to
    // `GetResource(...) >= Cost` and a tank that cannot go down sits at
    // capacity forever.
    //
    // TWO MORE THINGS THAT SHAPE THE CODE:
    //
    //   - **Never save-and-restore, and never write a bare `false`.** Several
    //     modules may want this at once and the debug menu writes the same
    //     fields. Each frame the wanted set is recomputed and only tanks THIS
    //     component turned on are ever turned off - the same contract the unit
    //     tint and the damage stack had to learn.
    //   - **`RefillResources()` heals you.** It fills `GetNotSharedTanks()`,
    //     and the only shared resource in the game is Money - so Health is in
    //     that list. A refill-on-kill module would quietly be a full heal on
    //     every kill, so the ammo tanks are refilled by default and health is
    //     an explicit opt-in.
    public class ModuleForgeAmmo : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge.Ammo");

        public class Spec
        {
            public string trigger = AmmoEffect.Kill;
            public float seconds = 3f;
            public bool infinite = true;
            public bool refill;
            public bool includeHealth;

            // Optional: restrict to one resource. Null means every ammo tank.
            public Resource only;
        }

        private readonly Dictionary<object, Spec> _specs =
            new Dictionary<object, Spec>();

        private Unit _unit;
        private float _lastKill = -999f;

        // Exactly the tanks we switched on, so nothing else's infinite flag is
        // ever cleared by us.
        private readonly HashSet<ResourceTank> _ours =
            new HashSet<ResourceTank>();

        private bool _saidGuarded;

        public void Request(object owner, Spec spec)
        {
            if (owner == null || spec == null)
                return;

            _specs[owner] = spec;

            if (_unit == null)
                _unit = GetComponent<Unit>();
        }

        public void Release(object owner)
        {
            if (owner == null)
                return;

            _specs.Remove(owner);

            if (_specs.Count == 0)
                ClearOurs();
        }

        // Called by the effect when a kill it owns lands. Refill is an EVENT
        // rather than a state, so it happens here and not in Update.
        public void NoteKill()
        {
            _lastKill = Time.time;

            foreach (var kv in _specs)
            {
                Spec s = kv.Value;

                if (s.refill && s.trigger == AmmoEffect.Kill)
                    DoRefill(s);
            }
        }

        // ------------------------------------------------------------------
        // Which tanks are fair game
        // ------------------------------------------------------------------
        //
        // A tank guarded by a DamagableResource is one you can LOSE - health,
        // and any shield resource. Making one of those infinite is
        // invulnerability, which this mod already does properly (and with a
        // cooldown) in "invuln" and "laststand".
        private bool Guarded(Resource resource)
        {
            if (resource == null)
                return true;

            var all = GetComponentsInChildren<DamagableResource>(true);

            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].resource == resource)
                    return true;

            return false;
        }

        private void DoRefill(Spec s)
        {
            if (_unit == null)
                return;

            try
            {
                List<ResourceTank> tanks = _unit.ComponentData.GetNotSharedTanks();

                if (tanks == null)
                    return;

                for (int i = 0; i < tanks.Count; i++)
                {
                    ResourceTank t = tanks[i];

                    if (t == null || t.resource == null)
                        continue;

                    if (s.only != null && t.resource != s.only)
                        continue;

                    // The whole reason refill is not just RefillResources():
                    // that call includes health, because health is not a
                    // SHARED resource. A heal on every kill is a different
                    // module from a reload on every kill.
                    if (!s.includeHealth && Guarded(t.resource))
                        continue;

                    t.Value = t.Capacity;
                }
            }
            catch (Exception e)
            {
                Log.LogError("Refill failed: " + e);
            }
        }

        private void Update()
        {
            if (_specs.Count == 0)
            {
                if (_ours.Count > 0)
                    ClearOurs();

                return;
            }

            if (_unit == null)
                _unit = GetComponent<Unit>();

            if (_unit == null)
                return;

            Spec active = null;

            foreach (var kv in _specs)
            {
                if (kv.Value.infinite && Grants(kv.Value))
                {
                    active = kv.Value;
                    break;
                }
            }

            if (active == null)
            {
                ClearOurs();
                return;
            }

            Apply(active);
        }

        private bool Grants(Spec s)
        {
            switch (s.trigger)
            {
                case AmmoEffect.Always:
                    return true;

                case AmmoEffect.Kill:
                    return Time.time - _lastKill < s.seconds;

                default:
                    return false;
            }
        }

        private void Apply(Spec s)
        {
            try
            {
                List<ResourceTank> tanks = _unit.ComponentData.GetAllTanks();

                if (tanks == null)
                    return;

                for (int i = 0; i < tanks.Count; i++)
                {
                    ResourceTank t = tanks[i];

                    if (t == null || t.resource == null)
                        continue;

                    if (s.only != null && t.resource != s.only)
                        continue;

                    if (Guarded(t.resource))
                    {
                        if (!_saidGuarded)
                        {
                            _saidGuarded = true;

                            Log.LogInfo(
                                "infinite ammo is skipping '" +
                                t.resource.name + "' because a " +
                                "DamagableResource guards it - that is a pool " +
                                "you can LOSE, and an infinite tank refuses to " +
                                "go down, so making it infinite would be " +
                                "invulnerability rather than ammo. Use " +
                                "\"invuln\" or \"laststand\" for that, both of " +
                                "which have a cooldown.");
                        }

                        continue;
                    }

                    if (!t.isInfinite)
                    {
                        t.isInfinite = true;
                        _ours.Add(t);
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogError("Infinite ammo failed: " + e);
            }
        }

        // Only ever clears what we set. The debug menu writes the same flags,
        // and stomping them would make the mod look like it was fighting the
        // developer console.
        private void ClearOurs()
        {
            if (_ours.Count == 0)
                return;

            foreach (ResourceTank t in _ours)
                if (t != null)
                    t.isInfinite = false;

            _ours.Clear();
        }

        private void OnDestroy()
        {
            ClearOurs();
        }

        public static ModuleForgeAmmo For(Unit.Data unit)
        {
            Unit u = ModuleForgeUnits.Find(unit);

            if (u == null)
                return null;

            var a = u.GetComponent<ModuleForgeAmmo>();

            if (a == null)
                a = u.gameObject.AddComponent<ModuleForgeAmmo>();

            return a;
        }
    }
}
