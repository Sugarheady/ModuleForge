using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // Attaches and drives the growth on shots fired by a weapon a grow module
    // applies to. See ModuleForgeGrow for why the component goes on the LIVE
    // projectile rather than the prefab, and why `damageAtFull` is absent.
    public static class ModuleForgeGrowPatch
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Grow");

        // ------------------------------------------------------------------
        // Plain projectiles
        // ------------------------------------------------------------------

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                if (!ModuleForgeGrow.Any || __instance == null)
                    return;

                try
                {
                    // WHICH WEAPON FIRED THIS, asked of the CALLER rather than
                    // of a map.
                    //
                    // `ModuleForgeKills.WeaponOf` would be the obvious route and
                    // it races: that map is filled by this same class's own
                    // `Projectile.Shoot` postfix, and Harmony does not order two
                    // postfixes on one method. `Firing` is set by the
                    // `WeaponBase.DoShoot` prefix - the caller - so it has
                    // provably already run. See ModuleForgeKills.Firing.
                    ModuleForgeGrow.Options cfg =
                        ModuleForgeGrow.Resolve(ModuleForgeKills.Firing);

                    if (cfg == null)
                        return;

                    var g = __instance.gameObject
                        .AddComponent<ModuleForgeGrowth>();

                    g.cfg = cfg;
                    g.shot = true;
                    g.startTime = Time.time;
                    g.origin = __instance.transform.position;
                    g.baseScale = __instance.transform.localScale;
                    g.baseRadius = __instance.Radius;

                    g.resolvedSpan = ResolveSpan(
                        cfg,
                        __instance.RangeData.enabled
                            ? __instance.RangeData.range : 0f,
                        __instance.LifetimeData.enabled
                            ? __instance.LifetimeData.time : 0f);

                    Apply(__instance, g);
                }
                catch (Exception e)
                {
                    Log.LogError("Grow could not start on a shot: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        public class OnFixedUpdate
        {
            static void Prefix(Projectile __instance)
            {
                var g = (__instance != null)
                    ? __instance.GetComponent<ModuleForgeGrowth>() : null;

                if (g == null || !g.shot || g.cfg == null)
                    return;

                try
                {
                    Apply(__instance, g);
                }
                catch (Exception e)
                {
                    // LATCH OFF rather than throwing every frame for every
                    // shot. A per-frame error that repeats per projectile
                    // floods the log so badly it hides its own first line.
                    g.shot = false;
                    Log.LogError("Grow failed, the shot keeps its size: " + e);
                }
            }
        }

        private static void Apply(Projectile p, ModuleForgeGrowth g)
        {
            float scale = Progress(g, p.transform.position);

            p.transform.localScale = g.baseScale * scale;

            // THIS is the whole feature: Radius is public and the game rebuilds
            // its CircleCast from it every frame, so writing it grows the real
            // damage volume rather than just the picture.
            if (g.cfg.hitbox)
                p.Radius = g.baseRadius * scale;
        }

        // ------------------------------------------------------------------
        // Lobbed / usePhysics shots
        // ------------------------------------------------------------------
        //
        // A different class with a different collision model, so it needs its
        // own hooks - but the maths is shared, and it is simpler in one way: a
        // PhysicsProjectile has no `Radius`. It collides with a real
        // `Collider2D` driven by a `Rigidbody2D`, and a real collider scales
        // with its transform, so growing the art grows the true hitbox for
        // free. `hitbox: false` therefore cannot be honoured here at all, which
        // the build log says rather than pretending.

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnShootPhysics
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                if (!ModuleForgeGrow.Any || __instance == null)
                    return;

                try
                {
                    // ★ `ProjectileWeapon.FireSingle` CALLS `Shoot()` TWICE ON A
                    // LOBBED SHOT, and the first call is against a BLANK - it
                    // sits inside the `if (UsePhysics)` branch, before `Damage`,
                    // `RangeData`, `LifetimeData`, `Velocity` and `Owner` are
                    // assigned further down. A plain `Projectile` never does
                    // this.
                    //
                    // Capturing on that first call would latch a zero scale and
                    // a range of 0, so the span would fall back to its default
                    // and the shot would grow over the wrong distance - and,
                    // because the component would already exist, the real call
                    // would be skipped by the guard below.
                    //
                    // The test is `Owner == null` **and** zero velocity
                    // together: either alone would reject a legitimate speed-0
                    // dropped mine.
                    if (__instance.Owner == null &&
                        __instance.Velocity.sqrMagnitude <= 0.000001f)
                    {
                        return;
                    }

                    // The second Shoot on the same object must not stack a
                    // second component.
                    if (__instance.GetComponent<ModuleForgeGrowth>() != null)
                        return;

                    ModuleForgeGrow.Options cfg =
                        ModuleForgeGrow.Resolve(ModuleForgeKills.Firing);

                    if (cfg == null)
                        return;

                    var g = __instance.gameObject
                        .AddComponent<ModuleForgeGrowth>();

                    g.cfg = cfg;
                    g.shot = true;
                    g.startTime = Time.time;
                    g.origin = __instance.transform.position;
                    g.baseScale = __instance.transform.localScale;
                    g.baseRadius = 0f;              // no Radius on this class

                    // `rangeData` is completely INERT on a lobbed shot - the
                    // game computes a time from it at Shoot and never reads it
                    // again, and every stock lobbed weapon ships it disabled.
                    // So a distance span falls back rather than borrowing a
                    // number the engine does not enforce.
                    g.resolvedSpan = ResolveSpan(
                        cfg,
                        0f,
                        __instance.LifetimeData.enabled
                            ? __instance.LifetimeData.time : 0f);

                    ApplyPhysics(__instance, g);
                }
                catch (Exception e)
                {
                    Log.LogError("Grow could not start on a lobbed shot: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "FixedUpdate")]
        public class OnFixedUpdatePhysics
        {
            static void Prefix(PhysicsProjectile __instance)
            {
                var g = (__instance != null)
                    ? __instance.GetComponent<ModuleForgeGrowth>() : null;

                if (g == null || !g.shot || g.cfg == null)
                    return;

                try
                {
                    ApplyPhysics(__instance, g);
                }
                catch (Exception e)
                {
                    g.shot = false;
                    Log.LogError("Grow failed, the shot keeps its size: " + e);
                }
            }
        }

        private static void ApplyPhysics(
            PhysicsProjectile p, ModuleForgeGrowth g)
        {
            float scale = Progress(g, p.transform.position);

            p.transform.localScale = g.baseScale * scale;
        }

        // ------------------------------------------------------------------
        // Shared maths
        // ------------------------------------------------------------------

        // 0 means "borrow the shot's own bound", which is almost always what an
        // author wants: the shot then peaks exactly as it runs out.
        private static float ResolveSpan(
            ModuleForgeGrow.Options cfg, float range, float lifetime)
        {
            if (cfg.span > 0f)
                return cfg.span;

            if (!cfg.overTime && range > 0f)
                return range;

            if (cfg.overTime && lifetime > 0f)
                return lifetime;

            // Last resort. Distinct per mode because a "2 unit" ramp and a
            // "2 second" ramp are not the same shape at any speed.
            return cfg.overTime ? 2f : 10f;
        }

        private static float Progress(ModuleForgeGrowth g, Vector2 here)
        {
            ModuleForgeGrow.Options cfg = g.cfg;

            float span = (g.resolvedSpan > 0f) ? g.resolvedSpan : 1f;

            float t = cfg.overTime
                ? (Time.time - g.startTime) / span
                : Vector2.Distance(g.origin, here) / span;

            t = cfg.clamp ? Mathf.Clamp01(t) : Mathf.Max(0f, t);

            // Pow only when asked: it is the difference between "swells
            // steadily" and "stays small then blows up at the last moment", and
            // it is not free on every shot every frame.
            float eased = (cfg.curve == 1f) ? t : Mathf.Pow(t, cfg.curve);

            // UNCLAMPED so `clamp: false` really can carry on past `to`.
            return Mathf.LerpUnclamped(cfg.from, cfg.to, eased);
        }
    }
}
