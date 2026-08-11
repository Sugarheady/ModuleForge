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

        private static MethodInfo _sfxResolve;   // ForgeSfxRegistry.Resolve(...)
        private static MethodInfo _sfxKnownGuid; // ForgeSfxRegistry.KnownGuid(string)
        private static MethodInfo _soundLoadAll; // ForgeSoundLibrary.LoadAll()

        private static MethodInfo _iconResolve;  // ForgeSpriteLibrary.ResolveIconSprite(string)
        private static PropertyInfo _spriteCount; // ForgeSpriteLibrary.Count

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

                // WeaponForge owns the custom-audio pipeline (it loads a
                // "sounds" folder, decodes WAV itself, and registers each clip
                // as a real Sfx in the game's audio database). Rather than
                // duplicating all of that here, ModuleForge borrows it when
                // WeaponForge is installed - so there is ONE sounds folder and
                // one set of names across both mods.
                Type sfx = AccessTools.TypeByName("WeaponForge.ForgeSfxRegistry");
                if (sfx != null)
                {
                    _sfxResolve = AccessTools.Method(sfx, "Resolve");
                    _sfxKnownGuid = AccessTools.Method(sfx, "KnownGuid");
                }

                Type lib = AccessTools.TypeByName("WeaponForge.ForgeSoundLibrary");
                if (lib != null)
                    _soundLoadAll = AccessTools.Method(lib, "LoadAll");

                // Same bargain for custom ART. Weapon Forge owns the "sprites"
                // folder, the PNG decode and the sheet slicer; borrowing it
                // means one folder and one set of names across both mods,
                // rather than a second copy of all of that here.
                Type sprites = AccessTools.TypeByName(
                    "WeaponForge.ForgeSpriteLibrary");
                if (sprites != null)
                {
                    _iconResolve = AccessTools.Method(
                        sprites, "ResolveIconSprite", new[] { typeof(string) });
                    _spriteCount = AccessTools.Property(sprites, "Count");
                }
            }
            catch
            {
                // WeaponForge absent or incompatible - stay dormant.
            }
        }

        // Turn a custom sound NAME into a registered Sfx guid, via WeaponForge.
        //
        // Returns null when WeaponForge is not installed, or when the name is
        // not one of its loaded sounds - in both cases the caller should treat
        // the value as a plain guid and pass it through untouched.
        public static string TryResolveSound(string name, string fileName)
        {
            Ensure();

            if (_sfxResolve == null || string.IsNullOrEmpty(name))
                return null;

            try
            {
                // Load order between two independent BepInEx plugins is not
                // guaranteed, so make sure the folder has been scanned before
                // asking about a name. LoadAll is idempotent.
                if (_soundLoadAll != null)
                    _soundLoadAll.Invoke(null, null);

                // Resolve(soundName, inheritFrom, forceLoop, fileName).
                // No slot to inherit from here and it is a one-shot click.
                return (string)_sfxResolve.Invoke(
                    null, new object[] { name, null, false, fileName });
            }
            catch
            {
                return null;
            }
        }

        // Does the GAME already know this sound id? Answers true when we cannot
        // tell (WeaponForge absent), so a stock guid is never wrongly reported
        // as a typo.
        public static bool IsKnownSoundGuid(string guid)
        {
            Ensure();

            if (_sfxKnownGuid == null || string.IsNullOrEmpty(guid))
                return true;

            try
            {
                return (bool)_sfxKnownGuid.Invoke(null, new object[] { guid });
            }
            catch
            {
                return true;
            }
        }

        // Turn a custom art NAME into a module-icon Sprite, via WeaponForge.
        //
        // Returns null when WeaponForge is not installed or when the name is
        // not one of its loaded sprites; the caller should then treat it as a
        // stock sprite name. WeaponForge does the folder scan itself (its
        // LoadAll is idempotent), so plugin load order does not matter, and it
        // rebuilds the sprite at the scale the game's own icons use.
        public static Sprite TryResolveIcon(string name)
        {
            Ensure();

            if (_iconResolve == null || string.IsNullOrEmpty(name))
                return null;

            try
            {
                return _iconResolve.Invoke(null, new object[] { name })
                    as Sprite;
            }
            catch
            {
                return null;
            }
        }

        // True when WeaponForge's sprite pipeline is available at all.
        public static bool HasCustomSprites
        {
            get
            {
                Ensure();
                return _iconResolve != null;
            }
        }

        // How many custom sprites WeaponForge loaded - only for wording a
        // warning ("you have none" vs "that is not one of them").
        public static int CustomSpriteCount
        {
            get
            {
                Ensure();

                if (_spriteCount == null)
                    return 0;

                try
                {
                    return (int)_spriteCount.GetValue(null, null);
                }
                catch
                {
                    return 0;
                }
            }
        }

        // True when WeaponForge's audio pipeline is available at all.
        public static bool HasCustomSounds
        {
            get
            {
                Ensure();
                return _sfxResolve != null;
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
