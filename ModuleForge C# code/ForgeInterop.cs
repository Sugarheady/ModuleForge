using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // Reads WeaponForge's per-weapon baked phasing / pierce by reflection (no
    // assembly reference), so ModuleForge can COMBINE them with its own module
    // effects into a single additive pierce cap and one stat line. When both
    // mods are installed ModuleForge is the pierce authority and WeaponForge
    // stands down (see WeaponForge.ForgePierceCompat). All lookups are cached;
    // if WeaponForge isn't installed, everything returns "nothing".
    public static class ForgeInterop
    {
        private static bool _init;

        private static Type _pierceCapType;     // WeaponForge.ForgePierceCap (component)
        private static FieldInfo _fLimit, _fFalloff, _fExplode;

        private static MethodInfo _isPhasing;   // ForgeWeaponInfo.IsPhasing(WeaponData)
        private static MethodInfo _tryGetPierce; // ForgeWeaponInfo.TryGetPierce(WeaponData, out int)

        private static void Ensure()
        {
            if (_init)
                return;
            _init = true;

            try
            {
                _pierceCapType = AccessTools.TypeByName("WeaponForge.ForgePierceCap");
                if (_pierceCapType != null)
                {
                    _fLimit = AccessTools.Field(_pierceCapType, "limit");
                    _fFalloff = AccessTools.Field(_pierceCapType, "falloff");
                    _fExplode = AccessTools.Field(_pierceCapType, "explodeOnLimit");
                }

                Type info = AccessTools.TypeByName("WeaponForge.ForgeWeaponInfo");
                if (info != null)
                {
                    _isPhasing = AccessTools.Method(
                        info, "IsPhasing", new[] { typeof(WeaponData) });
                    _tryGetPierce = AccessTools.Method(info, "TryGetPierce");
                }
            }
            catch
            {
                // WeaponForge absent or incompatible - stay dormant.
            }
        }

        // Runtime: the WeaponForge pierce cap baked onto THIS projectile (the
        // weapon's own pierceLimit), read off its ForgePierceCap component.
        public static bool TryReadWeaponPierce(
            Component projectile, out int limit, out float falloff, out bool explode)
        {
            limit = 0; falloff = 0f; explode = false;
            Ensure();
            if (_pierceCapType == null || _fLimit == null || projectile == null)
                return false;

            try
            {
                var comp = projectile.GetComponent(_pierceCapType);
                if (comp == null)
                    return false;
                limit = (int)_fLimit.GetValue(comp);
                if (_fFalloff != null) falloff = (float)_fFalloff.GetValue(comp);
                if (_fExplode != null) explode = (bool)_fExplode.GetValue(comp);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Tooltip: the weapon's own baked pierce limit (0 if none), by template.
        public static int WeaponBakedPierce(WeaponData weapon)
        {
            Ensure();
            if (_tryGetPierce == null || weapon == null)
                return 0;
            try
            {
                var args = new object[] { weapon, 0 };
                bool ok = (bool)_tryGetPierce.Invoke(null, args);
                return ok ? (int)args[1] : 0;
            }
            catch
            {
                return 0;
            }
        }

        // Tooltip: whether the weapon itself is a phasing weapon.
        public static bool WeaponBakedPhasing(WeaponData weapon)
        {
            Ensure();
            if (_isPhasing == null || weapon == null)
                return false;
            try
            {
                return (bool)_isPhasing.Invoke(null, new object[] { weapon });
            }
            catch
            {
                return false;
            }
        }
    }
}
