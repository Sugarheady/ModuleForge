using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // THE WEAPON'S FEEL - sound, muzzle flash, camera shake, gamepad rumble.
    //
    // Tier 4 of STAT GAPS - BUILD LIST.md, the last thing on that page. Ten
    // properties on `WeaponBase` that the game's own effects cannot reach:
    // six sound ids, two particle prefabs, and the two presets behind the
    // camera kick and the controller buzz.
    //
    // WHY THE SEAM IS SAFE, because it is the first thing that would sink this.
    // `WeaponFactory.Create` does: construct (which copies every one of these
    // ten out of `WeaponData`) -> run every `IWeaponModifier.Modify` -> return.
    // `InitializeVisuals()` is NOT called there; `ModuleSlotWeaponHolder`
    // calls it on the line AFTER `Create` returns. So a write here lands after
    // the template copy and before the visuals are built.
    //
    // That ordering is load-bearing rather than incidental:
    // `InitializeVisuals` only reconciles the muzzle instance COUNT against
    // `ProjectileCount` - it never notices the prefab itself changed. Had
    // `Modify` run after it, a muzzle swap would have kept the old flash with
    // nothing in the log.
    //
    // WEAPON GRID ONLY, by construction rather than by a flag: `Modify` is
    // only ever called for modules in a weapon's cluster. That is the same
    // trick `KillRewardEffect` uses to scope itself, and it is why this class
    // has no `target` field and no `OnInstalled` - which also keeps it on the
    // right side of `modlivetest.py` (a pure `IWeaponModifier` is already
    // gated by the cluster walk, so it needs no `ModuleForgeLive` plumbing).
    //
    // ALL TEN ARE LIVE. Checked read-by-read before building, because a
    // cosmetic module writing a field nothing reads is a silent no-op and this
    // mod's own build list already carries two dead fields under DO NOT BUILD.
    // `ShootSfx` is read by `WeaponBase.DoShoot`; the other five sounds and
    // `ReloadParticlePrefab` by `Shooter`; `ShakePreset` by `ShipCameraShaker`
    // and `RumblePreset` by `ShipGamepadRumble`, both off the live weapon.
    [Serializable]
    public class WeaponCosmeticEffect
        : ModuleEffect, IWeaponModifier, IHasDescriptionForWeapon
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge");

        // ---- the six sounds ------------------------------------------------
        //
        // Held as parallel arrays rather than twelve fields. The GUID is what
        // the game wants (`AudioManager` matches `s.guid == guid`, so a NAME
        // assigned here would play nothing and return -1 with nobody
        // checking); the name is what the card shows, because a 36-character
        // guid on a module card tells the player nothing.
        public const int Shoot = 0;
        public const int Continuous = 1;
        public const int Start = 2;
        public const int Release = 3;
        public const int Warmup = 4;
        public const int Reload = 5;
        public const int SfxCount = 6;

        public string[] sfxGuid = new string[SfxCount];
        public string[] sfxName = new string[SfxCount];

        public static string SfxLabel(int slot)
        {
            switch (slot)
            {
                case Continuous: return "HELD SFX";
                case Start:      return "START SFX";
                case Release:    return "RELEASE SFX";
                case Warmup:     return "SPIN-UP SFX";
                case Reload:     return "RELOAD SFX";
                default:         return "SHOT SFX";
            }
        }

        public static string SfxKey(int slot)
        {
            switch (slot)
            {
                case Continuous: return "continuousShootSfx";
                case Start:      return "startSfx";
                case Release:    return "releaseSfx";
                case Warmup:     return "warmupSfx";
                case Reload:     return "reloadSfx";
                default:         return "shootSfx";
            }
        }

        // ---- the two particle prefabs --------------------------------------
        public ParticleSystem muzzleParticle;
        public ParticleSystem reloadParticle;

        // ---- shake and rumble ----------------------------------------------
        //
        // TWO WAYS TO ASK, and the scale is the one people want. "This gun
        // kicks 50% harder" works on any weapon and needs no asset names;
        // naming a preset is the escape hatch for putting a kick on a gun that
        // never had one. Same shape as Weapon Forge's `projectileGlow` / an
        // explicit material name.
        //
        // The preset is typed `UnityEngine.Object` ON PURPOSE. `ShakePreset`
        // is a `ScriptableObject` in **ProCamera2D**, a third-party assembly
        // NEITHER mod references - naming the type in a field would drag that
        // reference back in. `ModuleForgeCrit` already reaches it the same way.
        public UnityEngine.Object shakePreset;
        public bool hasShakeScale;
        public FloatSeries shakeScale;

        // RumblePreset is Punk.Main's own, so this half needs no reflection.
        public RumblePreset rumblePreset;
        public bool hasRumbleScale;
        public FloatSeries rumbleScale;

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        private float ShakeFor { get { return Clamp(shakeScale.GetElement(Level - 1)); } }
        private float RumbleFor { get { return Clamp(rumbleScale.GetElement(Level - 1)); } }

        // A negative multiplier would mirror the shake rather than shrink it,
        // and an unbounded one can make the game genuinely unplayable - a
        // module is allowed to be strong, not to be a denial of service.
        private const float MaxScale = 5f;

        private static float Clamp(float v)
        {
            if (v < 0f) return 0f;
            return (v > MaxScale) ? MaxScale : v;
        }

        // ---- the effect ----------------------------------------------------
        public void Modify(WeaponBase weaponBase)
        {
            if (weaponBase == null)
                return;

            try
            {
                for (int i = 0; i < SfxCount; i++)
                    AssignSfx(weaponBase, i);

                // Only ever ASSIGN what was asked for. A null here would mute
                // a weapon that had a perfectly good sound of its own, which
                // is a cosmetic module doing damage.
                if (muzzleParticle != null)
                    weaponBase.MuzzleParticlePrefab = muzzleParticle;

                if (reloadParticle != null)
                    weaponBase.ReloadParticlePrefab = reloadParticle;

                ApplyShake(weaponBase);
                ApplyRumble(weaponBase);
            }
            catch (Exception e)
            {
                // A cosmetic must never cost a weapon. Anything unexpected
                // leaves the gun exactly as the template built it.
                Log.LogWarning(
                    "A cosmetic module could not be applied (" + e.Message +
                    "). The weapon keeps its own sound and feel.");
            }
        }

        private void AssignSfx(WeaponBase w, int slot)
        {
            string guid = (sfxGuid != null && slot < sfxGuid.Length)
                ? sfxGuid[slot] : null;

            if (string.IsNullOrEmpty(guid))
                return;

            switch (slot)
            {
                case Continuous: w.ContinousShootSfx = guid; break;
                case Start:      w.StartSfx = guid; break;
                case Release:    w.ReleaseSfx = guid; break;
                case Warmup:     w.WarmupSfx = guid; break;
                case Reload:     w.ReloadSfx = guid; break;
                default:         w.ShootSfx = guid; break;
            }
        }

        // ---- shake ----------------------------------------------------------
        private static PropertyInfo _shakeProp;
        private static bool _shakeLooked;

        // Swept by ModuleForgeDiagnosticGates on its NAME, so a second run in
        // one session still reports it - the thing it describes is per run.
        private static bool _saidNoShake;
        private static bool _saidNoRumble;

        private static PropertyInfo ShakeProp()
        {
            if (_shakeLooked)
                return _shakeProp;

            _shakeLooked = true;

            // The PROPERTY carries the type, so there is no need to name
            // ProCamera2D's assembly or guess at a type string.
            _shakeProp = HarmonyLib.AccessTools.Property(
                typeof(WeaponBase), "ShakePreset");

            if (_shakeProp == null)
            {
                Log.LogInfo(
                    "Camera shake is not reachable on this build of the " +
                    "game, so a cosmetic module's \"shake\" does nothing. " +
                    "Everything else in the module still works.");
            }

            return _shakeProp;
        }

        // The builder needs the type to look a preset up by name, and cannot
        // name it either - so it asks here rather than repeating the trick.
        public static Type ShakePresetType()
        {
            PropertyInfo p = ShakeProp();
            return (p != null) ? p.PropertyType : null;
        }

        private void ApplyShake(WeaponBase w)
        {
            PropertyInfo prop = ShakeProp();

            if (prop == null)
                return;

            // A named preset REPLACES, and wins over a scale: naming one is
            // the deliberate act, scaling is the adjustment.
            if (shakePreset != null)
            {
                prop.SetValue(w, shakePreset, null);
                return;
            }

            if (!hasShakeScale)
                return;

            object own = prop.GetValue(w, null);

            // A Unity object that has been destroyed compares equal to null
            // through the == operator but NOT through a plain reference test,
            // which is why this goes through the Unity comparison.
            if (own == null || (own as UnityEngine.Object) == null)
            {
                if (!_saidNoShake)
                {
                    _saidNoShake = true;

                    Log.LogInfo(
                        "A cosmetic module asked to scale the camera shake, " +
                        "but this weapon has NO shake preset of its own - so " +
                        "there is nothing to scale and the module's \"shake\" " +
                        "does nothing on it. Scaling deliberately does not " +
                        "invent a kick on a gun that never had one (a beam " +
                        "fires continuously, and a per-shot shake on one is " +
                        "unpleasant). Use \"shakePreset\" with a name - e.g. " +
                        "\"Shake_Gunshot_Small\" - to give it one outright.");
                }

                return;
            }

            object scaled = ScaledShake(own, ShakeFor);

            if (scaled != null)
                prop.SetValue(w, scaled, null);
        }

        // ONE-ENTRY CACHE, keyed on the source. `Modify` runs whenever the
        // weapon is rebuilt (any module added or removed), so making a fresh
        // ScriptableObject every time would litter one per rebuild for the
        // whole run. The old one is NOT destroyed: a WeaponBase we modified
        // earlier may still be holding it, and handing the camera a destroyed
        // object to read is a worse trade than a handful of tiny assets.
        private UnityEngine.Object _shakeSource;
        private UnityEngine.Object _shakeMade;

        private object ScaledShake(object source, float k)
        {
            var src = source as UnityEngine.Object;

            if (src == null)
                return null;

            if (_shakeSource == src && _shakeMade != null)
                return _shakeMade;

            Type t = source.GetType();

            var made = ScriptableObject.CreateInstance(t);

            if (made == null)
                return null;

            // Copy EVERY public field, then scale the one that means
            // "how hard". Copying wholesale rather than listing the nine keeps
            // vibrato, randomness, smoothness and the angles exactly as the
            // weapon's own preset had them, so the kick still feels like that
            // gun - just more of it.
            FieldInfo[] fields = t.GetFields(
                BindingFlags.Public | BindingFlags.Instance);

            for (int i = 0; i < fields.Length; i++)
            {
                try { fields[i].SetValue(made, fields[i].GetValue(source)); }
                catch (Exception) { }
            }

            // STRENGTH ONLY, not Duration. "Harder" and "longer" are different
            // asks, and a 5x-duration shake is nausea rather than feedback.
            FieldInfo strength = HarmonyLib.AccessTools.Field(t, "Strength");

            if (strength != null && strength.FieldType == typeof(Vector3))
            {
                try
                {
                    var v = (Vector3)strength.GetValue(made);
                    strength.SetValue(made, v * k);
                }
                catch (Exception) { }
            }

            made.name = "ModuleForge Shake x" + k.ToString("0.##");
            made.hideFlags = HideFlags.HideAndDontSave;

            _shakeSource = src;
            _shakeMade = made;

            return made;
        }

        // ---- rumble ---------------------------------------------------------
        private void ApplyRumble(WeaponBase w)
        {
            if (rumblePreset != null)
            {
                w.RumblePreset = rumblePreset;
                return;
            }

            if (!hasRumbleScale)
                return;

            RumblePreset own = w.RumblePreset;

            if (own == null)
            {
                if (!_saidNoRumble)
                {
                    _saidNoRumble = true;

                    Log.LogInfo(
                        "A cosmetic module asked to scale the gamepad " +
                        "rumble, but this weapon has NO rumble preset of its " +
                        "own, so there is nothing to scale. Use " +
                        "\"rumblePreset\" with a name - e.g. \"Rumble " +
                        "WeaponShot Large\" - to give it one outright.");
                }

                return;
            }

            RumblePreset scaled = ScaledRumble(own, RumbleFor);

            if (scaled != null)
                w.RumblePreset = scaled;
        }

        private RumblePreset _rumbleSource;
        private RumblePreset _rumbleMade;

        private RumblePreset ScaledRumble(RumblePreset source, float k)
        {
            if (source == null)
                return null;

            if (_rumbleSource == source && _rumbleMade != null)
                return _rumbleMade;

            var made = ScriptableObject.CreateInstance<RumblePreset>();

            // MOTOR SPEEDS ARE CLAMPED TO 1 because that is the hardware's
            // range, not a taste call: `Gamepad.SetMotorSpeeds` takes 0..1 and
            // a 3x on a preset already at 0.8 is simply 1 with the extra
            // thrown away. Clamping here means the card and the controller
            // agree about what the module did.
            made.leftMotorSpeed = Mathf.Clamp01(source.leftMotorSpeed * k);
            made.rightMotorSpeed = Mathf.Clamp01(source.rightMotorSpeed * k);
            made.duration = source.duration;

            made.name = "ModuleForge Rumble x" + k.ToString("0.##");
            made.hideFlags = HideFlags.HideAndDontSave;

            _rumbleSource = source;
            _rumbleMade = made;

            return made;
        }

        // ---- clone ----------------------------------------------------------
        //
        // EVERY FIELD. `Module`'s constructor Clones each effect off the
        // ModuleData, so the clone is the copy the game actually runs - a
        // missing line here is invisible in the log and reads as "the module
        // does nothing". The two arrays are COPIED rather than shared, per the
        // house rule about handing over an instance somebody else keeps.
        public override ModuleEffect Clone()
        {
            var copy = new WeaponCosmeticEffect
            {
                muzzleParticle = muzzleParticle,
                reloadParticle = reloadParticle,
                shakePreset = shakePreset,
                hasShakeScale = hasShakeScale,
                shakeScale = shakeScale,
                rumblePreset = rumblePreset,
                hasRumbleScale = hasRumbleScale,
                rumbleScale = rumbleScale
            };

            copy.sfxGuid = new string[SfxCount];
            copy.sfxName = new string[SfxCount];

            for (int i = 0; i < SfxCount; i++)
            {
                if (sfxGuid != null && i < sfxGuid.Length)
                    copy.sfxGuid[i] = sfxGuid[i];

                if (sfxName != null && i < sfxName.Length)
                    copy.sfxName[i] = sfxName[i];
            }

            return copy;
        }

        // ---- the card ------------------------------------------------------
        //
        // A new stat is not finished until it shows on the module card - the
        // card is the only place a player learns what a module does. Cosmetics
        // are no exception: a module that changes the feel of your gun and
        // says nothing is indistinguishable from one that is broken.
        public void GetDescription(
            WeaponBase weapon,
            bool isInstalled,
            List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            for (int i = 0; i < SfxCount; i++)
            {
                string guid = (sfxGuid != null && i < sfxGuid.Length)
                    ? sfxGuid[i] : null;

                if (string.IsNullOrEmpty(guid))
                    continue;

                string shown = (sfxName != null && i < sfxName.Length &&
                                !string.IsNullOrEmpty(sfxName[i]))
                    ? sfxName[i]
                    : "changed";

                properties.Add(new DisplayableProperty(
                    Caps(SfxLabel(i)), Short(shown)));
            }

            if (muzzleParticle != null)
            {
                properties.Add(new DisplayableProperty(
                    Caps("MUZZLE"), Short(Trim(muzzleParticle.name))));
            }

            if (reloadParticle != null)
            {
                properties.Add(new DisplayableProperty(
                    Caps("RELOAD FX"), Short(Trim(reloadParticle.name))));
            }

            if (shakePreset != null)
            {
                properties.Add(new DisplayableProperty(
                    Caps("SHAKE"), Short(Trim(shakePreset.name))));
            }
            else if (hasShakeScale)
            {
                properties.Add(new DisplayableProperty(
                    Caps("SHAKE"), "x" + ShakeFor.ToString("0.##")));
            }

            // GAMEPAD ONLY, and the card says so rather than the player
            // discovering it. `ShipGamepadRumble.Rumble` returns immediately
            // unless `shipInput.UsesGamepad`, and it is scaled again by a
            // per-player rumble setting in the options - so this is a module
            // that can do nothing through no fault of its own, twice over.
            // That is the same shape that got AimAssistData ruled out; the
            // difference is that this one is pure feel and cannot cost you a
            // fight, so it ships with the caveat printed.
            if (rumblePreset != null)
            {
                properties.Add(new DisplayableProperty(
                    Caps("RUMBLE (PAD)"), Short(Trim(rumblePreset.name))));
            }
            else if (hasRumbleScale)
            {
                properties.Add(new DisplayableProperty(
                    Caps("RUMBLE (PAD)"), "x" + RumbleFor.ToString("0.##")));
            }
        }

        private static string Caps(string s)
        {
            return TextFormatter.ColoredText(TextFormatter.capsColor, s);
        }

        // The card is a narrow column beside the grid, so a long hierarchical
        // sound name ("Weapons/Laser/LaserBigCharged") would push the value
        // off it. The tail is the part that identifies the sound.
        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "changed";

            int slash = s.LastIndexOf('/');

            if (slash >= 0 && slash < s.Length - 1)
                s = s.Substring(slash + 1);

            return (s.Length > 16) ? s.Substring(0, 16) : s;
        }

        // Stock prefabs and presets carry a category word that is the same on
        // every one of them, so it identifies nothing on a card.
        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            string[] prefixes =
            {
                "MuzzleParticle ", "Rumble WeaponShot ", "Rumble ", "Shake_"
            };

            for (int i = 0; i < prefixes.Length; i++)
            {
                if (s.StartsWith(prefixes[i],
                                 StringComparison.OrdinalIgnoreCase))
                {
                    return s.Substring(prefixes[i].Length);
                }
            }

            return s;
        }
    }
}
