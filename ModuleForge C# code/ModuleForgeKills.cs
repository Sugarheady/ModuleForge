using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // "Who killed that, and with which weapon?"
    //
    // The game answers the first half and not the second. `Unit.RegisterKill`
    // fires a public `KilledAnotherUnit` event with (killer, victim), and it is
    // called from exactly three places - all in DamagableResource, all gated on
    // `IsDead && owner != null`:
    //
    //     ProjectileCollided     -> projectile.Owner.RegisterKill(victim)
    //     OnHitByHitscanWeapon   -> weapon.Owner.RegisterKill(victim)
    //     OnExplosion            -> explosion.Owner.RegisterKill(victim)
    //
    // Two consequences shaped this class.
    //
    // 1. **Nothing else credits a kill.** Not Die() itself, not burn ticks, not
    //    electricity, not cell damage - and not any of the mod-side damage
    //    either. So a kill by a gas cloud or a dash-ram was invisible, which is
    //    precisely the case ("dash through enemies to win fuel back") this was
    //    asked for. Mod damage paths call CreditKill directly.
    //
    // 2. **The weapon is never recorded**, only the owning Unit. A module
    //    sitting in the PRIMARY weapon's grid is supposed to pay out only for
    //    that weapon's kills, so the weapon has to be tracked here. Each of the
    //    three paths needs its own answer:
    //      - projectile: stamped at Shoot, from the weapon DoShoot is running
    //        for. FireSingle instantiates AND calls Shoot() synchronously
    //        inside DoShoot, so a scoped static is exact.
    //      - hitscan: the listener is handed the weapon already.
    //      - explosion: the weapon of the projectile that spawned it, scoped
    //        the same way around SpawnExplosion.
    public static class ModuleForgeKills
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Kills");

        // Anything that wants telling about a kill. The Unit.Data is how a
        // module effect knows the kill was on ITS unit rather than someone
        // else's, and `weapon` is null for a ship-grid module (see the note on
        // KillRewardEffect.Modify).
        public interface IKillListener
        {
            Unit.Data Owner { get; }
            WeaponBase ScopedWeapon { get; }
            void OnKill(Unit killer, Unit victim);
        }

        private static readonly List<IKillListener> _listeners =
            new List<IKillListener>();

        public static void Register(IKillListener l)
        {
            if (l != null && !_listeners.Contains(l))
                _listeners.Add(l);
        }

        public static void Unregister(IKillListener l)
        {
            _listeners.Remove(l);
        }

        public static bool Any { get { return _listeners.Count > 0; } }

        // ------------------------------------------------------------------
        // Weapon attribution
        // ------------------------------------------------------------------

        // The weapon DoShoot is running for, valid only for the duration of
        // that call. Frame-stamped so a stale value can never be believed.
        private static WeaponBase _firing;
        private static int _firingFrame = -1;

        // The weapon whose projectile is currently resolving an explosion.
        private static WeaponBase _exploding;
        private static int _explodingFrame = -1;

        // Shot -> the weapon that fired it. Pruned rather than kept forever;
        // a destroyed projectile's entry is dead weight, and a spray weapon
        // fills this fast.
        private static readonly Dictionary<Component, WeaponBase> _shots =
            new Dictionary<Component, WeaponBase>();

        // "Something fired." Raised from the DoShoot prefix below, which is the
        // one method provably on the path of every shot - including gadget
        // weapons, which bypass Shooter entirely and would be missed by
        // anything watching the trigger instead.
        //
        // The weapon's Owner IS valid here (Equip has long since run), which is
        // what lets a listener tell the player's shots from everyone else's.
        public static event Action<WeaponBase> ShotFired;

        private static void RaiseShotFired(WeaponBase weapon)
        {
            Action<WeaponBase> h = ShotFired;

            if (h == null)
                return;

            try
            {
                h(weapon);
            }
            catch (Exception e)
            {
                // A listener must never be able to stop the gun firing.
                Log.LogError("A ShotFired listener threw: " + e);
            }
        }

        public static void BeginShoot(WeaponBase weapon)
        {
            _firing = weapon;
            _firingFrame = Time.frameCount;
        }

        public static void EndShoot()
        {
            _firing = null;
            _firingFrame = -1;
        }

        // THE WEAPON `DoShoot` IS CURRENTLY RUNNING FOR, or null outside a shot.
        //
        // Added for `grow`, and the alternative it replaces is the interesting
        // part. The obvious route is `WeaponOf(projectile)` from a
        // `Projectile.Shoot` postfix - but `StampShot` is called from **this
        // class's own** `Projectile.Shoot` postfix, and Harmony does not order
        // two postfixes on one method. So half the time the map would not have
        // the shot yet, and the feature would work or not depending on patch
        // registration order: the worst kind of failure, because it would look
        // intermittent rather than wrong.
        //
        // This has no such race. `WeaponBase.DoShoot` is the CALLER of
        // `Projectile.Shoot`, so its prefix has provably already run.
        //
        // The frame test is the same one `StampShot` applies, and it matters
        // for the same reason: `EndShoot` can be missed if the original throws,
        // and a stale weapon read a frame later would attribute a shot to a gun
        // that did not fire it.
        public static WeaponBase Firing
        {
            get
            {
                return (_firingFrame == Time.frameCount) ? _firing : null;
            }
        }

        public static void StampShot(Component shot)
        {
            if (shot == null || _firing == null || _firingFrame != Time.frameCount)
                return;

            if (_shots.Count > 512)
                Prune();

            _shots[shot] = _firing;
        }

        // Credit a shot that was NOT created by the game's own firing path.
        //
        // `StampShot` only records while `BeginShoot`/`EndShoot` bracket the
        // call, which is right for every shot the game fires and no use at all
        // for one born mid-flight. Weapon Forge creates those - a split
        // fragment, a chain jump, a rallied reflect, a catalyst copy - and it
        // has an identical map of its own, so before this existed such a shot
        // was attributed in that mod and nowhere here. A `KillRewardEffect` on
        // the weapon's grid then compared the killing weapon against its own
        // `_weapon` scope, found nothing, and paid nothing: "the damage is
        // being passed on from the flechette weapon but the onkill effects are
        // not."
        //
        // Public and deliberately loose about who calls it. Weapon Forge finds
        // it by type name, exactly as the cross-mod rules require, and this mod
        // never needs to know whether that mod is installed.
        public static void Attribute(Component shot, WeaponBase weapon)
        {
            if (shot == null || weapon == null)
                return;

            if (_shots.Count > 512)
                Prune();

            _shots[shot] = weapon;
        }

        private static void Prune()
        {
            var dead = new List<Component>();

            foreach (var kv in _shots)
                if (kv.Key == null)
                    dead.Add(kv.Key);

            for (int i = 0; i < dead.Count; i++)
                _shots.Remove(dead[i]);

            // Still full of live shots? Then this is a very fast weapon and
            // the oldest entries matter least - clear rather than grow without
            // bound.
            if (_shots.Count > 512)
                _shots.Clear();
        }

        public static WeaponBase WeaponOf(Component shot)
        {
            WeaponBase w;
            return (shot != null && _shots.TryGetValue(shot, out w)) ? w : null;
        }

        public static void BeginExplosion(Component shot)
        {
            _exploding = WeaponOf(shot);
            _explodingFrame = Time.frameCount;
        }

        public static void EndExplosion()
        {
            _exploding = null;
            _explodingFrame = -1;
        }

        // ------------------------------------------------------------------
        // The kill itself
        // ------------------------------------------------------------------

        // What the current kill should be attributed to, decided by whichever
        // of the three paths we are inside. Null means "we could not tell",
        // and a weapon-scoped listener deliberately does NOT pay out then -
        // paying out on an unattributable kill would make grid placement
        // meaningless, which is the whole point of the weapon scope.
        private static WeaponBase _credit;

        public static void SetCredit(WeaponBase weapon)
        {
            _credit = weapon;
        }

        public static void ClearCredit()
        {
            _credit = null;
        }

        // Public because it is useful well beyond kills: it is live for the
        // whole duration of all three damage routes, so anything reacting to
        // damage can ask "which weapon did this?" without adding a patch. It is
        // NULL for a burn tick or a cell collision, which makes it a free gate
        // for anything that should apply to weapon damage and not to
        // damage-over-time - that is exactly how the crit modules use it.
        public static WeaponBase Credit { get { return _credit; } }

        public static WeaponBase CurrentExplosionWeapon
        {
            get
            {
                return (_explodingFrame == Time.frameCount) ? _exploding : null;
            }
        }

        // Called from the RegisterKill hook, and directly by mod damage that
        // the game would never have credited.
        public static void Dispatch(Unit killer, Unit victim)
        {
            if (_listeners.Count == 0 || killer == null)
                return;

            // ★★ KILLING YOUR OWN SIDE PAYS NOTHING. Audit one mod, audit its
            // twin: Weapon Forge's `ForgeKillsPatch.Dispatch` had exactly this
            // hole, and so did this one. Nothing here asked whose side the
            // victim was on, so a kill-reward module paid its full reward for
            // destroying your own minion, your own charmed unit or your own
            // wingman drone - a flat amount per kill, on a unit that is cheap
            // to redeploy, which is a resource printer rather than a trickle.
            //
            // `IsFriendsWith` is the game's own question and the same one the
            // friendly-fire skip in `Projectile.FixedUpdate` asks. It rests on
            // `Player.IsFriendsWith(Player)` being true, which is not obvious -
            // checked rather than assumed: `Player.asset` ships
            // `allies: [ {fileID: 11400000} ]`, a self-reference.
            if (victim != null && killer.IsFriendsWith(victim))
                return;

            Unit.Data data = killer.ComponentData;

            for (int i = _listeners.Count - 1; i >= 0; i--)
            {
                IKillListener l = _listeners[i];

                if (l == null)
                {
                    _listeners.RemoveAt(i);
                    continue;
                }

                try
                {
                    if (l.Owner != null && l.Owner != data)
                        continue;

                    // Weapon-scoped: only this weapon's kills count. An
                    // unattributed kill is skipped rather than guessed at.
                    if (l.ScopedWeapon != null && l.ScopedWeapon != _credit)
                        continue;

                    l.OnKill(killer, victim);
                }
                catch (Exception e)
                {
                    Log.LogError("A kill reward failed: " + e);
                }
            }
        }

        // For damage the game does not credit at all. Registers the kill with
        // the game as well, so anything else listening (the music controller,
        // and any future feature) sees it too.
        public static void CreditKill(
            Unit killer, Unit victim, WeaponBase weapon)
        {
            if (killer == null || victim == null)
                return;

            SetCredit(weapon);

            try
            {
                killer.RegisterKill(victim);
            }
            finally
            {
                ClearCredit();
            }
        }

        // ------------------------------------------------------------------
        // Hooks
        // ------------------------------------------------------------------

        [HarmonyPatch(typeof(Unit), "RegisterKill")]
        public class OnRegisterKill
        {
            static void Postfix(Unit __instance, Unit unit)
            {
                try
                {
                    Dispatch(__instance, unit);
                }
                catch (Exception e)
                {
                    Log.LogError("RegisterKill hook failed: " + e);
                }
            }
        }

        // DoShoot is the one method provably on the path of every shot - the
        // shoot SFX and the muzzle flash both fire from it.
        [HarmonyPatch(typeof(WeaponBase), "DoShoot")]
        public class OnDoShoot
        {
            static void Prefix(WeaponBase __instance)
            {
                BeginShoot(__instance);
                RaiseShotFired(__instance);
            }

            // Finalizer, not Postfix: if DoShoot throws, a stale weapon left in
            // the static would be stamped onto somebody else's next shot.
            static void Finalizer()
            {
                EndShoot();
            }
        }

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                StampShot(__instance);
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnShootPhysics
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                StampShot(__instance);
            }
        }

        // The three crediting paths, each wrapped so `_credit` says which
        // weapon is responsible while RegisterKill runs inside them.

        [HarmonyPatch(typeof(DamagableResource), "ProjectileCollided")]
        public class OnProjectileCollided
        {
            static void Prefix(IProjectile projectile)
            {
                SetCredit(WeaponOf(projectile as Component));
            }

            static void Finalizer()
            {
                ClearCredit();
            }
        }

        [HarmonyPatch(typeof(DamagableResource), "OnHitByHitscanWeapon")]
        public class OnHitscan
        {
            static void Prefix(HitscanWeapon weapon)
            {
                SetCredit(weapon);
            }

            static void Finalizer()
            {
                ClearCredit();
            }
        }

        [HarmonyPatch(typeof(DamagableResource), "OnExplosion")]
        public class OnExplosionHit
        {
            static void Prefix()
            {
                SetCredit(CurrentExplosionWeapon);
            }

            static void Finalizer()
            {
                ClearCredit();
            }
        }

        // An explosion is a struct with no back-reference to the weapon, so the
        // only place to learn it is while the projectile that owns it is still
        // on the stack.
        [HarmonyPatch(typeof(Projectile), "SpawnExplosion")]
        public class OnSpawnExplosion
        {
            static void Prefix(Projectile __instance)
            {
                BeginExplosion(__instance);
            }

            static void Finalizer()
            {
                EndExplosion();
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "SpawnExplosion")]
        public class OnSpawnExplosionPhysics
        {
            static void Prefix(PhysicsProjectile __instance)
            {
                BeginExplosion(__instance);
            }

            static void Finalizer()
            {
                EndExplosion();
            }
        }

        public static void Reset()
        {
            _shots.Clear();
            EndShoot();
            EndExplosion();
            ClearCredit();
        }
    }
}
