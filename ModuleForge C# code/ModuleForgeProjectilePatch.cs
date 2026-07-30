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
            private static AccessTools.FieldRef<Projectile, LayerMask> _maskRef;
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

                    // Pierce cap = module caps (owner) + the weapon's OWN
                    // baked cap from WeaponForge, if any. The two ADD UP into
                    // one counter here (ModuleForge owns counting; WeaponForge
                    // stands down when we're installed).
                    int cap;
                    float falloff;
                    bool explode;
                    bool haveModule = ModuleForgeProjectile.TryGetPierce(
                        data, out cap, out falloff, out explode);

                    int wCap;
                    float wFalloff;
                    bool wExplode;
                    bool haveWeapon = ForgeInterop.TryReadWeaponPierce(
                        __instance, out wCap, out wFalloff, out wExplode);

                    if (haveModule || haveWeapon)
                    {
                        int totalCap =
                            (haveModule ? cap : 0) + (haveWeapon ? wCap : 0);
                        float totalFalloff = Mathf.Max(
                            haveModule ? falloff : 0f, haveWeapon ? wFalloff : 0f);
                        bool totalExplode =
                            (haveModule && explode) || (haveWeapon && wExplode);

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
                        pc.limit = Mathf.Max(0, totalCap);
                        pc.falloff = totalFalloff;
                        pc.explodeOnLimit = totalExplode;
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
                    // NOTE: the field is a LayerMask struct, not an int -
                    // FieldRefAccess<Projectile,int> throws for a value-type
                    // mismatch, which is what silently broke phasing before.
                    _maskRef = AccessTools.FieldRefAccess<Projectile, LayerMask>(
                        "collisionLayerMask");
                    int g = LayerMask.NameToLayer("Ground");
                    _groundBit = (g >= 0) ? (1 << g) : 0;
                    _ready = true;
                }

                if (_maskRef != null && _groundBit != 0)
                {
                    LayerMask mask = _maskRef(projectile);
                    mask.value &= ~_groundBit;
                    _maskRef(projectile) = mask;
                }
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
