using System.Runtime.CompilerServices;

namespace ModuleForge
{
    // WHOSE GUNS DOES A SHIP-GRID MODULE REACH? The unit that carries it -
    // and nobody else.
    //
    // ---------------------------------------------------------------------
    // ★ THE BUG THIS EXISTS FOR (R19 tests 243 and 246, 2026-09-25)
    // ---------------------------------------------------------------------
    //
    // Six registries here (crit, execute, grow, leech, slow, windup) have two
    // buckets: per weapon for a module in a weapon's grid, and a GLOBAL one
    // keyed by nothing for a module on the ship grid, documented as "a null
    // weapon means every weapon". Every patch then resolved that bucket against
    // whatever weapon had just fired - **and an enemy's gun is a weapon.** So a
    // grow module on your ship grew enemy bullets to x3.5, a windup module made
    // enemies fire faster the longer they held the trigger (and every enemy
    // meter posted its own line to your status feed, which is the chirp storm),
    // and the effects that guard the VICTIM ("never touches your own side")
    // were guarding the wrong direction: to an enemy gun YOU are the other
    // side, so an enemy shot could slow you, crit you, leech off you and -
    // with an execute module installed - finish you below 20% health.
    //
    // "Every weapon" was always meant to be "every weapon YOU carry". A ship
    // module is part of one ship; it has no business on anybody else's gun.
    //
    // ---------------------------------------------------------------------
    // How
    // ---------------------------------------------------------------------
    //
    // Every such effect already learns its owner - `OnInstalled(Unit.Data)` is
    // handed exactly the unit whose grid holds it (`ModuleGridOwner.Data` passes
    // `this.unit`, the entity's own `Unit.Data`) - and a firing weapon's
    // `Owner` is that unit's MonoBehaviour, whose `ComponentData` is the same
    // object. So the test is one reference comparison, with no grid walk.
    //
    // ★ HELD WEAKLY, SO THERE IS NOTHING TO RESET. A `ConditionalWeakTable`
    // drops an entry when its effect is collected, so the table cannot carry
    // one run's dead modules into the next, and - more importantly - no
    // run-entry reset can wipe the owner of an effect that is still installed.
    // Registries here ARE reset at run entry and refilled by `OnInstalled`;
    // an owner table on the same reset would work only for as long as nothing
    // ever re-pushed a contribution without re-installing, which is not a
    // property anything enforces.
    //
    // ★ AN UNKNOWN OWNER MATCHES NOBODY. A global contribution whose owner was
    // never recorded - or a weapon with no `Owner` at all - is not "yours",
    // and the failure is loud in the other direction: a module that does
    // nothing is a bug report, a module that fires on enemy guns is a death.
    public static class ModuleForgeScope
    {
        private sealed class Box
        {
            public Unit.Data owner;
        }

        private static readonly ConditionalWeakTable<object, Box> _owners =
            new ConditionalWeakTable<object, Box>();

        // Called by every effect that can contribute to a global bucket,
        // from each callback that hands it the unit.
        public static void SetOwner(object key, Unit.Data owner)
        {
            if (key == null || owner == null)
                return;

            _owners.GetOrCreateValue(key).owner = owner;
        }

        public static Unit.Data OwnerOf(object key)
        {
            if (key == null)
                return null;

            Box box;
            return _owners.TryGetValue(key, out box) ? box.owner : null;
        }

        // Does a SHIP-GRID contribution registered under `key` apply to shots
        // from `weapon`? Only if the weapon belongs to the unit carrying it.
        // (A WEAPON-grid contribution is keyed on its own weapon already and
        // never needs this.)
        public static bool Applies(object key, WeaponBase weapon)
        {
            if (weapon == null)
                return false;

            Unit shooter = weapon.Owner;

            if (shooter == null)
                return false;

            Unit.Data owner = OwnerOf(key);

            return owner != null && ReferenceEquals(shooter.ComponentData, owner);
        }
    }
}
