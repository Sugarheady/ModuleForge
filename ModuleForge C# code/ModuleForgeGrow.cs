using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // GROW - a shot that changes SIZE as it travels. A pinprick that arrives as
    // a wrecking ball, or a fat slug that tapers to nothing.
    //
    // A port of Weapon Forge's `ForgeGrowth`, and cheap for one specific reason
    // that was re-checked rather than recalled: **`Projectile.Radius` is a
    // public property and the game rebuilds its collision sweep from it every
    // frame** - `Physics2D.CircleCast(position, this.Radius, Velocity, ...)`.
    // So growing the number grows the real hitbox, and the art is just
    // `transform.localScale`. Nothing has to be kept in sync by hand: what you
    // see and what hits are driven by one progress value.
    //
    // ---------------------------------------------------------------------
    // THE ONE ARCHITECTURAL DIFFERENCE FROM THE WEAPON SIDE, and it is a
    // simplification rather than a compromise
    // ---------------------------------------------------------------------
    //
    // Weapon Forge attaches its growth component to the ammo **prefab** at build
    // time, because a weapon file owns its own prefab clone. A module owns no
    // prefab - it has to act on whatever gun the player slots it beside - so
    // this version adds the component to the **live projectile** in a
    // `Shoot` postfix instead.
    //
    // That sidesteps the most expensive bug this project has had outright. A
    // plain C# config object referenced from a prefab-attached MonoBehaviour is
    // **dropped by `Instantiate`** (Unity serializes serialized state only), so
    // every guard reading it early-returns and the feature silently does
    // nothing - which is what disabled the entire `gas` and `catalyst` blocks
    // for ten failed tests. A component added to an already-instantiated object
    // is never copied by anything, so there is nothing to lose.
    //
    // ---------------------------------------------------------------------
    // ★ `damageAtFull` IS DELIBERATELY NOT PORTED - see the note in the effect
    // ---------------------------------------------------------------------
    //
    // The weapon side publishes its damage multiplier into `ForgeDamageStack`.
    // This mod has no such stack, and it ALREADY has an in-flight damage writer
    // (`ModuleForgePierceCap` multiplies `Projectile.Damage` by its falloff on
    // every pierced target). A second writer with its own captured base would
    // overwrite that falloff on the very next frame - the exact ordering bug the
    // stack exists to prevent, reintroduced from the other side. Size and hitbox
    // are the whole headline of `grow` and they are complete without it.
    public static class ModuleForgeGrow
    {
        public class Options
        {
            // Size multipliers relative to the shot's NORMAL size, so 1 is
            // unchanged. from > to shrinks instead of growing, which is a real
            // weapon and not a mistake.
            public float from = 0.4f;
            public float to = 3f;

            // false = progress measured by DISTANCE travelled (what "grows as
            // it flies" usually means); true = by seconds alive.
            public bool overTime;

            // Distance in world units, or seconds, to reach `to`. 0 = borrow
            // the shot's own range (or lifetime), which is almost always what
            // you want: it then peaks exactly as it runs out.
            public float span;

            // Grow the hitbox with the art. Off makes it purely cosmetic.
            //
            // NOTE this can only be honoured on a plain `Projectile`. A lobbed
            // shot has no `Radius` - it collides with a real `Collider2D`
            // driven by a `Rigidbody2D`, and a real collider scales with its
            // transform - so on one the hitbox always follows. Said out loud at
            // build time rather than pretended.
            public bool hitbox = true;

            // Shape of the ramp. 1 = linear, >1 = stays small then swells late,
            // <1 = swells fast then eases off.
            public float curve = 1f;

            // Stop at `to`, or keep going past it for as long as the shot
            // lives.
            public bool clamp = true;
        }

        // ------------------------------------------------------------------
        // The registry - global (ship grid) plus per weapon (weapon grid)
        // ------------------------------------------------------------------
        //
        // Same two-bucket shape as ModuleForgeExecute and ModuleForgeCrit, for
        // the same reason: `Modify` being called is the only honest "which grid
        // am I in?" test, so a null weapon key means every weapon.
        private static readonly Dictionary<object, Options> _global =
            new Dictionary<object, Options>();

        private static readonly Dictionary<WeaponBase, Dictionary<object, Options>>
            _byWeapon = new Dictionary<WeaponBase, Dictionary<object, Options>>();

        // Read on every shot fired in the game, so it is a field test rather
        // than a dictionary walk.
        public static bool Any { get; private set; }

        private static void Refresh()
        {
            Any = _global.Count > 0 || _byWeapon.Count > 0;
        }

        public static void Set(WeaponBase weapon, object key, Options options)
        {
            if (key == null || options == null)
                return;

            // Withdraw from every other bucket first - a module can move
            // grids, and a weapon is a fresh WeaponBase whenever its cluster is
            // rebuilt, so "set here and nowhere else" is the only semantics
            // that cannot strand a contribution on a weapon that is gone.
            Clear(key);

            if (weapon == null)
                _global[key] = options;
            else
            {
                Dictionary<object, Options> byKey;

                if (!_byWeapon.TryGetValue(weapon, out byKey))
                {
                    byKey = new Dictionary<object, Options>();
                    _byWeapon[weapon] = byKey;
                }

                byKey[key] = options;
            }

            Refresh();
        }

        public static void Clear(object key)
        {
            if (key == null)
                return;

            _global.Remove(key);

            List<WeaponBase> emptied = null;

            foreach (var pair in _byWeapon)
            {
                if (!pair.Value.Remove(key))
                    continue;

                if (pair.Value.Count == 0)
                {
                    if (emptied == null)
                        emptied = new List<WeaponBase>();

                    emptied.Add(pair.Key);
                }
            }

            if (emptied != null)
            {
                for (int i = 0; i < emptied.Count; i++)
                    _byWeapon.Remove(emptied[i]);
            }

            Refresh();
        }

        // THE FIRST MATCH WINS, and unlike execute's "strongest threshold" that
        // is not a judgement - it is an admission.
        //
        // Two grow modules on one gun cannot be combined in any way a player
        // would predict: multiplying the ramps (0.4->3 twice) gives 0.16->9,
        // which is a different weapon; averaging them gives neither. So one
        // applies, and the BUILD LOG says so when a second is registered rather
        // than leaving the author to wonder which won.
        public static Options Resolve(WeaponBase weapon)
        {
            Dictionary<object, Options> byKey;

            if (weapon != null && _byWeapon.TryGetValue(weapon, out byKey))
            {
                foreach (var pair in byKey)
                {
                    if (pair.Value != null)
                        return pair.Value;
                }
            }

            // ★ SHIP-GRID MEANS THE SHIP'S OWN GUNS. This loop used to return
            // the first global entry for ANY weapon - enemy guns included - so
            // enemy bullets swelled to x3.5 (R19 test 243). See
            // ModuleForgeScope.
            foreach (var pair in _global)
            {
                if (pair.Value != null && ModuleForgeScope.Applies(pair.Key, weapon))
                    return pair.Value;
            }

            return null;
        }

        // How many are registered, so the effect can warn about the case above
        // at the moment it becomes true rather than at damage time.
        public static int CountFor(WeaponBase weapon)
        {
            int n = _global.Count;

            Dictionary<object, Options> byKey;

            if (weapon != null && _byWeapon.TryGetValue(weapon, out byKey))
                n += byKey.Count;

            return n;
        }

        // Per-RUN state: live `WeaponBase` keys and contributions from modules
        // that are not uninstalled on teardown. The live growth components are
        // MonoBehaviours on projectiles and die with the scene for free, which
        // is why there is nothing else to sweep here.
        public static void Reset()
        {
            _global.Clear();
            _byWeapon.Clear();
            Refresh();
        }
    }

    // The per-shot state, on the shot itself.
    //
    // Everything is captured at `Shoot` and every write is computed FROM the
    // captured values rather than from the current ones, so the effect can
    // never compound frame over frame - the read-modify-write trap that made
    // overheat's muzzle tint converge on fully-hot within a second whatever the
    // heat was.
    public class ModuleForgeGrowth : MonoBehaviour
    {
        public ModuleForgeGrow.Options cfg;

        public bool shot;
        public float startTime;
        public Vector2 origin;
        public Vector3 baseScale;
        public float baseRadius;
        public float resolvedSpan;
    }
}
