using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // Applies the phasing + pierce-cap modules to a ship's projectiles.
    //   Shoot  -> if the owner has a phasing module, strip the Ground
    //             collision bit (phase through terrain); if it has a pierce
    //             module, enable piercing + attach a ModuleForgePierceCap.
    //   TryHit -> count distinct enemies for the pierce cap and destroy
    //             past the limit (optional falloff + explode).
    // Coexists with WeaponForge: stripping the Ground bit is idempotent,
    // and the pierce cap uses ModuleForge's own component/count.
    public static class ModuleForgeProjectilePatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge");

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            private static AccessTools.FieldRef<Projectile, int> _maskRef;
            private static bool _ready;
            private static int _groundBit;

            static void Postfix(Projectile __instance)
            {
                try
                {
                    Unit owner = __instance.Owner;
                    if (owner == null || owner.ComponentData == null)
                        return;

                    Unit.Data data = owner.ComponentData;

                    if (ModuleForgeProjectile.IsPhasing(data))
                        StripGround(__instance);

                    int cap;
                    float falloff;
                    bool explode;
                    if (ModuleForgeProjectile.TryGetPierce(
                        data, out cap, out falloff, out explode))
                    {
                        PiercingData pd = __instance.PiercingData;
                        if (!pd.enabled)
                        {
                            pd.enabled = true;
                            if (pd.damageRepeatDelay <= 0f)
                                pd.damageRepeatDelay = 0.15f;
                            __instance.PiercingData = pd;
                        }

                        var pc = __instance.GetComponent<ModuleForgePierceCap>();
                        if (pc == null)
                            pc = __instance.gameObject.AddComponent<ModuleForgePierceCap>();
                        pc.limit = Mathf.Max(0, cap);
                        pc.falloff = falloff;
                        pc.explodeOnLimit = explode;
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("Projectile-mod shoot failed: " + e);
                }
            }

            private static void StripGround(Projectile projectile)
            {
                if (!_ready)
                {
                    _maskRef = AccessTools.FieldRefAccess<Projectile, int>(
                        "collisionLayerMask");
                    int g = LayerMask.NameToLayer("Ground");
                    _groundBit = (g >= 0) ? (1 << g) : 0;
                    _ready = true;
                }

                if (_maskRef != null && _groundBit != 0)
                    _maskRef(projectile) &= ~_groundBit;
            }
        }

        [HarmonyPatch(typeof(Projectile), "TryHit")]
        public class OnTryHit
        {
            private static MethodInfo _spawnExplosion;
            private static bool _lookedUp;

            static void Postfix(Projectile __instance, IProjectileListener listener)
            {
                try
                {
                    var cap = __instance.GetComponent<ModuleForgePierceCap>();
                    if (cap == null || listener == null)
                        return;

                    if (!cap.seen.Add(listener))
                        return;

                    if (cap.falloff > 0f)
                    {
                        Damage d = __instance.Damage;
                        d.amount *= Mathf.Max(0f, 1f - cap.falloff);
                        __instance.Damage = d;
                    }

                    if (cap.seen.Count > cap.limit)
                    {
                        if (cap.explodeOnLimit)
                            Explode(__instance);
                        UnityEngine.Object.Destroy(__instance.gameObject);
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("Projectile-mod pierce failed: " + e);
                }
            }

            private static void Explode(Projectile projectile)
            {
                if (!_lookedUp)
                {
                    _spawnExplosion = AccessTools.Method(
                        typeof(Projectile), "SpawnExplosion");
                    _lookedUp = true;
                }

                if (_spawnExplosion == null)
                    return;

                try
                {
                    _spawnExplosion.Invoke(projectile,
                        new object[] { (Vector2)projectile.transform.position });
                }
                catch { }
            }
        }
    }
}
