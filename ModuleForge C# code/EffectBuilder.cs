using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ModuleForge
{
    // Builds ModuleEffect instances from JSON "effects" entries. Each
    // entry is { "type": "...", ...params }. Magnitudes use FloatSeries;
    // a plain number is treated as a flat (level-independent) value.
    public static class EffectBuilder
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge");

        // Cached reflection for ModifyWeaponProperty's private fields.
        private static FieldInfo _mwpTarget;
        private static FieldInfo _mwpOperation;
        private static FieldInfo _mwpDeltaMode;
        private static FieldInfo _mwpValue;

        public static ModuleEffect Build(JObject entry, string fileName)
        {
            string type = (string)entry["type"];

            if (string.IsNullOrEmpty(type))
            {
                Log.LogWarning(fileName + ": an effect has no \"type\".");
                return null;
            }

            try
            {
                switch (type.Trim().ToLowerInvariant())
                {
                    case "modifyresourcecapacity":
                        return new ModifyResourceCapacity
                        {
                            resource = Res(entry, fileName),
                            delta = Series(entry["delta"] ?? entry["amount"])
                        };

                    case "resourceautochargeeffect":
                    case "regen":
                        return new ResourceAutoChargeEffect
                        {
                            resource = Res(entry, fileName),
                            rechargeRate =
                                Series(entry["rechargeRate"] ?? entry["amount"])
                        };

                    case "drainresourceeffect":
                    case "drain":
                        return new DrainResourceEffect
                        {
                            resource = Res(entry, fileName),
                            drainRate =
                                Series(entry["drainRate"] ?? entry["amount"])
                        };

                    case "addshieldeffect":
                    case "shield":
                        return new AddShieldEffect
                        {
                            resource = Res(entry, fileName),
                            effectiveness =
                                Series(entry["effectiveness"] ?? entry["amount"])
                        };

                    // "killrewardeffect" is the CLASS NAME, and it is what the
                    // builder page emits - every other case here lists its class
                    // name for exactly that reason. Leaving it out meant the page
                    // generated `"type": "KillRewardEffect"` and this mod rejected
                    // its own output with "unknown effect type", so the module
                    // built with zero effects and did nothing.
                    case "killrewardeffect":
                    case "killreward":
                    case "onkill":
                    case "resourceonkill":
                        return BuildKillReward(entry, fileName);

                    // CLASS NAME FIRST, as above. Spelled `contactDamage` to
                    // match Weapon Forge exactly - one word for one idea across
                    // both mods is a rule here, and this is the block most
                    // likely to be copied between them.
                    case "contactdamageeffect":
                    case "contactdamage":
                    case "contact":
                    case "ram":
                    case "spikes":
                        return BuildContactDamage(entry, fileName);

                    // CLASS NAME FIRST, as above. "finish" and "finisher" are
                    // offered because that is what the mechanic is usually
                    // called outside this codebase; "execute" is the name the
                    // weapon side already uses and the two mods must spell one
                    // idea one way.
                    case "executeeffect":
                    case "execute":
                    case "finish":
                    case "finisher":
                        return BuildExecute(entry, fileName);

                    // CLASS NAME FIRST. "growth" is accepted because Weapon
                    // Forge's component is called ForgeGrowth while its JSON key
                    // is "grow", and somebody reading one and writing the other
                    // should not be punished for it.
                    case "groweffect":
                    case "grow":
                    case "growth":
                    case "growingshot":
                        return BuildGrow(entry, fileName);

                    // CLASS NAME FIRST, as always. "timeslow" and "hitstop" are
                    // accepted because the time-bend half is the reason many
                    // people reach for this at all, and a file that names only
                    // that half should still find the effect - the two halves
                    // live in one block exactly as they do on the weapon side.
                    case "sloweffect":
                    case "slow":
                    case "chill":
                    case "timeslow":
                    case "hitstop":
                        return BuildSlow(entry, fileName);

                    // CLASS NAME FIRST, as always.
                    //
                    // ★ "spinup" IS DELIBERATELY NOT HERE, and that is the one
                    // case label in this switch whose ABSENCE is the design.
                    // It belongs to `ExtraWeaponStatEffect` (the game's flat
                    // `WarmupTime`) and was already spoken for when this was
                    // built. Weapon Forge calls this same feature `spinUp`, so
                    // somebody who knows that mod WILL write it here and get a
                    // one-off delay change with no meter - which is why the
                    // other case warns rather than leaving them to find out.
                    case "windupeffect":
                    case "windup":
                    case "rampup":
                    case "spool":
                        return BuildWindup(entry, fileName);

                    // "criteffect" is the CLASS NAME and it goes FIRST for the
                    // reason the comment above records: the builder page emits
                    // "type": "<ClassName>", and a switch listing only the
                    // friendly aliases makes the mod reject JSON its own page
                    // wrote.
                    case "criteffect":
                    case "crit":
                    case "critical":
                    case "critchance":
                    case "criticalhit":
                        return BuildCrit(entry, fileName);

                    // CLASS NAME FIRST, as above - the page emits
                    // "type": "LeechEffect".
                    //
                    // NOT "drain": that alias already belongs to
                    // DrainResourceEffect ("bleed this tank per second"), and a
                    // duplicate case here would not even compile - which is the
                    // lucky version of the "ammo" collision recorded above.
                    case "leecheffect":
                    case "leech":
                    case "lifesteal":
                    case "healonhit":
                    case "vampiric":
                        return BuildLeech(entry, fileName);

                    case "modifyweaponproperty":
                    case "weaponstat":
                        return BuildWeaponProperty(entry, fileName);

                    // The CLASS NAME is first on purpose: the builder page
                    // emits "type": "<ClassName>", and a switch that lists only
                    // the friendly aliases means the mod rejects JSON its own
                    // page wrote. That has cost a test round once already.
                    case "extraweaponstateffect":
                    case "extraweaponstat":
                    case "pushforce":
                    case "push":
                    case "warmuptime":
                    case "spinup":
                        return BuildExtraWeaponStat(entry, type, fileName);

                    // The CLASS NAME first, as above. NOT a bare "sound" or
                    // "reload": "reload" already belongs to BuildAmmo and a
                    // duplicate case would silently steal it, which is the
                    // collision this switch has paid for twice.
                    case "weaponcosmeticeffect":
                    case "weaponcosmetic":
                    case "cosmetic":
                    case "feel":
                    case "shake":
                    case "rumble":
                    case "muzzle":
                    case "weaponsfx":
                        return BuildWeaponCosmetic(entry, fileName);

                    case "weaponresourceeffect":
                    case "weaponresource":
                    case "ammo":
                    case "resourceused":
                        return BuildWeaponResource(entry, fileName);

                    case "shipstateffect":
                    case "shipstat":
                    case "speed":
                    case "engine":
                    case "dash":
                    case "iframes":
                        return BuildShipStat(entry, type, fileName);

                    // NOT "ammo" - that alias already belongs to
                    // BuildWeaponResource ("which resource this weapon
                    // spends"), and a duplicate case would silently steal it.
                    // The CLASS NAME must be here: the builder page emits
                    // "type": "AmmoEffect" and a switch that does not list it
                    // rejects JSON its own page wrote.
                    case "cellconverteffect":
                    case "cellconvert":
                    case "terraform":
                    case "dig":
                        return BuildCellConvert(entry, fileName);

                    case "ammoeffect":
                    case "infiniteammo":
                    case "freeammo":
                    case "reload":
                        return BuildAmmo(entry, fileName);

                    case "stealtheffect":
                    case "stealth":
                    case "cloak":
                    case "invisible":
                        return BuildStealth(entry, fileName);

                    case "laststandeffect":
                    case "laststand":
                    case "cannotdie":
                        return BuildLastStand(entry, fileName);

                    case "invulneffect":
                    case "invuln":
                    case "invulnerability":
                    case "mercy":
                        return new InvulnEffect
                        {
                            seconds = SeriesOr(
                                entry["seconds"] ?? entry["duration"] ??
                                entry["amount"] ?? entry["value"], 0.5f)
                        };

                    case "increaseexplosionradiuseffect":
                    case "explosionradius":
                        return new IncreaseExplosionRadiusEffect
                        {
                            increaseAmount =
                                Flat(entry["increaseAmount"], 1f)
                        };

                    case "addimpactexplosioneffect":
                    case "impactexplosion":
                        return new AddImpactExplosionEffect();

                    case "addburneffect":
                    case "burn":
                        return new AddBurnEffect
                        {
                            amount = Series(entry["amount"]),
                            costPerProjectile =
                                Flat(entry["costPerProjectile"], 0f),
                            costResource =
                                ForgeAssets.ResolveResource(
                                    (string)entry["costResource"])
                        };

                    // THE CLASS NAME FIRST. `KnownEffects` below advertises
                    // class names and states in a comment that one is always
                    // an accepted spelling - this case, `phasing` and
                    // `piercecap` were the three where that was not true, so
                    // the unknown-type warning was recommending three
                    // spellings the switch would reject.
                    case "burnratemoduleeffect":
                    case "burntickrateeffect":
                    case "burntickrate":
                    case "burnrate":
                    case "burnspeed":
                    case "quickenburn":
                        return new BurnRateModuleEffect
                        {
                            ticksPerSecond =
                                Series(entry["ticksPerSecond"] ??
                                       entry["amount"] ?? entry["value"])
                        };

                    case "burncoloreffect":
                    case "burncolor":
                    case "burntint":
                        return BuildBurnColor(entry, fileName);

                    case "phasingmoduleeffect":
                    case "phasing":
                    case "phase":
                    case "noclip":
                        return new PhasingModuleEffect();

                    case "piercemoduleeffect":
                    case "piercecap":
                    case "pierce":
                    case "piercing":
                        return new PierceModuleEffect
                        {
                            // A series, so it can scale with level - and a
                            // FLOAT, so 1.5 means "a second pierce half the
                            // time". Defaults to a flat 2 when absent, which
                            // is what it has always been.
                            pierceCap = SeriesOr(
                                entry["pierceCap"] ?? entry["cap"], 2f),
                            falloff = Series(
                                entry["falloff"] ??
                                entry["pierceDamageFalloff"]),
                            explodeOnLimit =
                                FlatBool(entry["explodeOnLimit"] ??
                                entry["pierceExplodeOnLimit"], false)
                        };

                    case "addexplosioneffect":
                    case "explosion":
                        return new AddExplosionEffect
                        {
                            damageType =
                                ForgeAssets.ResolveResource(
                                    (string)entry["damageType"]),
                            damageAmount = Series(entry["damageAmount"]),
                            costPerProjectile =
                                Flat(entry["costPerProjectile"], 0f),
                            costResource =
                                ForgeAssets.ResolveResource(
                                    (string)entry["costResource"]),
                            addImpactExplosion =
                                FlatBool(entry["addImpactExplosion"], true),
                            addTimeoutExplosion =
                                FlatBool(entry["addTimeoutExplosion"], false),
                            explosionRadiusIncrement =
                                Flat(entry["explosionRadiusIncrement"], 0f),
                            burn = Series(entry["burn"])
                        };

                    case "adddischargeeffect":
                    case "discharge":
                    case "spark":
                        return new AddDischargeEffect
                        {
                            chainLengthIncrement =
                                FlatInt(entry["chainLengthIncrement"], 1),
                            damageIncrement = Series(entry["damageIncrement"]),
                            impact = FlatBool(entry["impact"], true),
                            timeout = FlatBool(entry["timeout"], false),
                            costPerProjectile =
                                FlatInt(entry["costPerProjectile"], 0),
                            costResource =
                                ForgeAssets.ResolveResource(
                                    (string)entry["costResource"])
                        };

                    default:
                        // NAMING THE INPUT IS NOT NAMING THE CAPABILITY, and
                        // this warning was the pure form of that mistake.
                        //
                        // "unknown effect type 'orbit' - skipped" is true and
                        // it sends the reader to check their SPELLING, when the
                        // answer is often "there is no such effect in this mod
                        // at all" - orbit, for instance, is a Weapon Forge
                        // weapon block, not a module effect. His R13 log
                        // carried `module 'Orbit3' has no valid effects - it
                        // will do nothing`, which is the downstream symptom of
                        // exactly this.
                        //
                        // Same failure shape as the seven sprite keys that
                        // could not see stock art: loud, accurate, and pointing
                        // the wrong way.
                        Log.LogWarning(
                            fileName + ": unknown effect type '" + type +
                            "' - skipped, so this module will do nothing. " +
                            "Note the type is a CLASS NAME, and if what you " +
                            "want is not in this list it may be a Weapon Forge " +
                            "weapon feature rather than a module effect (orbit, " +
                            "chain, gas, split and the rest live there). " +
                            KnownEffects());
                        return null;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    fileName + ": failed to build effect '" + type +
                    "': " + e.Message);
                return null;
            }
        }

        // The effects that actually exist, DERIVED FROM THE ASSEMBLY rather
        // than typed out.
        //
        // A hand-written list is the obvious way and it would drift: every
        // effect added after it was written would be missing from the very
        // message that exists to tell you what is available, and nothing would
        // catch that. Scanning for `ModuleEffect` subclasses cannot drift,
        // because it is asking the same question the switch answers.
        //
        // It is exact rather than approximate for a documented reason: the
        // house rule here is that **every case in the switch must include its
        // own class name**, so a class name is always an accepted spelling.
        // (The switch also takes friendlier aliases - "crit", "onKill" - which
        // this deliberately does not list; the canonical name is the one worth
        // showing.)
        //
        // THAT GUARANTEE WAS FALSE FOR FIVE OF THE NAMES THIS PRINTED, and
        // found 2026-09-08 by reading the message in a real log rather than by
        // any test. Two separate causes, which is why it took a while to see:
        //
        //  * THREE were the house rule simply not kept - `BurnRateModuleEffect`,
        //    `PhasingModuleEffect` and `PierceModuleEffect` are class names the
        //    switch had no case for, because those three are reached by the
        //    friendlier `burnrate` / `phasing` / `piercecap` and nobody went
        //    back for the canonical spelling. Cases added.
        //  * TWO WERE ANOTHER MOD'S. This walked every loaded assembly, so with
        //    Weapon Forge installed it advertised `ForgeBurnColorEffect` and
        //    `ForgeBurnRateEffect` - real `ModuleEffect` subclasses that this
        //    mod's switch has never heard of and never will.
        //
        // Same family as the original `killrewardeffect` bug that cost a round:
        // **the mod rejecting a spelling its own diagnostics recommend.** And
        // the lesson is narrower than "derive it from the code" - a derived
        // list is only right if it is derived from the same source the decision
        // is made from. A scan of the whole AppDomain answers "what effect
        // classes exist anywhere", and the question is "what will my switch
        // accept".
        //
        // THE BOUNDARY IS TWO ASSEMBLIES, NOT ONE. Scoping this to Module
        // Forge's own assembly was the first attempt and was worse than the
        // bug: most of the effect types the switch builds are the GAME's own
        // `ModuleEffect` subclasses (`AddBurnEffect`, `ModifyWeaponProperty`,
        // `AddShieldEffect` and nine more), so a this-assembly-only scan would
        // have silently dropped twelve legitimate names from the very message
        // that exists to list them. Caught by a test asserting the count, which
        // is the only reason it did not ship.
        //
        // The exact rule: an effect the switch can build is declared either by
        // the game or by this mod, and never by anybody else. The game's
        // assembly is identified as the one that declares `ModuleEffect` itself
        // rather than by name, so there is nothing to keep in step.
        private static string _known;

        private static string KnownEffects()
        {
            if (_known != null)
                return _known;

            _known = "Could not list the available effects.";

            try
            {
                var names = new List<string>();

                var mine = new[]
                {
                    typeof(ModuleEffect).Assembly,      // the game's
                    typeof(EffectBuilder).Assembly,     // this mod's
                };

                for (int a = 0; a < mine.Length; a++)
                {
                    if (a > 0 && mine[a] == mine[0])
                        continue;

                    Type[] types;

                    // A partially-loaded assembly throws here, and a warning
                    // helper must never be the thing that breaks a build.
                    try { types = mine[a].GetTypes(); }
                    catch (ReflectionTypeLoadException e)
                    {
                        types = e.Types ?? new Type[0];
                    }
                    catch (Exception) { continue; }

                    foreach (Type t in types)
                    {
                        if (t == null || t.IsAbstract)
                            continue;

                        if (typeof(ModuleEffect).IsAssignableFrom(t))
                            names.Add(t.Name);
                    }
                }

                if (names.Count == 0)
                    return _known;

                names.Sort(StringComparer.OrdinalIgnoreCase);

                _known = "The effect types available are: " +
                         string.Join(", ", names.ToArray()) +
                         ". The builder page's own dropdown is the same list " +
                         "with descriptions.";
            }
            catch (Exception)
            {
                // Advisory only.
            }

            return _known;
        }

        // onKill - pays out when the ship kills something.
        //
        // Nothing here needs a "scope" parameter: whether this counts every
        // kill or only one weapon's is decided by which GRID the module ends up
        // in, and the game tells the effect that for free. See the note on
        // KillRewardEffect.
        // CONTACT DAMAGE - your ship damages what it touches.
        private static ModuleEffect BuildContactDamage(
            JObject entry, string fileName)
        {
            float dmg = Flat(entry["damage"], 3f);
            bool bySpeed = FlatBool(entry["scaleBySpeed"] ?? entry["bySpeed"], false);
            float perSpeed = Flat(entry["damagePerSpeed"], 1f);
            float minSpeed = Flat(entry["minSpeed"], 6f);
            int detect = ModuleForgeContact.ParseDetect((string)entry["detect"]);
            bool boostOnly = FlatBool(
                entry["whileBoosting"] ?? entry["onlyWhileBoosting"], false);
            bool ignoreSelf = FlatBool(
                entry["ignoreSelfDamage"] ?? entry["safeRam"], false);

            var tint = ForgeAssets.ResolveColor((string)entry["tint"]
                ?? (string)entry["color"]);

            // ★ THE DAMAGE TYPE IS RESOLVED HERE, AT BUILD TIME, AND THAT IS
            // THE WHOLE POINT. Contact damage has no weapon to borrow an element
            // from - it is the hull, not a gun - and a null type makes the
            // game's Damage refuse every point of it in silence. That exact gap
            // cost two test rounds on the weapon side, reported as "they bump
            // into each other and doesnt seem to hurt one another".
            //
            // So it falls back to a real resource rather than to null, and says
            // which one it picked.
            Resource type = ForgeAssets.ResolveResource((string)entry["damageType"]);

            if (type == null)
            {
                type = ForgeAssets.ResolveResource("Health");

                string asked = (string)entry["damageType"];

                if (!string.IsNullOrEmpty(asked))
                {
                    Log.LogWarning(
                        fileName + ": contactDamage \"damageType\": \"" + asked +
                        "\" is not one of the game's resources. Fell back to " +
                        "Health, because a contact hit with NO damage type is " +
                        "refused in silence by the game - it has no weapon to " +
                        "borrow an element from the way a projectile does.");
                }
                else
                {
                    Log.LogInfo(
                        fileName + ": contactDamage has no \"damageType\", so " +
                        "it uses Health. Unlike a projectile there is no weapon " +
                        "here to inherit an element from, and a hit with no " +
                        "type deals nothing at all - so this defaults rather " +
                        "than failing quietly.");
                }
            }

            var effect = new ContactDamageEffect
            {
                damage = SeriesOr(entry["damage"], 3f),
                damageType = type,
                scaleBySpeed = bySpeed,
                damagePerSpeed = perSpeed,
                minSpeed = minSpeed,
                maxDamage = Flat(entry["maxDamage"], 999f),
                push = Flat(entry["push"], 0f),
                recoil = Flat(entry["recoil"], 0f),
                repeatDelay = Flat(entry["repeatDelay"], 0.3f),
                detect = detect,
                radius = Flat(entry["radius"], 0f),
                whileBoosting = boostOnly,
                ignoreSelfDamage = ignoreSelf,
                hasTint = tint != null,
                tintPulse = Flat(entry["tintPulse"], 0f),
                tintStrength = Mathf.Clamp01(
                    Flat(entry["tintStrength"] ?? entry["colorStrength"], 1f)),
                tintMode = TintMode(entry, fileName, "contactDamage")
            };

            if (tint != null)
                effect.tint = tint;

            if (!bySpeed && dmg <= 0f && effect.push == 0f && effect.recoil == 0f)
            {
                Log.LogWarning(
                    fileName + ": contactDamage does nothing - no \"damage\", " +
                    "no \"push\", no \"recoil\" and \"scaleBySpeed\" is off. " +
                    "Set at least one.");
            }

            // ★ THE DETECTION MODE IS THE INTERESTING WARNING, because choosing
            // the wrong one fails ONLY on the hardest hits - which is exactly
            // when a ram is supposed to matter, so it reads as the feature being
            // unreliable rather than as a setting.
            if (detect == ModuleForgeContact.DetectCollision)
            {
                Log.LogWarning(
                    fileName + ": contactDamage \"detect\": \"collision\" uses " +
                    "the stock Hazard's collision callbacks. EVERY unit in this " +
                    "game uses DISCRETE collision detection on a 0.02s step, so " +
                    "anything moving faster than about 50 units/s skips a " +
                    "1-unit object between physics steps - and a boosting ship " +
                    "is that fast. So this mode misses your hardest impacts " +
                    "specifically. The default swept cast is ChargerHead's own " +
                    "answer to the same problem; use \"collision\" only to " +
                    "compare them.");
            }

            if (bySpeed && perSpeed <= 0f)
            {
                Log.LogWarning(
                    fileName + ": contactDamage \"scaleBySpeed\" is on but " +
                    "\"damagePerSpeed\" is " + perSpeed.ToString("0.##") +
                    ", so a ram at any speed deals nothing. The flat " +
                    "\"damage\" value is IGNORED in this mode.");
            }

            // The ship's own crash damage is live exactly while boosting, which
            // is the mode a ram module encourages - so the combination is worth
            // naming rather than leaving to be discovered by dying.
            if (boostOnly && !ignoreSelf)
            {
                Log.LogInfo(
                    fileName + ": contactDamage is boost-gated, and the ship's " +
                    "OWN crash damage (ImpactDamage) is enabled by the game " +
                    "exactly while you boost - so ramming will hurt you too. " +
                    "That may be the trade you want; set " +
                    "\"ignoreSelfDamage\": true to suppress it while this " +
                    "module is installed. It is restored when you pull the " +
                    "card.");
            }

            Log.LogInfo(
                fileName + ": contactDamage - YOUR SHIP hurts what it touches, " +
                (bySpeed
                    ? ("scaled by speed: " + perSpeed.ToString("0.##") +
                       " per unit above " + minSpeed.ToString("0.##"))
                    : ("a flat " + dmg.ToString("0.##"))) +
                " per target every " +
                Mathf.Max(0.02f, Flat(entry["repeatDelay"], 0.3f))
                    .ToString("0.##") + "s, detected by " +
                ModuleForgeContact.DetectWord(detect) + ". " +
                (boostOnly
                    ? "Armed ONLY while boosting."
                    : "Armed whenever the module is powered and connected.") +
                " It never touches your own side. This works the same in EITHER " +
                "grid, unlike every other effect here - there is one hull " +
                "however many guns are bolted to it.");

            return effect;
        }

        // EXECUTE - finish anything a hit leaves under a threshold.
        //
        // Every scalar goes through Flat / FlatInt / FlatBool for the reason
        // recorded on BuildKillReward: a plain `(float?)` cast throws on the
        // `{baseValue, increaseMethod, change}` shape the builder page emits the
        // moment a "per level" box is filled, and a throw here abandons the
        // whole effect - the module then builds with ZERO effects and shows no
        // card line either, which reads as several separate bugs.
        private static ModuleEffect BuildExecute(
            JObject entry, string fileName)
        {
            float pc = Flat(entry["percent"], 0.15f);
            float fl = Flat(entry["flat"], 0f);
            float cap = Flat(entry["maxTargetHealth"], 0f);
            bool correctedPercent = false;

            // A PERCENT ABOVE 1 IS CORRECTED RATHER THAN SHIPPED, and this is
            // the one correction in the block that genuinely has to happen.
            // "percent": 20 means "under 2000% health", which EVERY living
            // thing satisfies - so the module would delete everything it
            // touched, instantly, and read as the mod being broken rather than
            // as a typo. Nobody types 20 meaning 2000%.
            if (pc > 1f)
            {
                Log.LogWarning(
                    fileName + ": execute \"percent\": " + pc.ToString("0.##") +
                    " is above 1, which would mean \"below " +
                    (pc * 100f).ToString("0") + "% health\" - every living " +
                    "thing in the game. Read as a PERCENT and converted to " +
                    (pc / 100f).ToString("0.###") + ". Write it as a fraction " +
                    "(0.15 = 15%) to say so on purpose.");

                pc = pc / 100f;
                correctedPercent = true;
            }

            // STACKING (R20 252, his design): added to the threshold for every
            // copy of THIS module past the first. Same fraction rule as
            // `percent`, and for the same reason - "stackPercent": 5 means 5%.
            float sp = Mathf.Max(0f, Flat(entry["stackPercent"], 0f));
            float sf = Mathf.Max(0f, Flat(entry["stackFlat"], 0f));

            if (sp > 1f)
            {
                Log.LogWarning(
                    fileName + ": execute \"stackPercent\": " + sp.ToString("0.##") +
                    " is above 1 - read as a PERCENT and converted to " +
                    (sp / 100f).ToString("0.###") + ". Write it as a fraction " +
                    "(0.05 = +5% per extra copy) to say so on purpose.");

                sp = sp / 100f;
            }

            var colour = ForgeAssets.ResolveColor((string)entry["color"]);

            var effect = new ExecuteEffect
            {
                percent = SeriesOr(entry["percent"], 0.15f),
                flat = SeriesOr(entry["flat"], 0f),
                maxTargetHealth = cap,
                stackPercent = sp,
                stackFlat = sf,
                hasColor = colour != null,
                colorSeconds = Flat(entry["colorSeconds"], 0.25f),
                colorPulse = Flat(entry["colorPulse"], 0f),
                tintStrength = Mathf.Clamp01(
                    Flat(entry["tintStrength"] ?? entry["colorStrength"], 1f)),
                tintMode = TintMode(entry, fileName, "execute")
            };

            // THE CORRECTION HAS TO REACH THE SERIES, not just the local used
            // for the log line. `SeriesOr` read the raw token, so without this
            // the warning would announce a division that the module never
            // performed - a log that describes work it did not do is worse than
            // no log, because it is believed.
            //
            // The per-level `change` is divided too: a "per level" box filled
            // in the same units as a percent-shaped base is meant in those same
            // units, and leaving it raw would make level 2 jump by whole
            // multiples of the tank.
            if (correctedPercent)
            {
                FloatSeries s = effect.percent;
                s.baseValue = pc;
                s.change = s.change / 100f;
                effect.percent = s;
            }

            if (colour != null)
                effect.color = colour;

            string sfx = (string)entry["sfx"];

            if (!string.IsNullOrEmpty(sfx))
            {
                string guid = ForgeInterop.TryResolveSound(sfx, fileName);

                if (string.IsNullOrEmpty(guid))
                    guid = ForgeAssets.StockSound(sfx, fileName);

                effect.sfxGuid = guid;
            }

            if (pc <= 0f && fl <= 0f)
            {
                Log.LogWarning(
                    fileName + ": execute has neither a \"percent\" nor a " +
                    "\"flat\" threshold, so nothing can ever be finished. Set " +
                    "one - \"percent\": 0.15 finishes anything under 15% of " +
                    "its own health.");
            }

            // THE BOSS GUARD MATTERS MORE THAN IT LOOKS, and its absence is
            // silent otherwise: a 15% execute on a 2000-health boss is 300 free
            // damage in one hit, which is a different module than the finisher
            // the author thinks they wrote.
            if (cap <= 0f && pc > 0f)
            {
                Log.LogWarning(
                    fileName + ": execute has no \"maxTargetHealth\", so it " +
                    "applies to BOSSES too. At " + (pc * 100f).ToString("0.#") +
                    "% that is a large chunk of a big health pool taken in one " +
                    "hit. Set \"maxTargetHealth\" to the biggest pool you want " +
                    "this to touch, or leave it off deliberately.");
            }

            Log.LogInfo(
                fileName + ": execute - finishes anything left below " +
                (pc > 0f ? ((pc * 100f).ToString("0.#") + "% health") : "") +
                (pc > 0f && fl > 0f ? " or " : "") +
                (fl > 0f ? (fl.ToString("0.##") + " health") : "") +
                (cap > 0f
                    ? (", skipping anything with more than " +
                       cap.ToString("0.##") + " max health")
                    : ", with NO size limit") +
                ". The kill credits itself through the game's own Die(), so " +
                "on-kill rewards, leech and anything else listening all see " +
                "it. Burn ticks and gas clouds can never execute - they report " +
                "no weapon.");

            if (sp > 0f || sf > 0f)
            {
                var ladder = new List<string>();

                for (int n = 2; n <= 4; n++)
                {
                    string at = "";

                    if (pc > 0f || sp > 0f)
                        at = (Mathf.Min(1f, pc + sp * (n - 1)) * 100f).ToString("0.#") + "%";

                    if (fl > 0f || sf > 0f)
                        at += (at.Length > 0 ? " / " : "") +
                              (fl + sf * (n - 1)).ToString("0.##") + " health";

                    ladder.Add(n + " copies " + at);
                }

                Log.LogInfo(
                    fileName + ": execute stacks - each copy of this module past " +
                    "the first adds " +
                    (sp > 0f ? "+" + (sp * 100f).ToString("0.#") + "%" : "") +
                    (sp > 0f && sf > 0f ? " and " : "") +
                    (sf > 0f ? "+" + sf.ToString("0.##") + " health" : "") +
                    " (" + string.Join(", ", ladder.ToArray()) + "). Copies of " +
                    "the SAME file only: a different execute module still " +
                    "counts on its own, strongest wins. The card shows the " +
                    "stack you are carrying.");

                if (pc > 0f && sp > 0f && pc + sp * 3 >= 1f)
                {
                    Log.LogWarning(
                        fileName + ": with 4 copies this execute reaches 100% - " +
                        "every hit then finishes anything under the size cap. " +
                        "That is the file's own numbers; lower \"stackPercent\" " +
                        "if it is not what you meant.");
                }
            }
            else
            {
                Log.LogInfo(
                    fileName + ": execute - copies of this module do NOT stack " +
                    "(no \"stackPercent\" / \"stackFlat\"): with two on the " +
                    "grid, the strongest one counts. Add \"stackPercent\" to " +
                    "make each extra copy widen it.");
            }

            return effect;
        }

        // GROW - a shot that changes size as it travels.
        private static ModuleEffect BuildGrow(JObject entry, string fileName)
        {
            float f = Flat(entry["from"], 0.4f);
            float t = Flat(entry["to"], 3f);
            float span = Flat(entry["span"], 0f);
            bool overTime = FlatBool(entry["overTime"] ?? entry["time"], false);
            bool hitbox = FlatBool(entry["hitbox"], true);

            var effect = new GrowEffect
            {
                from = SeriesOr(entry["from"], 0.4f),
                to = SeriesOr(entry["to"], 3f),
                overTime = overTime,
                span = span,
                hitbox = hitbox,
                curve = Flat(entry["curve"], 1f),
                clamp = FlatBool(entry["clamp"], true)
            };

            if (f == t)
            {
                Log.LogWarning(
                    fileName + ": grow has \"from\" and \"to\" both at " +
                    f.ToString("0.##") + ", so the shot never changes size. " +
                    "Give them different values - 0.4 to 3 starts small and " +
                    "finishes large, 3 to 0.4 does the reverse.");
            }

            // A KEY THAT IS READ ON ONE SHOT TYPE AND NOT THE OTHER HAS TO SAY
            // SO. A module applies to whatever gun the player slots it beside,
            // and on a lobbed weapon there is no `Radius` to leave alone - the
            // collider scales with the transform, so the hitbox always follows.
            // Silently ignoring the switch is how a setting becomes folklore.
            if (!hitbox)
            {
                Log.LogWarning(
                    fileName + ": grow \"hitbox\": false makes the growth " +
                    "purely visual on ordinary bullets - but it CANNOT be " +
                    "honoured on a lobbed or rocket weapon. Those collide with " +
                    "a real Collider2D that scales with the sprite, so there " +
                    "is no separate radius to hold still. On one of those the " +
                    "hitbox grows regardless.");
            }

            if (span <= 0f)
            {
                Log.LogInfo(
                    fileName + ": grow \"span\" is 0, so each shot borrows its " +
                    "own " + (overTime ? "lifetime" : "range") + " and peaks " +
                    "exactly as it runs out. That is usually what you want; " +
                    "set a number to fix the ramp regardless of the gun. NOTE " +
                    "a lobbed shot's range is inert in this game - the engine " +
                    "never enforces it - so a distance ramp on one falls back " +
                    "to 10 units.");
            }

            Log.LogInfo(
                fileName + ": grow - shots " +
                (t >= f ? "swell" : "shrink") + " from x" + f.ToString("0.##") +
                " to x" + t.ToString("0.##") + " over " +
                (span > 0f
                    ? (span.ToString("0.##") + (overTime ? "s" : " units"))
                    : ("their own " + (overTime ? "lifetime" : "range"))) +
                (hitbox
                    ? ". The HITBOX grows with the art - Radius is what the " +
                      "game rebuilds its collision sweep from, so this is a " +
                      "real damage-area change and not a picture."
                    : ". Visual only on bullets.") +
                " There is no damage multiplier here on purpose: this mod has " +
                "no damage stack and pierce falloff already writes that field " +
                "in flight, so a second writer would silently erase it.");

            return effect;
        }

        // SLOW - a debuff on what you hit, and a bend in time when you land it.
        //
        // Every scalar goes through Flat / FlatInt / FlatBool for the reason
        // recorded on BuildKillReward: a plain `(float?)` cast throws on the
        // `{baseValue, increaseMethod, change}` shape the builder page emits the
        // moment a "per level" box is filled, and a throw here abandons the
        // whole effect - the module then builds with ZERO effects and shows no
        // card line either, which reads as several separate bugs.
        private static ModuleEffect BuildSlow(JObject entry, string fileName)
        {
            bool debuff = FlatBool(
                entry["debuff"] ?? entry["slowEnabled"], true);

            float amount = Flat(entry["amount"], 0.5f);
            float duration = Flat(entry["duration"], 2f);
            float floor = Flat(entry["floor"], 0.2f);
            int mode = ModuleForgeSlow.ParseMode((string)entry["mode"]);
            int maxStacks = FlatInt(entry["maxStacks"], 3);
            bool correctedAmount = false;

            // NAMING `timeScale` TURNS THE TIME HALF ON. The two halves share no
            // mechanism, so a file that only wants hit-stop should not have to
            // set a flag as well as a number - and a file that wants the debuff
            // alone must not get a time bend it never asked for.
            bool hasTime = FlatBool(
                entry["timeSlow"] ?? entry["hasTimeSlow"],
                entry["timeScale"] != null);

            float timeScale = Flat(entry["timeScale"], 0.35f);
            float timeDuration = Flat(entry["timeDuration"], 0.25f);
            float timeCooldown = Flat(entry["timeCooldown"], 0f);

            bool onHit = FlatBool(entry["onHit"], true);
            bool onKill = FlatBool(entry["onKill"], false);

            // AN AMOUNT ABOVE 1 IS CORRECTED RATHER THAN SHIPPED, the same
            // correction `execute`'s percent gets and for the same reason:
            // "amount": 50 means "50 times normal speed", which is a SPEED-UP
            // module that reads as the slow being broken. Nobody types 50
            // meaning 5000%.
            if (amount > 1f)
            {
                Log.LogWarning(
                    fileName + ": slow \"amount\": " + amount.ToString("0.##") +
                    " is above 1, and this is a FRACTION OF NORMAL SPEED - so " +
                    "that would mean " + (amount * 100f).ToString("0") +
                    "% speed, i.e. faster. Read as a percentage and converted " +
                    "to " + (amount / 100f).ToString("0.###") + ". Write it as " +
                    "a fraction (0.5 = half speed) to say so on purpose.");

                amount = amount / 100f;
                correctedAmount = true;
            }

            var tint = ForgeAssets.ResolveColor((string)entry["tint"]
                ?? (string)entry["color"]);

            var effect = new SlowEffect
            {
                hasSlow = debuff,
                amount = SeriesOr(entry["amount"], 0.5f),
                duration = SeriesOr(entry["duration"], 2f),
                mode = mode,
                cooldown = Flat(entry["cooldown"], 0f),
                maxStacks = maxStacks,
                floor = floor,

                slowMove = FlatBool(entry["slowMove"] ?? entry["move"], true),
                slowTurn = FlatBool(entry["slowTurn"] ?? entry["turn"], true),
                slowFire = FlatBool(entry["slowFire"] ?? entry["fireRate"], true),
                slowKnockback = FlatBool(
                    entry["slowKnockback"] ?? entry["knockback"], true),
                slowShots = FlatBool(entry["slowShots"] ?? entry["shots"], true),

                hasTint = tint != null,
                tintStrength = Mathf.Clamp01(
                    Flat(entry["tintStrength"] ?? entry["colorStrength"], 1f)),
                tintMode = TintMode(entry, fileName, "slow"),

                hasTimeSlow = hasTime,
                timeScale = SeriesOr(entry["timeScale"], 0.35f),
                timeDuration = timeDuration,
                timeCooldown = timeCooldown,
                timeMode = ModuleForgeSlow.ParseMode(
                    (string)(entry["timeMode"] ?? entry["mode"])),
                timeEase = FlatBool(entry["timeEase"] ?? entry["ease"], true),
                timeUnscaled = FlatBool(entry["timeUnscaled"], true),

                onHit = onHit,
                onKill = onKill
            };

            // THE CORRECTION HAS TO REACH THE SERIES, not just the local used
            // for the log line. `SeriesOr` read the raw token, so without this
            // the warning would announce a division the module never performed -
            // a log that describes work it did not do is worse than no log,
            // because it is believed.
            if (correctedAmount)
            {
                FloatSeries s = effect.amount;
                s.baseValue = amount;
                s.change = s.change / 100f;
                effect.amount = s;
            }

            if (tint != null)
                effect.tint = tint;

            // ---- warnings -----------------------------------------------

            if (!debuff && !hasTime)
            {
                Log.LogWarning(
                    fileName + ": slow has its debuff switched off and no " +
                    "\"timeSlow\", so this effect does nothing at all. Set " +
                    "\"amount\" for the debuff or \"timeScale\" for hit-stop.");
            }

            if (!onHit && !onKill)
            {
                Log.LogWarning(
                    fileName + ": slow has both \"onHit\" and \"onKill\" off, " +
                    "so it can never fire. One of them has to be true.");
            }

            if (debuff && !effect.slowMove && !effect.slowTurn &&
                !effect.slowFire && !effect.slowKnockback && !effect.slowShots)
            {
                Log.LogWarning(
                    fileName + ": slow has every part switched off (move, " +
                    "turn, fire rate, knockback, shots), so the debuff lands " +
                    "and changes nothing. Leave at least one on.");
            }

            // ★ THE FLOOR IS THE STRONGEST A STACK CAN GET, SO A FLOOR ABOVE
            // THE AMOUNT SILENTLY WEAKENS THE MODULE. `Factor()` is
            // `max(amount, floor)`, so "amount": 0.3 with the default floor of
            // 0.2 is fine, while "amount": 0.1 with floor 0.2 is really a 20%
            // slow wearing a 10% label - and the card would print the number
            // the module does not use.
            if (debuff && floor > amount)
            {
                Log.LogWarning(
                    fileName + ": slow \"floor\": " + floor.ToString("0.##") +
                    " is WEAKER than \"amount\": " + amount.ToString("0.##") +
                    " - and the floor is the strongest this can ever get, so " +
                    "the real slow is " + floor.ToString("0.##") + " and the " +
                    "amount is ignored. Lower is stronger here. Set the floor " +
                    "below the amount, or leave it out.");
            }

            if (debuff && mode == ModuleForgeSlow.Stack && maxStacks <= 1)
            {
                Log.LogWarning(
                    fileName + ": slow \"mode\": \"stack\" with " +
                    "\"maxStacks\": " + maxStacks + " behaves exactly like " +
                    "\"refresh\" - there is no second stack to reach. Raise " +
                    "maxStacks or drop the mode.");
            }

            if (debuff && duration <= 0f)
            {
                Log.LogWarning(
                    fileName + ": slow \"duration\" is " +
                    duration.ToString("0.##") + ", so the debuff expires on " +
                    "the frame it lands. It is clamped up to 0.05s. Unlike " +
                    "\"timeDuration\" this one does NOT mean forever.");
            }

            // ★ 0 MEANS FOREVER ON THE TIME HALF AND NOWHERE ELSE, which is
            // exactly the sort of asymmetry that gets shipped. The game's
            // `TimeScaleModifier.IsIndefinit` is `duration <= 0f`, and an
            // indefinite modifier is never evaluated for expiry - it sits there
            // holding the whole game in slow motion with nothing to clear it.
            if (hasTime && timeDuration <= 0f)
            {
                Log.LogWarning(
                    fileName + ": slow \"timeDuration\" is " +
                    timeDuration.ToString("0.##") + ". In this game a time " +
                    "modifier with a duration of 0 or less is INDEFINITE - it " +
                    "would hold the game in slow motion for the rest of the " +
                    "run. Clamped to 0.01s. Set a real duration.");
            }

            // ★ THE MODULE PROBLEM THE WEAPON VERSION NEVER HAD. A weapon's
            // timeSlow fires when THAT weapon connects; a module on the SHIP
            // grid fires when ANY of your weapons connects, and a beam or a
            // shotgun connects many times a second. The author cannot be told
            // which grid the player will use it in, so this is said whenever
            // there is no cooldown at all.
            if (hasTime && timeCooldown <= 0f)
            {
                Log.LogWarning(
                    fileName + ": slow has \"timeSlow\" with no " +
                    "\"timeCooldown\". A MODULE applies to every gun it is " +
                    "connected to, so on the SHIP grid this bends time on " +
                    "every hit from every weapon you carry - a beam or a " +
                    "shotgun will re-trigger it several times a second and the " +
                    "game will feel like it is stuttering rather than " +
                    "punching. Set \"timeCooldown\" to about 1, or " +
                    "\"onKill\": true with \"onHit\": false.");
            }

            // ---- the summary --------------------------------------------

            Log.LogInfo(
                fileName + ": slow - " +
                (debuff
                    ? ("your hits drop what they touch to " +
                       (amount * 100f).ToString("0.#") + "% speed for " +
                       duration.ToString("0.##") + "s (" +
                       ModuleForgeSlow.ModeWord(mode) + " on a repeat hit), " +
                       "affecting " + Parts(effect) + ". ")
                    : "the debuff is off. ") +
                (hasTime
                    ? ("Time bends to x" + timeScale.ToString("0.##") + " for " +
                       timeDuration.ToString("0.##") + " real seconds" +
                       (timeCooldown > 0f
                            ? (", at most once every " +
                               timeCooldown.ToString("0.##") + "s. ")
                            : ". "))
                    : "") +
                "It never touches you or your own side. Which grid you place " +
                "this in decides whether it is one gun or all of them. NOTE " +
                "the debuff rides the victim's RIGIDBODY, not its movement " +
                "component - the AI snapshots and restores those, which would " +
                "make a temporary slow permanent at random.");

            // WHICH MOD IS HOLDING THE RIGIDBODY IS WORTH SAYING AT BUILD TIME
            // TOO. ModuleForgeSlowCompat says it on the first slow of a run,
            // which is the right place for "this actually happened" - but the
            // person reading a build log is the person editing the file, and
            // they should not have to fire a shot to learn that the debuff's
            // settings are carried across into another mod's engine.
            if (debuff && !ModuleForgeSlowCompat.OwnsDebuff)
            {
                Log.LogInfo(
                    fileName + ": Weapon Forge is installed, so IT owns a " +
                    "unit's rigidbody while slowed and this module's debuff is " +
                    "applied through its engine. Every setting above crosses " +
                    "over; only \"onHit\"/\"onKill\" stay here. Two mods each " +
                    "remembering a unit's original speed is what would make " +
                    "the slow permanent, which is why there is one owner.");
            }

            return effect;
        }

        private static string Parts(SlowEffect e)
        {
            var bits = new List<string>();

            if (e.slowMove) bits.Add("movement");
            if (e.slowTurn) bits.Add("turning");
            if (e.slowFire) bits.Add("fire rate");
            if (e.slowShots) bits.Add("the speed of their shots");
            if (e.slowKnockback) bits.Add("weight (your shoves throw them further)");

            return (bits.Count == 0)
                ? "nothing" : string.Join(", ", bits.ToArray());
        }

        // Resolve one sound name to a guid: custom first (Weapon Forge's
        // library, borrowed), then the game's own by readable name. The same
        // two-step five other call sites in this file spell out by hand -
        // written as a helper here because `windup` needs it three times, and
        // three copies of a two-step lookup is how the third one goes stale.
        private static string Sfx(JToken token, string fileName)
        {
            string asked = (token != null && token.Type == JTokenType.String)
                ? (string)token : null;

            if (string.IsNullOrEmpty(asked))
                return "";

            string guid = ForgeInterop.TryResolveSound(asked, fileName);

            if (string.IsNullOrEmpty(guid))
                guid = ForgeAssets.StockSound(asked, fileName);

            return guid ?? "";
        }

        // WINDUP - sustained fire rewards you.
        //
        // Every scalar goes through Flat / FlatInt / FlatBool for the reason
        // recorded on BuildKillReward: a plain `(float?)` cast throws on the
        // `{baseValue, increaseMethod, change}` shape the builder page emits the
        // moment a "per level" box is filled, and a throw here abandons the
        // whole effect.
        private static ModuleEffect BuildWindup(JObject entry, string fileName)
        {
            float up = Flat(entry["windUpTime"] ?? entry["spinUpTime"], 1.5f);
            float down = Flat(entry["windDownTime"] ?? entry["spinDownTime"], 1f);
            int ramp = ModuleForgeWindup.ParseRamp((string)entry["ramp"]);
            int steps = FlatInt(entry["steps"], 3);

            var effect = new WindupEffect
            {
                windUpTime = SeriesOr(
                    entry["windUpTime"] ?? entry["spinUpTime"], 1.5f),
                windDownTime = down,
                grace = Flat(entry["grace"], 0.25f),
                ramp = ramp,
                steps = steps,

                // The two that default ON, matching the weapon side.
                rate = FlatBool(entry["rate"] ?? entry["fireRate"], true),
                fireRateAtFull = Flat(
                    entry["fireRateAtFull"] ?? entry["fireRate"], 2f),

                accuracy = FlatBool(entry["accuracy"], true),
                accuracyAtFull = Flat(entry["accuracyAtFull"], 1f),
                spreadAtFull = Flat(
                    entry["spreadAtFull"] ?? entry["spread"], 1f),

                damage = FlatBool(entry["damage"], false),
                damageAtFull = Flat(entry["damageAtFull"], 1.5f),

                pellets = FlatBool(entry["pellets"], false),
                pelletsAtFull = Flat(entry["pelletsAtFull"], 2f),

                cheaper = FlatBool(entry["cheaper"], false),
                costAtFull = Flat(entry["costAtFull"], 0.5f),

                speed = FlatBool(entry["speed"], false),
                speedAtFull = Flat(entry["speedAtFull"], 1.5f),

                range = FlatBool(entry["range"], false),
                rangeAtFull = Flat(entry["rangeAtFull"], 1.5f),

                size = FlatBool(entry["size"], false),
                sizeAtFull = Flat(entry["sizeAtFull"], 1.5f),

                burst = FlatBool(entry["burst"], false),
                burstAtFull = Flat(entry["burstAtFull"], 2f),
                burstDelayAtFull = Flat(entry["burstDelayAtFull"], 1f),

                burn = FlatBool(entry["burn"], false),
                burnAtFull = Flat(entry["burnAtFull"], 2f),

                explosion = FlatBool(entry["explosion"], false),
                explosionAtFull = Flat(entry["explosionAtFull"], 1.5f),

                push = FlatBool(entry["push"], false),
                pushAtFull = Flat(entry["pushAtFull"], 2f),
                knockbackAtFull = Flat(entry["knockbackAtFull"], 1f),

                warmup = FlatBool(entry["warmup"], false),
                warmupAtFull = Flat(entry["warmupAtFull"], 0f),

                lifetime = FlatBool(entry["lifetime"], false),
                lifetimeAtFull = Flat(entry["lifetimeAtFull"], 1.5f),

                showOnHud = FlatBool(entry["showOnHud"] ?? entry["show"], true),
                readySfxGuid = Sfx(entry["readySfx"], fileName),
                stepSfxGuid = Sfx(entry["stepSfx"], fileName),
                lostSfxGuid = Sfx(entry["lostSfx"], fileName)
            };

            // ---- warnings -----------------------------------------------

            // A ramp that pays nothing is a valid file and a dead module.
            if (!effect.rate && !effect.accuracy && !effect.damage &&
                !effect.pellets && !effect.cheaper && !effect.speed &&
                !effect.range && !effect.size && !effect.burst &&
                !effect.burn && !effect.explosion && !effect.push &&
                !effect.warmup && !effect.lifetime)
            {
                Log.LogWarning(
                    fileName + ": windup has every reward switched off, so the " +
                    "meter fills and nothing happens. Turn at least one on - " +
                    "\"rate\" and \"accuracy\" are the two that default on.");
            }

            // ★ THE ACCURACY REWARD HAS NOTHING TO REMOVE ON MOST GUNS, and
            // that is a fact about the GAME rather than about this module.
            // Most stock weapons ship angleVariance 0 - White Popper, White
            // Shotgun, Bolt, Worm - so a shotgun's inaccuracy is entirely its
            // deliberate `spread`, which this leaves alone by default. A module
            // cannot know which gun it will be slotted beside, so it is said
            // here rather than being discovered as "the accuracy reward does
            // nothing".
            if (effect.accuracy && Mathf.Abs(effect.spreadAtFull - 1f) < 0.001f)
            {
                Log.LogInfo(
                    fileName + ": windup's accuracy reward tightens " +
                    "\"angleVariance\", and MOST STOCK WEAPONS SHIP THAT AT " +
                    "ZERO (Popper, Shotgun, Bolt, Worm) - so on those it has " +
                    "nothing to remove and you will see no change. A " +
                    "shotgun's fan is \"spread\", which is left alone unless " +
                    "you set \"spreadAtFull\" (below 1 tightens it; that turns " +
                    "a shotgun into a slug gun, which is the point or a " +
                    "surprise).");
            }

            if (up <= 0f)
            {
                Log.LogWarning(
                    fileName + ": windup \"windUpTime\" is " +
                    up.ToString("0.##") + ", so the gun is fully wound the " +
                    "instant you pull the trigger. That is legal and is a " +
                    "different module - there is no ramp to feel. Set a " +
                    "positive number for a wind-up.");
            }

            // ★ A WARNING THAT READS THE BASE IS NOT A WARNING ABOUT THE
            // MODULE, and the check above reads the base.
            //
            // `windUpTime` is a series and its per-level change is NEGATIVE by
            // design - a shorter wind-up is the natural thing for a level to
            // buy. So a perfectly ordinary card (base 2, change -1) is 2s at
            // level 1 and **0s at level 3**, which is the instant-full case the
            // warning above exists for, reached by a route that warning cannot
            // see. A BoosterCore is enough to get there.
            //
            // Same family as the cruise-speed line that stated a derived number
            // without the conditions it was derived under: say WHICH LEVEL, and
            // let the author decide whether that is the module they meant.
            float perLevel = 0f;

            {
                FloatSeries s = effect.windUpTime;

                if (s.increaseMethod == FloatSeries.IncreaseMethod.Add)
                    perLevel = s.change;
            }

            if (up > 0f && perLevel < 0f)
            {
                // baseValue + change * (level - 1) <= 0
                int zeroAt = Mathf.CeilToInt(up / -perLevel) + 1;

                string tail =
                    (zeroAt <= 6)
                        ? (" - and a BoosterCore or two can reach that, so " +
                           "check it is the module you meant.")
                        : ", which is out of reach in practice.";

                Log.LogInfo(
                    fileName + ": windup's wind-up time falls by " +
                    (-perLevel).ToString("0.##") + "s per level, so it is " +
                    up.ToString("0.##") + "s at level 1 and reaches ZERO at " +
                    "level " + zeroAt + " - fully wound from the first shot, " +
                    "with no ramp left to feel" + tail +
                    " (It is floored at 0; it never goes negative.)");
            }

            if (ramp == ModuleForgeWindup.Stepped && steps <= 1)
            {
                Log.LogWarning(
                    fileName + ": windup \"ramp\": \"stepped\" with " +
                    "\"steps\": " + steps + " is the same as \"smooth\" - " +
                    "there is no second step to reach.");
            }

            // ★ THE ONE THAT CANNOT BE ANSWERED AT BUILD TIME, said anyway.
            Log.LogInfo(
                fileName + ": windup - hold the trigger for " +
                up.ToString("0.##") + "s and the gun winds up " +
                ModuleForgeWindup.RampWord(ramp, steps) + ", winding back down " +
                "over " + down.ToString("0.##") + "s once you stop. " +
                "An empty gun does not wind up and a jam or a stun costs you " +
                "the wind-up. GADGET WEAPONS CANNOT BE REACHED - they fire " +
                "directly and never touch the trigger machinery this reads, " +
                "so a windup module beside one does nothing. The weapon-side " +
                "version refuses a gadget outright; a module cannot, because " +
                "you choose the slot - so the card says so and the log warns " +
                "the first time such a weapon fires.");

            return effect;
        }

        // The `gas` block on a kill-reward module: the cloud left on a corpse.
        //
        // EVERY SCALAR GOES THROUGH Flat / FlatInt / FlatBool, and that is not
        // stylistic. A plain `(float?)` cast throws `Can not convert Object to
        // Single` on the `{baseValue, increaseMethod, change}` shape the builder
        // page emits the moment a "per level" box is filled - and a throw here
        // abandons the whole effect, so the module builds with ZERO effects: no
        // payout, no pickup, no buff and no card line either. That read as four
        // separate bugs and cost five failed tests. These fields are not
        // series-capable, but the page can still emit that shape into any of
        // them, and "this cast can never see an object" is exactly the
        // reasoning that was wrong last time.
        private static ModuleForgeGas.Config BuildGas(
            JObject gas, string fileName)
        {
            if (gas == null)
                return null;

            var cfg = new ModuleForgeGas.Config
            {
                radius = Flat(gas["radius"], 1.6f),
                grows = FlatBool(gas["grow"] ?? gas["grows"], true),
                growTo = Flat(gas["growTo"], 3.2f),
                growTime = Flat(gas["growTime"], 1.2f),
                duration = Flat(gas["duration"], 4f),
                fade = Flat(gas["fade"], 0.6f),

                drifts = FlatBool(gas["drift"] ?? gas["drifts"], false),
                driftDamping = Flat(gas["driftDamping"], 0.4f),

                max = FlatInt(gas["max"], 24),

                burnPerSecond = Flat(gas["burnPerSecond"] ?? gas["burn"], 0f),
                damagePerSecond =
                    Flat(gas["damagePerSecond"] ?? gas["damage"], 0f),
                slow = Flat(gas["slow"], 0f),
                push = Flat(gas["push"], 0f),
                extinguish = Flat(gas["extinguish"], 0f),
                extinguishTerrain = Flat(gas["extinguishTerrain"], 0f),
                igniteTerrain = Flat(gas["igniteTerrain"], 0f),

                hitEnemies = FlatBool(gas["hitEnemies"], true),
                hitPlayer = FlatBool(gas["hitPlayer"], false),
                tickRate = Flat(gas["tickRate"], 0.25f),

                chains = FlatBool(gas["chains"] ?? gas["gasChains"], true),

                puffs = FlatInt(gas["puffs"], 5),
                swirl = Flat(gas["swirl"], 25f),

                sprite = (string)gas["sprite"] ?? "",
                sfx = (string)gas["sfx"] ?? "",
                damageType = (string)gas["damageType"] ?? ""
            };

            // `driftSpeed` is the honest name and `drift` is the switch, but
            // the weapon side spells the SPEED `drift` and the mode `cloud`, so
            // both readings arrive. Taken as a number only when it is one.
            cfg.drift = Flat(gas["driftSpeed"], Flat(gas["driftRate"], 1.2f));

            var colour = ForgeAssets.ResolveColor((string)gas["color"]);

            if (colour != null)
                cfg.color = colour;

            // Named element, or null meaning "inherit from whichever gun scored
            // the kill" - resolved per cloud, never cached onto this config.
            if (!string.IsNullOrEmpty(cfg.damageType))
            {
                cfg.damageResource = ForgeAssets.ResolveResource(cfg.damageType);

                if (cfg.damageResource == null)
                {
                    Log.LogWarning(
                        fileName + ": gas \"damageType\": \"" + cfg.damageType +
                        "\" is not one of the game's resources, so the cloud " +
                        "falls back to the element of whichever weapon scored " +
                        "the kill. That is the default and is usually what you " +
                        "want - remove the key to say so on purpose.");
                }
            }

            // STOCK ART ONLY, and the warning says which half it searched
            // rather than only naming the string - a message that names the
            // INPUT but not the CAPABILITY sends the reader to check their
            // spelling when the answer is that the lookup cannot see their
            // folder at all.
            if (!string.IsNullOrEmpty(cfg.sprite))
            {
                cfg.artFrames = ForgeAssets.ResolveSpriteFrames(cfg.sprite);

                if (cfg.artFrames == null || cfg.artFrames.Length == 0)
                {
                    Log.LogWarning(
                        fileName + ": gas \"sprite\": \"" + cfg.sprite +
                        "\" did not resolve. This key searches the GAME's own " +
                        "sprites only - custom PNGs are Weapon Forge's loader " +
                        "and it rebuilds what it returns at the module-icon " +
                        "size (24x24 at PPU 40), which is wrong for a gas " +
                        "puff. The cloud falls back to its own soft blob, " +
                        "which is what most clouds should use anyway.");
                }
                else
                {
                    // OPT-IN, the same rule the five one-sprite keys follow on
                    // the weapon side: a bare name stays a still puff so
                    // nothing already written starts moving on the next launch.
                    // `:0-8` or `@12` is how you ask.
                    cfg.animates =
                        cfg.sprite.IndexOf(':') >= 0 ||
                        cfg.sprite.IndexOf('@') >= 0;

                    cfg.artFps = Flat(gas["spriteFps"], 12f);
                }
            }

            if (!string.IsNullOrEmpty(cfg.sfx))
            {
                // Custom first, then stock - the same order every sprite key
                // uses. Borrowed from Weapon Forge when present; looking a name
                // up in an asset the GAME owns is native here.
                string guid = ForgeInterop.TryResolveSound(cfg.sfx, fileName);

                if (string.IsNullOrEmpty(guid))
                    guid = ForgeAssets.StockSound(cfg.sfx, fileName);

                cfg.sfxGuid = guid;
            }

            WarnAboutGas(cfg, fileName);

            return cfg;
        }

        // Said at BUILD time because every one of these is knowable from the
        // file, and a cloud that does nothing is invisible in play - there is
        // no failure to see, only an absence.
        private static void WarnAboutGas(
            ModuleForgeGas.Config cfg, string fileName)
        {
            if (!cfg.DoesAnything)
            {
                Log.LogWarning(
                    fileName + ": onKill has a \"gas\" block that carries no " +
                    "payload, so no cloud is left at all. Set at least one of " +
                    "\"damagePerSecond\", \"burnPerSecond\", \"slow\", " +
                    "\"push\", \"extinguish\", \"extinguishTerrain\" or " +
                    "\"igniteTerrain\". Colour and puffs describe a cloud; " +
                    "they do not make one.");

                return;
            }

            if (cfg.duration <= 0f)
            {
                Log.LogWarning(
                    fileName + ": gas \"duration\" is " +
                    cfg.duration.ToString("0.##") + ", so the cloud dies on " +
                    "the frame it is born. Give it seconds to live.");
            }

            if (cfg.grows && cfg.growTo < cfg.radius)
            {
                Log.LogWarning(
                    fileName + ": gas \"growTo\" (" +
                    cfg.growTo.ToString("0.##") + ") is SMALLER than " +
                    "\"radius\" (" + cfg.radius.ToString("0.##") +
                    "), so the cloud shrinks instead of swelling. That is a " +
                    "real effect and may be what you want - set " +
                    "\"grow\": false if it is not.");
            }

            // Cell fire thresholds are NOT normalised and this is the number
            // people get wrong: CellType_Solid ignites at 45 and is topped up
            // by every burning neighbour every frame, so a small extinguish
            // rate can never win against it and the feature looks broken on the
            // only terrain most rooms are made of.
            if (cfg.extinguishTerrain > 0f && cfg.extinguishTerrain < 45f)
            {
                Log.LogWarning(
                    fileName + ": gas \"extinguishTerrain\": " +
                    cfg.extinguishTerrain.ToString("0.#") + " will put soft " +
                    "terrain out (Slime 1, Tuff 1.3, Moss 2.5) and will NEVER " +
                    "beat CellType_Solid, the main terrain, which ignites at " +
                    "45 and is re-lit by every burning neighbour each frame. " +
                    "Use about 120 if you meant to douse rock.");
            }

            if (cfg.max <= 0)
            {
                Log.LogWarning(
                    fileName + ": gas \"max\": " + cfg.max + " means UNLIMITED " +
                    "clouds from this module. With \"chains\" on, a packed " +
                    "room can leave one cloud per corpse with nothing trimming " +
                    "them. It is still bounded - every enemy dies once - but " +
                    "set a number if you would rather not find the ceiling in " +
                    "a boss room.");
            }

            if (cfg.tickRate <= 0.02f)
            {
                Log.LogWarning(
                    fileName + ": gas \"tickRate\": " +
                    cfg.tickRate.ToString("0.###") + " is at or below one " +
                    "physics step, so the cloud applies its payload as fast as " +
                    "the engine allows. Each application drives a hit flash, a " +
                    "hit sound and the aggro event, so this is loud rather " +
                    "than strong - \"damagePerSecond\" already means per " +
                    "second whatever the tick rate is.");
            }

            if (cfg.hitPlayer)
            {
                Log.LogWarning(
                    fileName + ": gas \"hitPlayer\" is ON, so this module's " +
                    "own clouds damage YOU as well. That is a real build and " +
                    "not a mistake, but it is off by default for a reason. " +
                    "Note \"extinguish\" reaches you either way - putting your " +
                    "own fire out is the point of an extinguisher gas.");
            }

            Log.LogInfo(
                fileName + ": onKill \"gas\" - every kill leaves a " +
                cfg.duration.ToString("0.#") + "s cloud on the corpse" +
                (cfg.grows
                    ? (", swelling " + cfg.radius.ToString("0.#") + " -> " +
                       cfg.growTo.ToString("0.#"))
                    : (", radius " + cfg.radius.ToString("0.#"))) +
                Payload(cfg) +
                ". At most " +
                (cfg.max > 0 ? cfg.max.ToString() : "unlimited") +
                " of this module's clouds exist at once, the oldest fading to " +
                "make room. " +
                (cfg.chains
                    ? "\"chains\" is ON (the default): a cloud that scores its " +
                      "own kill leaves another, so a packed room chains " +
                      "through itself. It cannot run away - every enemy dies " +
                      "once - but set \"chains\": false for one cloud per kill " +
                      "you scored yourself."
                    : "\"chains\" is OFF: only kills you score leave gas, and " +
                      "a kill by the cloud itself leaves nothing.") +
                (string.IsNullOrEmpty(cfg.damageType)
                    ? " The cloud is resisted and coloured like whichever gun " +
                      "scored the kill, which is what keeps one ship-grid " +
                      "module sensible across every weapon you carry."
                    : " Damage type is fixed to \"" + cfg.damageType +
                      "\" whatever scored the kill."));
        }

        private static string Payload(ModuleForgeGas.Config cfg)
        {
            string s = "";

            if (cfg.damagePerSecond > 0f)
                s += ", damage " + cfg.damagePerSecond.ToString("0.#") + "/s";

            if (cfg.burnPerSecond > 0f)
                s += ", burn " + cfg.burnPerSecond.ToString("0.#") + "/s";

            if (cfg.slow > 0f)
                s += ", slows";

            if (cfg.push != 0f)
                s += (cfg.push > 0f ? ", shoves outward" : ", pulls inward");

            if (cfg.extinguish > 0f)
                s += ", puts units' fires out";

            if (cfg.extinguishTerrain > 0f)
                s += ", douses terrain";

            if (cfg.igniteTerrain > 0f)
                s += ", sets terrain alight";

            return s;
        }

        private static ModuleEffect BuildKillReward(
            JObject entry, string fileName)
        {
            var resource = ForgeAssets.ResolveResource((string)entry["resource"]);

            JToken amountTok = entry["amount"] ?? entry["resourceAmount"];

            float burn = Flat(entry["clearBurn"], 0f);

            // "coolHeat" is the key; "cool" and "clearHeat" are accepted
            // because Weapon Forge's build log called its BURN field "cools"
            // until R15 and both are what somebody would try first.
            JToken heatTok =
                entry["coolHeat"] ?? entry["cool"] ?? entry["clearHeat"];

            float heat = Flat(heatTok, 0f);
            float fire = Flat(entry["buffFireRate"], 0f);
            float dmg = Flat(entry["buffDamage"], 0f);
            float dur = Flat(entry["buffDuration"], 0f);

            var effect = new KillRewardEffect
            {
                resource = resource,
                amount = SeriesOr(amountTok, 0f),
                asPickup = FlatBool(entry["asPickup"] ?? entry["drop"], false),
                chance = SeriesOr(entry["chance"], 1f),
                maxPerSecond = Flat(entry["maxPerSecond"], 0f),
                clearBurn = SeriesOr(entry["clearBurn"], 0f),
                coolHeat = SeriesOr(heatTok, 0f),
                buffDuration = dur,
                buffFireRate = SeriesOr(entry["buffFireRate"], 0f),
                buffDamage = SeriesOr(entry["buffDamage"], 0f),
                buffMaxStacks = FlatInt(entry["buffMaxStacks"], 1),
                buffIndicator = (string)entry["buffIndicator"] ?? "",
                gas = BuildGas(entry["gas"] as JObject, fileName)
            };

            // A payout with no resource named is the commonest mistake here,
            // and it is silent otherwise - the effect installs and simply never
            // gives anything.
            bool wantsResource = amountTok != null;

            if (wantsResource && resource == null)
            {
                Log.LogWarning(
                    fileName + ": onKill has an \"amount\" but no valid " +
                    "\"resource\", so there is nothing to grant. Name a " +
                    "resource, or drop the amount and use \"clearBurn\" / the " +
                    "buff instead.");
            }

            if ((fire > 0f || dmg > 0f) && dur <= 0f)
            {
                Log.LogWarning(
                    fileName + ": onKill sets \"buffFireRate\" or " +
                    "\"buffDamage\" but no \"buffDuration\", so the buff " +
                    "would last no time at all and has been ignored. Give it a " +
                    "duration in seconds.");
            }

            if (dur > 0f && fire <= 0f && dmg <= 0f)
            {
                Log.LogWarning(
                    fileName + ": onKill has a \"buffDuration\" but neither " +
                    "\"buffFireRate\" nor \"buffDamage\", so the buff does " +
                    "nothing.");
            }

            // THIS LIST HAS TO LEARN EVERY NEW KEY, and forgetting to update it
            // is not a cosmetic slip - it cost a whole test round on the weapon
            // side. R17's test 231 was run against a build without `onKill.gas`
            // and the only evidence anyone had was this same warning printing
            // its OLD key set, which reads as "your file is wrong" when it
            // means "this build has never heard of that key". His log carried
            // the proof and nobody could see it.
            bool wantsGas = effect.gas != null && effect.gas.DoesAnything;

            if (!wantsResource && burn <= 0f && heat <= 0f && dur <= 0f &&
                !wantsGas)
            {
                Log.LogWarning(
                    fileName + ": onKill pays out nothing at all. Set at least " +
                    "one of \"amount\" (with a \"resource\"), " +
                    "\"clearBurn\" (puts out your ship burning), " +
                    "\"coolHeat\" (takes heat off an \"overheat\" weapon), " +
                    "\"gas\" (leaves a cloud on the corpse), " +
                    "or a buff.");
            }

            // Said at BUILD time as well as on the first kill, because this one
            // depends on which mods are installed rather than on the file - and
            // the answer is already known here.
            if (heat > 0f && !ForgeInterop.HasWeaponOverheat)
            {
                Log.LogWarning(
                    fileName + ": onKill \"coolHeat\" needs WEAPON FORGE - the " +
                    "\"overheat\" meter is a per-weapon block from a Weapon " +
                    "Forge weapon file, and Module Forge deliberately has no " +
                    "heat system of its own to fall back on (one owner per " +
                    "mechanic). The rest of this module still works; this key " +
                    "will do nothing.");
            }

            return effect;
        }

        // ★ THE ONE OWNER OF "which tint mode did this file ask for", AND EVERY
        // TINTED EFFECT MUST COME THROUGH IT.
        //
        // `ModuleForgeUnitTint.ParseMode` returns **-1** for a word it does not
        // know - deliberately, so the caller can warn instead of silently
        // handing back the default. It returns -1 for a MISSING word too, and
        // that is the trap: a layer published with mode -1 is skipped by BOTH
        // passes in `ModuleForgeUnitTint.Write` (`if (mode != Replace) continue`
        // and `if (mode != Multiply) continue`), so **the tint is computed,
        // stored, re-asserted every frame and never drawn**. No warning
        // anywhere, because nothing was wrong with the colour.
        //
        // `contactDamage` and `execute` both called ParseMode directly and both
        // shipped with exactly that: a working tint that could only appear if
        // the file happened to spell out "tintMode". Found 2026-09-18 while
        // building `slow`, by reading the two `continue` guards rather than by
        // any test - a tint that does not appear reads as a colour choice.
        //
        // The parameter exists so the warning names the block the reader is
        // editing; it used to say "crit" for every caller, which is the same
        // family of wrong-name-in-a-diagnostic bug as `HUD_GridTiles_07`.
        private static int TintMode(JObject entry, string fileName, string block)
        {
            JToken t = (entry != null)
                ? (entry["tintMode"] ?? entry["colorMode"])
                : null;

            if (t == null)
                return ModuleForgeUnitTint.Multiply;

            string raw = (t.Type == JTokenType.String) ? (string)t : null;
            int mode = ModuleForgeUnitTint.ParseMode(raw);

            if (mode < 0)
            {
                Log.LogWarning(
                    fileName + ": " + block + " \"tintMode\": \"" + raw +
                    "\" is not " +
                    "one I know, so it stays \"multiply\". The choices are " +
                    "\"multiply\" (tints by darkening - the original " +
                    "behaviour, also spelled \"tint\") and \"replace\" " +
                    "(paints the colour on, so it can brighten a dark enemy " +
                    "and reads much stronger).");

                return ModuleForgeUnitTint.Multiply;
            }

            return mode;
        }

        private static Resource Res(JObject entry, string fileName)
        {
            var r = ForgeAssets.ResolveResource((string)entry["resource"]);

            if (r == null)
                Log.LogWarning(
                    fileName + ": effect '" + (string)entry["type"] +
                    "' has no valid \"resource\".");

            return r;
        }

        // crit - a chance for your hits to do multiplied damage.
        //
        // EVERY scalar read here goes through Flat / FlatBool / SeriesOr. That
        // is not style: every series-capable param arrives as
        // {baseValue, increaseMethod, change} the moment a "per level" box is
        // filled on the builder page, and a plain (float?) cast on that object
        // THROWS - which abandons the whole effect and builds a module with no
        // effects and no card line. That has cost a test round before.
        private static ModuleEffect BuildCrit(JObject entry, string fileName)
        {
            var effect = new CritEffect
            {
                chance = Percent(
                    SeriesOr(entry["chance"] ?? entry["critChance"], 0f),
                    fileName),
                multiplierBonus = SeriesOr(
                    entry["multiplierBonus"] ?? entry["extraMultiplier"] ??
                    entry["critDamage"], 0f),
                overflow = FlatBool(entry["overflow"], true),
                impact = FlatBool(entry["impact"], true),
                impactScale = Mathf.Max(
                    1.05f, Flat(entry["impactScale"], 2f)),
                colorSeconds = Mathf.Max(
                    0.05f, Flat(entry["colorSeconds"], 0.25f)),
                colorPulse = Mathf.Max(0f, Flat(entry["colorPulse"], 0f)),

                // HOW HARD THE CRIT FLASH COMES THROUGH, 0..1 - his R13 ask,
                // and the same key is on every tinting block in Weapon Forge.
                //
                // Both spellings, because this mod's own blocks are split
                // between `color*` and `tint*` naming and making the author
                // remember which one a given block uses is a bug generator.
                //
                // THROUGH `Flat`, NOT A `float?` CAST. Every series-capable
                // param can arrive as `{baseValue, increaseMethod, change}` the
                // moment a "per level" box is filled, and a plain cast throws
                // on that - which once abandoned a whole effect from inside a
                // line that only wanted to log a warning. This one is not
                // series-capable, but the rule is about the SHAPE that can
                // arrive, not about what the field means.
                tintStrength = Mathf.Clamp01(
                    Flat(entry["tintStrength"] ?? entry["colorStrength"], 1f)),

                // multiply (the default) or replace. A multiply can only
                // darken, so no strength value makes a pale flash show up on a
                // dark enemy - see ModuleForgeUnitTint. An unrecognised word
                // warns rather than quietly staying on multiply.
                tintMode = TintMode(entry, fileName, "crit"),

                shake = FlatBool(entry["shake"], false)
            };

            // "multiplier" is deliberately NOT an alias. On a WEAPON (Weapon
            // Forge) that word means the absolute - 2 is double damage. Here it
            // is a bonus added to the x2 a crit already does, so accepting the
            // same word for a different meaning would be a trap: someone writing
            // "multiplier": 2 would get x4 and no warning.
            if (entry["multiplier"] != null)
            {
                Log.LogWarning(
                    fileName + ": a crit module uses \"multiplierBonus\", not " +
                    "\"multiplier\" - a crit ALREADY does x" +
                    ModuleForgeCrit.BaseMultiplier.ToString("0.##") +
                    " and a module ADDS to that, so \"multiplierBonus\": 0.5 " +
                    "means x" +
                    (ModuleForgeCrit.BaseMultiplier + 0.5f).ToString("0.##") +
                    " and 0 means a plain double-damage crit. (Weapon Forge's " +
                    "weapon-level \"crit.multiplier\" IS the absolute - two " +
                    "different words for the two things, on purpose.) The " +
                    "\"multiplier\" you wrote is being ignored.");
            }

            string rollText = ((string)entry["roll"] ?? "hit")
                .Trim().ToLowerInvariant();

            if (rollText == "shot" || rollText == "projectile")
                effect.roll = ModuleForgeCrit.Roll.Shot;
            else if (rollText == "pull" || rollText == "volley")
                effect.roll = ModuleForgeCrit.Roll.Pull;
            else
                effect.roll = ModuleForgeCrit.Roll.Hit;

            // The sound. Weapon Forge owns the custom-audio pipeline, so a
            // custom name only resolves when it is installed; anything else is
            // treated as a stock guid and checked, so a typo is reported rather
            // than being silently mute.
            string sfx = (string)entry["sfx"] ?? (string)entry["sound"];

            if (!string.IsNullOrEmpty(sfx))
            {
                // CUSTOM FIRST (Weapon Forge's folder, when installed), then
                // one of the GAME's own sound NAMES, then treat it as a raw
                // guid. The middle step is new: until 2026-09-02 a game sound
                // was reachable only by typing its guid, because both mods
                // matched on guid alone. See ForgeAssets.StockSound.
                string guid = ForgeInterop.TryResolveSound(sfx, fileName);

                if (string.IsNullOrEmpty(guid))
                    guid = ForgeAssets.StockSound(sfx, fileName);

                effect.sfxGuid =
                    !string.IsNullOrEmpty(guid) ? guid : sfx.Trim();

                if (!ForgeInterop.IsKnownSoundGuid(effect.sfxGuid))
                {
                    Log.LogWarning(
                        fileName + ": crit \"sfx\": '" + sfx + "' is not a " +
                        "sound I know, so the crit will be silent. Custom " +
                        "sounds live in Weapon Forge's sounds folder and need " +
                        "that mod installed; a GAME sound is named the way " +
                        "the audio database names it, with slashes - all 259 " +
                        "are listed in SOUNDS.txt next to this mod" +
                        (ForgeInterop.HasCustomSounds
                            ? "; the startup log lists what loaded."
                            : " - it is not, so only the game's own sound " +
                              "ids work here."));
                }
            }

            string colorText = (string)entry["color"];

            if (!string.IsNullOrEmpty(colorText))
            {
                var colorAsset = ForgeAssets.ResolveColor(colorText);

                if (colorAsset != null)
                {
                    effect.hasColor = true;
                    effect.color = colorAsset.color;
                    effect.colorLabel = colorText.Trim();
                }
                else
                {
                    Log.LogWarning(
                        fileName + ": crit \"color\": '" + colorText +
                        "' is not a colour I can read, so no crit tint.");
                }
            }

            float chance = effect.chance.baseValue;

            // The per-level `change` counts as asking for something: a module
            // that only crits once a BoosterCore has raised it is odd but
            // entirely legitimate, and testing baseValue alone would reject it
            // silently at build time - the worst way for it to fail.
            bool asksForSomething =
                chance != 0f || effect.chance.change != 0f ||
                effect.multiplierBonus.baseValue != 0f ||
                effect.multiplierBonus.change != 0f;

            if (!asksForSomething)
            {
                Log.LogWarning(
                    fileName + ": a crit module with no \"chance\" and no " +
                    "\"multiplierBonus\" does nothing at all - skipped. Set " +
                    "\"chance\": 0.15 for 15%.");
                return null;
            }

            // Say what it MEANS in play, not just what parsed. A chance and a
            // multiplier are hard to feel without the average.
            float multiplier =
                ModuleForgeCrit.BaseMultiplier + effect.multiplierBonus.baseValue;

            if (chance <= 0f)
            {
                Log.LogInfo(
                    fileName + ": this crit module changes crit DAMAGE only " +
                    "(x" + multiplier.ToString("0.##") + ") and grants no " +
                    "chance of its own, so on its own it does nothing - it " +
                    "makes the crits you get from a WEAPON or another module " +
                    "hit harder. That is a legitimate design; noting it in " +
                    "case it was not the intent.");
            }
            else
            {
                float avg = 1f +
                    Mathf.Min(chance, 8f) * (multiplier - 1f);

                Log.LogInfo(
                    fileName + ": crit module - " +
                    (chance * 100f).ToString("0.#") + "% for x" +
                    multiplier.ToString("0.##") + ", rolled " +
                    (effect.roll == ModuleForgeCrit.Roll.Shot
                        ? "once per SHOT (a shotgun rolls per pellet)"
                        : effect.roll == ModuleForgeCrit.Roll.Pull
                            ? "once per TRIGGER PULL (a whole blast crits " +
                              "together)"
                            : "once per THING HIT (a piercing shot rolls " +
                              "again for each enemy)") +
                    ". Average damage is about x" + avg.ToString("0.##") +
                    " over time. WHICH GRID DECIDES THE SCOPE: in a WEAPON's " +
                    "grid only that gun crits; on the SHIP grid every weapon " +
                    "does.");
            }

            // The game has NO floating damage numbers anywhere, so a crit with
            // every cue switched off is genuinely invisible.
            if (!effect.impact && !effect.hasColor && !effect.shake &&
                string.IsNullOrEmpty(effect.sfxGuid))
            {
                Log.LogWarning(
                    fileName + ": this crit module has EVERY cue switched " +
                    "off, and this game has no floating damage numbers - so a " +
                    "crit will be completely invisible. Turn \"impact\" back " +
                    "on, or set \"color\" / \"sfx\" / \"shake\".");
            }

            return effect;
        }

        // leech - your hits heal you.
        //
        // Every scalar through Flat / FlatBool / SeriesOr, for the reason the
        // file's other builders record: a series-capable param arrives as
        // {baseValue, increaseMethod, change} the moment a "per level" box is
        // filled, and a plain (float?) cast on that object THROWS - abandoning
        // the whole effect and building a module with no effects and no card.
        private static ModuleEffect BuildLeech(JObject entry, string fileName)
        {
            var effect = new LeechEffect
            {
                fraction = SeriesOr(
                    entry["fraction"] ?? entry["share"] ?? entry["percent"], 0f),
                flat = SeriesOr(entry["flat"] ?? entry["amount"], 0f),
                maxPerSecond = SeriesOr(entry["maxPerSecond"], 0f),
                perEnemyDelay =
                    Mathf.Max(0f, Flat(entry["perEnemyDelay"], 0f)),
                orb = FlatBool(entry["orb"], true),
                orbSprite = (string)entry["orbSprite"] ??
                            "area_zero_particle_tuff",
                orbFps = Mathf.Max(1f, Flat(entry["orbFps"], 14f)),
                orbScale = Mathf.Max(0.01f, Flat(entry["orbScale"], 1f)),
                orbSpeed = Mathf.Max(1f, Flat(entry["orbSpeed"], 22f)),
                orbArc = Mathf.Max(0f, Flat(entry["orbArc"], 0.7f)),
                orbSpin = Flat(entry["orbSpin"], 0f),
                orbLifetime =
                    Mathf.Max(0.2f, Flat(entry["orbLifetime"], 2.5f)),
                maxOrbs = Mathf.Max(1, FlatInt(entry["maxOrbs"], 14))
            };

            string healOnText = ((string)entry["healOn"] ?? "arrival")
                .Trim().ToLowerInvariant();

            effect.healOn =
                (healOnText == "hit" || healOnText == "instant" ||
                 healOnText == "immediate" || healOnText == "now")
                    ? ModuleForgeLeech.HealOn.Hit
                    : ModuleForgeLeech.HealOn.Arrival;

            // A named pool is optional and unusual - blank means the ship's own
            // health, found from the ship rather than from the string "Health".
            string resourceText = (string)entry["resource"];

            if (!string.IsNullOrEmpty(resourceText))
            {
                effect.resource = ForgeAssets.ResolveResource(resourceText);

                if (effect.resource == null)
                {
                    Log.LogWarning(
                        fileName + ": leech \"resource\": '" + resourceText +
                        "' is not a resource I know - falling back to your " +
                        "ship's own health pool.");
                }
            }

            string sfx = (string)entry["sfx"] ?? (string)entry["sound"];

            if (!string.IsNullOrEmpty(sfx))
            {
                // CUSTOM FIRST (Weapon Forge's folder, when installed), then
                // one of the GAME's own sound NAMES, then treat it as a raw
                // guid. The middle step is new: until 2026-09-02 a game sound
                // was reachable only by typing its guid, because both mods
                // matched on guid alone. See ForgeAssets.StockSound.
                string guid = ForgeInterop.TryResolveSound(sfx, fileName);

                if (string.IsNullOrEmpty(guid))
                    guid = ForgeAssets.StockSound(sfx, fileName);

                effect.sfxGuid =
                    !string.IsNullOrEmpty(guid) ? guid : sfx.Trim();

                if (!ForgeInterop.IsKnownSoundGuid(effect.sfxGuid))
                {
                    Log.LogWarning(
                        fileName + ": leech \"sfx\": '" + sfx + "' is not a " +
                        "sound I know, so the absorb will be silent. Custom " +
                        "sounds live in Weapon Forge's sounds folder and need " +
                        "that mod installed; a GAME sound is named the way " +
                        "the audio database names it, with slashes - all 259 " +
                        "are listed in SOUNDS.txt next to this mod" +
                        (ForgeInterop.HasCustomSounds
                            ? "; the startup log lists what loaded."
                            : " - it is not, so only the game's own sound ids " +
                              "work here."));
                }
            }

            string colorText = (string)entry["orbColor"] ?? (string)entry["color"];

            if (!string.IsNullOrEmpty(colorText))
            {
                var colorAsset = ForgeAssets.ResolveColor(colorText);

                if (colorAsset != null)
                {
                    effect.orbHasColor = true;
                    effect.orbColor = colorAsset.color;
                    effect.orbColorLabel = colorText.Trim();
                }
                else
                {
                    Log.LogWarning(
                        fileName + ": leech \"orbColor\": '" + colorText +
                        "' is not a colour I can read, so the bubble keeps its " +
                        "own colours.");
                }
            }

            float share = effect.fraction.baseValue;
            float flat = effect.flat.baseValue;

            // The per-level `change` counts as asking for something: a module
            // that only drains once boosted is odd but legitimate, and testing
            // baseValue alone would reject it silently at build time.
            bool asksForSomething =
                share != 0f || effect.fraction.change != 0f ||
                flat != 0f || effect.flat.change != 0f;

            if (!asksForSomething)
            {
                Log.LogWarning(
                    fileName + ": a leech module with no \"fraction\" and no " +
                    "\"flat\" heals nothing at all - skipped. Set " +
                    "\"fraction\": 0.15 for 15% of the damage you deal.");
                return null;
            }

            // A FRACTION ABOVE 1 IS NOT CONVERTED, matching the weapon-side key
            // and differing from crit's "chance" on purpose: healing for more
            // than you dealt is a legitimate build, so dividing by 100 would put
            // it out of reach. Said loudly enough to catch a typo instead.
            if (share > 1f)
            {
                Log.LogWarning(
                    fileName + ": leech \"fraction\": " + share.ToString("0.##") +
                    " is a FRACTION, not a percent - so this heals " +
                    (share * 100f).ToString("0.#") + "% of the damage dealt, " +
                    "more than you dealt. If you meant " +
                    share.ToString("0.#") + "%, write \"fraction\": " +
                    (share / 100f).ToString("0.###") + ". NOT auto-corrected, " +
                    "because a leech above 100% is a legitimate thing to build.");
            }

            Log.LogInfo(
                fileName + ": leech module - heals " +
                ((share > 0f) ? (share * 100f).ToString("0.#") +
                                "% of the damage dealt" : "") +
                ((share > 0f && flat > 0f) ? " plus " : "") +
                ((flat > 0f) ? flat.ToString("0.##") + " a hit" : "") +
                " into " +
                ((effect.resource != null)
                    ? effect.resource.name
                    : "your ship's own health pool") +
                ", " +
                ((effect.healOn == ModuleForgeLeech.HealOn.Arrival && effect.orb)
                    ? "arriving when the absorb bubble reaches you"
                    : "the instant the hit lands") +
                ". WHICH GRID DECIDES THE SCOPE: in a WEAPON's grid only that " +
                "gun drains; on the SHIP grid every weapon does. It works on " +
                "every weapon type, because it measures the victim's own tank " +
                "rather than hooking a projectile - so a hit that was blocked, " +
                "i-framed or aimed at an enemy with no tank of that damage type " +
                "heals you nothing, and overkill pays only what the enemy had.");

            // THE NUMBER THAT DECIDES WHETHER THIS IS BALANCED.
            if (effect.maxPerSecond.baseValue <= 0f &&
                effect.maxPerSecond.change <= 0f)
            {
                Log.LogWarning(
                    fileName + ": this leech module has NO \"maxPerSecond\" " +
                    "cap. A BEAM damages its target every damageRepeatDelay " +
                    "(0.08s on the stock Caps Laser) and a piercing one does " +
                    "that to every enemy it threads - roughly 75 heals a second " +
                    "through six enemies. On the SHIP grid this module applies " +
                    "to every weapon you carry, so it can find that beam " +
                    "without you planning it. Set \"maxPerSecond\" to the most " +
                    "healing a second you want this module to be worth. (With " +
                    "Weapon Forge installed the TIGHTEST cap wins between the " +
                    "module and the weapon, so a cap here is never overridden " +
                    "by a weapon that has none.)");
            }

            return effect;
        }

        // A chance written as a percent is almost certainly meant as one: 15
        // would otherwise read as "always crit, fifteen stacks deep", which is
        // not something anybody typed on purpose. Applied to the per-level
        // `change` as well, or a scaling chance would be inconsistent with its
        // own base.
        private static FloatSeries Percent(FloatSeries s, string fileName)
        {
            if (s.baseValue <= 1f)
                return s;

            Log.LogInfo(
                fileName + ": crit \"chance\": " +
                s.baseValue.ToString("0.###") + " reads as a PERCENT, so it " +
                "is being used as " + (s.baseValue / 100f).ToString("0.###") +
                " (" + s.baseValue.ToString("0.#") + "%). Write it as a " +
                "fraction to silence this. Values above 1 as a FRACTION are " +
                "also meaningful - see \"overflow\" - so this guess only " +
                "applies above 1.");

            s.baseValue = s.baseValue / 100f;
            s.change = s.change / 100f;
            return s;
        }

        private static ModuleEffect BuildBurnColor(
            JObject entry, string fileName)
        {
            bool rgb =
                FlatBool(entry["rgb"] ?? entry["rainbow"], false);

            var effect = new BurnColorEffect
            {
                rgb = rgb,
                rgbSpeed =
                    Flat(entry["rgbSpeed"] ?? entry["speed"], 0.5f),
                saturation = Flat(entry["saturation"], 1f),
                brightness = Flat(entry["brightness"], 1f),
                includeTerrain =
                    FlatBool(entry["includeTerrain"] ?? entry["terrain"], false)
            };

            if (rgb)
            {
                effect.colorLabel = "RGB";
                return effect;
            }

            string colorText = (string)entry["color"];

            if (string.IsNullOrEmpty(colorText))
            {
                Log.LogWarning(
                    fileName + ": BurnColorEffect needs a \"color\" " +
                    "(hex or game color) or \"rgb\": true - skipped.");
                return null;
            }

            var colorAsset = ForgeAssets.ResolveColor(colorText);

            if (colorAsset == null)
            {
                Log.LogWarning(
                    fileName + ": BurnColorEffect color '" + colorText +
                    "' not found - skipped.");
                return null;
            }

            effect.color = colorAsset.color;
            effect.colorLabel = colorText;
            return effect;
        }

        // The weapon stats the game's own enum cannot name. Same arithmetic
        // model as "weaponstat" on purpose, so nobody has to learn a second one.
        private static ModuleEffect BuildExtraWeaponStat(
            JObject entry, string type, string fileName)
        {
            // The friendly aliases ARE the choice: "type": "pushforce" needs no
            // targetProperty at all. Only the generic names fall through to the
            // key, so the common case is one line of JSON.
            string alias = (type ?? "").Trim().ToLowerInvariant();

            // ★ THE NAME COLLISION, ANSWERED OUT LOUD.
            //
            // `spinup` here means the game's flat `WarmupTime` - one number,
            // applied once, no meter, no reward. Weapon Forge's `spinUp` is a
            // RAMPING METER that pays out while you hold the trigger, and this
            // mod now has that too, as `windup`.
            //
            // So a player who knows one mod writes the word and gets the other
            // mod's mechanic. Both readings are legitimate and the JSON is
            // valid either way, which is precisely why this cannot be inferred
            // from the other keys and must not be: type resolution that depends
            // on which fields happen to be present is a new failure mode, not a
            // fix. It stays unambiguous and says so instead.
            //
            // Said every time rather than once per session: it is a build-time
            // line about one file, the author is reading the log for that file,
            // and a per-session gate would hide it on the second module.
            if (alias == "spinup")
            {
                Log.LogWarning(
                    fileName + ": \"type\": \"spinup\" in a MODULE means the " +
                    "game's flat \"warmupTime\" - the delay before the gun " +
                    "starts firing, applied once, with no meter and no " +
                    "reward. If you wanted Weapon Forge's SPIN-UP (hold the " +
                    "trigger and the gun gets better as it winds up), that is " +
                    "\"windup\" here. The two mods spell this one idea " +
                    "differently on purpose, because this word was already " +
                    "taken. Write \"warmuptime\" to say you meant the delay.");
            }

            string target =
                (alias == "pushforce" || alias == "push")
                    ? ExtraWeaponStatEffect.PushForce
                    : (alias == "warmuptime" || alias == "spinup")
                        ? ExtraWeaponStatEffect.WarmupTime
                        : (alias == "barrellength" || alias == "barrel")
                            ? ExtraWeaponStatEffect.BarrelLength
                            : ((string)entry["targetProperty"] ?? "PushForce")
                                  .Trim().ToLowerInvariant();

            if (target != ExtraWeaponStatEffect.PushForce &&
                target != ExtraWeaponStatEffect.WarmupTime &&
                target != ExtraWeaponStatEffect.BarrelLength)
            {
                Log.LogWarning(
                    fileName + ": targetProperty '" + target + "' is not one " +
                    "this effect handles. It covers the stats the game's own " +
                    "\"weaponStat\" cannot reach - PushForce, WarmupTime and " +
                    "BarrelLength. For FireRate, Spread, KnockbackForce and " +
                    "the rest, use \"weaponStat\". Falling back to PushForce.");

                target = ExtraWeaponStatEffect.PushForce;
            }

            if (target == ExtraWeaponStatEffect.BarrelLength)
            {
                Log.LogInfo(
                    fileName + ": barrel length - how far in front of the ship " +
                    "a shot is born. Small on most guns and very real on a " +
                    "SHOTGUN, where every pellet spawns at the same point, so " +
                    "a longer barrel pushes the whole cone forward and clear " +
                    "of your own hull.");
            }

            // EVERY scalar through Flat/FlatBool. A series-capable param
            // arrives as {baseValue, increaseMethod, change} the moment a "per
            // level" box is filled, and a plain (float?)/(bool?) cast on that
            // object THROWS - which abandons the whole effect and builds a
            // module with no effects and no card line. That has cost a round.
            bool multiply = false;
            string op = (string)entry["operation"];

            if (!string.IsNullOrEmpty(op))
            {
                string o = op.Trim().ToLowerInvariant();

                if (o == "multiply" || o == "scale" || o == "x")
                    multiply = true;
                else if (o != "add" && o != "plus")
                {
                    Log.LogWarning(
                        fileName + ": operation '" + op + "' is not " +
                        "recognised - use \"Add\" or \"Multiply\". Using Add.");
                }
            }

            var effect = new ExtraWeaponStatEffect
            {
                targetProperty = target,
                multiply = multiply,
                deltaMode =
                    ((string)entry["deltaCalculationMode"] ?? "Constant")
                        .Trim().ToLowerInvariant(),
                value = SeriesOr(
                    entry["value"] ?? entry["amount"], 0f)
            };

            // Push is the one with a real interaction, and it fails silently:
            // Projectile.OnObjectHit returns at the piercing branch BEFORE the
            // push block, so a piercing shot never shoves anything. Worth
            // saying out loud because this mod ships a pierce module of its own.
            if (target == ExtraWeaponStatEffect.PushForce)
            {
                Log.LogInfo(
                    fileName + ": push affects what the shot HITS, not your " +
                    "own recoil (that is \"KnockbackForce\" on " +
                    "\"weaponStat\"). NOTE: a PIERCING shot never pushes at " +
                    "all - the game returns before the push - so this and a " +
                    "pierce module cancel each other out.");
            }

            return effect;
        }

        // THE WEAPON'S FEEL - the ten cosmetic properties on WeaponBase.
        //
        // Everything resolves HERE, at build time, rather than at Modify time,
        // for the reason every other resolver in this file does: a warning can
        // name the file. A module that resolved its sound on the first shot
        // could only say "something is wrong somewhere".
        private static ModuleEffect BuildWeaponCosmetic(
            JObject entry, string fileName)
        {
            var effect = new WeaponCosmeticEffect();

            int sounds = 0;

            for (int slot = 0; slot < WeaponCosmeticEffect.SfxCount; slot++)
            {
                string key = WeaponCosmeticEffect.SfxKey(slot);
                string asked = (string)entry[key];

                // "shootSfx" is the one people will reach for, so it takes the
                // two obvious shorthands as well.
                if (string.IsNullOrEmpty(asked) &&
                    slot == WeaponCosmeticEffect.Shoot)
                {
                    asked = (string)entry["sfx"] ?? (string)entry["sound"];
                }

                if (string.IsNullOrEmpty(asked))
                    continue;

                // CUSTOM FIRST (Weapon Forge's folder, when installed), then
                // one of the GAME's own sound NAMES, then treat it as a raw
                // guid. Identical to the crit and kill-reward paths - the one
                // vocabulary across both mods.
                string guid = ForgeInterop.TryResolveSound(asked, fileName);

                if (string.IsNullOrEmpty(guid))
                    guid = ForgeAssets.StockSound(asked, fileName);

                string resolved =
                    !string.IsNullOrEmpty(guid) ? guid : asked.Trim();

                if (!ForgeInterop.IsKnownSoundGuid(resolved))
                {
                    // NOT SET, deliberately. Assigning an unknown id would
                    // leave the weapon SILENT on that event - `PlaySfx`
                    // returns -1 for an id it cannot find and nobody checks -
                    // so a typo would take away a sound the gun already had.
                    // Leaving it alone keeps the template's own.
                    Log.LogWarning(
                        fileName + ": cosmetic \"" + key + "\": '" + asked +
                        "' is not a sound I know, so the weapon KEEPS ITS " +
                        "OWN " + WeaponCosmeticEffect.SfxLabel(slot) +
                        " rather than being silenced. Custom sounds live in " +
                        "Weapon Forge's sounds folder and need that mod " +
                        "installed; a GAME sound is named the way the audio " +
                        "database names it, with slashes - all 259 are " +
                        "listed in SOUNDS.txt next to this mod" +
                        (ForgeInterop.HasCustomSounds
                            ? "; the startup log lists what loaded."
                            : " - it is not, so only the game's own sound " +
                              "names work here."));

                    continue;
                }

                effect.sfxGuid[slot] = resolved;
                effect.sfxName[slot] = asked.Trim();
                sounds++;
            }

            // ---- particles --------------------------------------------------
            string muzzle =
                (string)entry["muzzleParticle"] ??
                (string)entry["muzzle"] ??
                (string)entry["muzzleParticlePrefab"];

            if (!string.IsNullOrEmpty(muzzle))
            {
                effect.muzzleParticle =
                    ForgeAssets.ResolveParticlePrefab(muzzle, fileName);

                if (effect.muzzleParticle == null)
                {
                    Log.LogWarning(
                        fileName + ": muzzle flash '" + muzzle + "' is not a " +
                        "particle system in the game, so the weapon keeps its " +
                        "own. The eleven stock ones are \"MuzzleParticle\" " +
                        "plus Beacon, CrawlerLaser, Cross, CrossAlpha, Drone, " +
                        "Fly, Laser, LaserRed, Popper, PopperRed or Sniper.");
                }
            }

            string reloadFx =
                (string)entry["reloadParticle"] ??
                (string)entry["reloadParticlePrefab"];

            if (!string.IsNullOrEmpty(reloadFx))
            {
                effect.reloadParticle =
                    ForgeAssets.ResolveParticlePrefab(reloadFx, fileName);

                if (effect.reloadParticle == null)
                {
                    Log.LogWarning(
                        fileName + ": reload effect '" + reloadFx + "' is " +
                        "not a particle system in the game, so the weapon " +
                        "keeps its own.");
                }
            }

            // ---- shake ------------------------------------------------------
            string shakeName = (string)entry["shakePreset"];

            if (!string.IsNullOrEmpty(shakeName))
            {
                Type shakeType = WeaponCosmeticEffect.ShakePresetType();

                effect.shakePreset = (shakeType != null)
                    ? ForgeAssets.FindAsset(shakeType, shakeName.Trim())
                    : null;

                if (effect.shakePreset == null)
                {
                    Log.LogWarning(
                        fileName + ": shake preset '" + shakeName + "' was " +
                        "not found, so the weapon keeps its own camera kick. " +
                        "The stock ones are Shake_Gunshot_Small, " +
                        "Shake_Gunshot_Medium, Shake_Collision, " +
                        "Shake_Excavate, Shake_Explosion, " +
                        "Shake_Explosion_Long, Shake_Explosion_MIN, " +
                        "Shake_Explosion_MAX, Shake_Shipdamage, Shake_Wound, " +
                        "SmallExplosion and LargeExplosion.");
                }
            }

            // Series-capable, so a BoosterCore can make the kick grow. Read
            // through SeriesOr like every other scalar here: a "per level" box
            // on the page emits an OBJECT, and a plain (float?) cast on that
            // throws and abandons the whole effect.
            if (entry["shake"] != null)
            {
                effect.hasShakeScale = true;
                effect.shakeScale = SeriesOr(entry["shake"], 1f);
            }

            if (effect.shakePreset != null && effect.hasShakeScale)
            {
                Log.LogInfo(
                    fileName + ": both \"shakePreset\" and \"shake\" were " +
                    "set. The named preset is used as-is; naming one is the " +
                    "deliberate act and the number is only an adjustment to " +
                    "the weapon's OWN kick, so the two cannot combine.");
            }

            // ---- rumble -----------------------------------------------------
            string rumbleName = (string)entry["rumblePreset"];

            if (!string.IsNullOrEmpty(rumbleName))
            {
                effect.rumblePreset = ForgeAssets.FindAsset(
                    typeof(RumblePreset), rumbleName.Trim()) as RumblePreset;

                if (effect.rumblePreset == null)
                {
                    Log.LogWarning(
                        fileName + ": rumble preset '" + rumbleName + "' was " +
                        "not found, so the weapon keeps its own. The stock " +
                        "ones are \"Rumble WeaponShot\" plus Small, Medium, " +
                        "Large, Flame or Derbis, and \"Rumble Ship\" plus " +
                        "Boost, Damage or Death.");
                }
            }

            if (entry["rumble"] != null)
            {
                effect.hasRumbleScale = true;
                effect.rumbleScale = SeriesOr(entry["rumble"], 1f);
            }

            if (effect.rumblePreset != null && effect.hasRumbleScale)
            {
                Log.LogInfo(
                    fileName + ": both \"rumblePreset\" and \"rumble\" were " +
                    "set; the named preset is used as-is.");
            }

            // THE GAMEPAD CAVEAT, said once at build time where the file can be
            // named. `ShipGamepadRumble.Rumble` returns immediately unless the
            // ship is on a gamepad, and what survives that is scaled again by a
            // per-player rumble slider in the options - so this key can do
            // nothing for a player through no fault of the module. Said here
            // rather than discovered; the card carries "(PAD)" for the same
            // reason.
            if (effect.rumblePreset != null || effect.hasRumbleScale)
            {
                Log.LogInfo(
                    fileName + ": rumble is GAMEPAD ONLY - on keyboard and " +
                    "mouse this part of the module does nothing at all, and " +
                    "even on a pad it is scaled by the rumble setting in the " +
                    "game's options. The card says \"(PAD)\" so a player is " +
                    "not left guessing.");
            }

            // NOTHING ASKED FOR IS A REAL MISTAKE, not a no-op to shrug at: the
            // module builds, takes a grid slot, shows no lines and does nothing,
            // which reads as the mod being broken rather than the file being
            // empty. Same reasoning as the "tint strength with no tint" warning
            // in Weapon Forge.
            if (sounds == 0 &&
                effect.muzzleParticle == null &&
                effect.reloadParticle == null &&
                effect.shakePreset == null && !effect.hasShakeScale &&
                effect.rumblePreset == null && !effect.hasRumbleScale)
            {
                Log.LogWarning(
                    fileName + ": a \"cosmetic\" effect was declared but " +
                    "nothing in it was set, so it will do nothing and show " +
                    "no lines on the card. The keys are shootSfx, " +
                    "continuousShootSfx, startSfx, releaseSfx, warmupSfx, " +
                    "reloadSfx, muzzleParticle, reloadParticle, shake / " +
                    "shakePreset and rumble / rumblePreset.");
            }

            // WEAPON GRID ONLY, and this is the one thing about the effect that
            // is not discoverable from the card. IWeaponModifier.Modify is only
            // called for modules in a WEAPON's cluster, so the same file on the
            // ship grid is inert - no warning is possible at runtime because
            // the effect simply never runs.
            Log.LogInfo(
                fileName + ": cosmetics apply to the WEAPON this module is " +
                "connected to, so the module has to sit in a weapon's grid. " +
                "On the ship grid it does nothing.");

            return effect;
        }

        private static ModuleEffect BuildStealth(JObject entry, string fileName)
        {
            string trigger =
                ((string)entry["trigger"] ?? "dash").Trim().ToLowerInvariant();

            // Friendly spellings for the two that read oddly.
            if (trigger == "boost" || trigger == "sprinting")
                trigger = StealthEffect.Sprint;
            else if (trigger == "quiet" || trigger == "notfiring" ||
                     trigger == "gotoground")
                trigger = StealthEffect.Idle;
            else if (trigger == "damaged" || trigger == "onhit")
                trigger = StealthEffect.Hit;
            else if (trigger == "onkill")
                trigger = StealthEffect.Kill;

            if (!StealthEffect.KnownTrigger(trigger))
            {
                Log.LogWarning(
                    fileName + ": stealth trigger '" + trigger + "' is not " +
                    "recognised. Valid: dash, sprint, idle, kill, hit, " +
                    "always. Using dash.");

                trigger = StealthEffect.Dash;
            }

            bool reveal = FlatBool(entry["revealOnFire"], true);

            // The balance warning, said once and plainly. Vision only ever
            // checks the flag, so a cloak that survives firing is not a stealth
            // mechanic - it is an off switch for the enemy AI.
            if (!reveal)
            {
                Log.LogWarning(
                    fileName + ": stealth has \"revealOnFire\": false. " +
                    "Shooting will NOT reveal you, and enemy Vision checks " +
                    "nothing except the invisible flag - so while this is " +
                    "active nothing can target you at all, whatever you do. " +
                    "That is deliberate rope; just know it is the strongest " +
                    "setting in the mod.");
            }

            if (trigger == StealthEffect.Always && reveal)
            {
                Log.LogInfo(
                    fileName + ": stealth \"always\" still breaks when you " +
                    "fire, so it reads as 'hidden until you shoot'. Set " +
                    "\"revealOnFire\": false for a true permanent cloak.");
            }

            return new StealthEffect
            {
                trigger = trigger,
                seconds = SeriesOr(
                    entry["seconds"] ?? entry["duration"] ?? entry["value"], 1f),
                idleSeconds = Flat(entry["idleSeconds"], 2f),
                revealOnFire = reveal,
                revealSeconds = Flat(entry["revealSeconds"], 1f)
            };
        }

        private static ModuleEffect BuildLastStand(
            JObject entry, string fileName)
        {
            bool onKill = FlatBool(entry["onKill"], false);

            // Default the threshold on only when nothing else was asked for,
            // so "onKill": true alone does not silently also fire at low
            // health.
            bool onThreshold = FlatBool(entry["onThreshold"], !onKill);

            if (!onThreshold && !onKill)
            {
                Log.LogWarning(
                    fileName + ": a last-stand effect has neither " +
                    "\"onThreshold\" nor \"onKill\", so nothing could ever " +
                    "trigger it. Turning the health threshold on.");

                onThreshold = true;
            }

            float cooldown = Flat(entry["cooldown"], 15f);

            // Without a cooldown this is simply immortality, which is worth
            // naming rather than letting somebody discover by becoming
            // unkillable.
            if (cooldown <= 0f)
            {
                Log.LogWarning(
                    fileName + ": last stand has no cooldown, so it can " +
                    "re-arm the instant it ends - that is permanent " +
                    "immortality, not a last stand. Using 15 seconds.");

                cooldown = 15f;
            }

            return new LastStandEffect
            {
                onThreshold = onThreshold,
                threshold = Mathf.Clamp01(Flat(entry["threshold"], 0.25f)),
                onKill = onKill,
                seconds = SeriesOr(
                    entry["seconds"] ?? entry["duration"] ?? entry["value"], 3f),
                cooldown = cooldown
            };
        }

        // How the SHIP handles. Same arithmetic as the weapon stats.
        private static ModuleEffect BuildShipStat(
            JObject entry, string type, string fileName)
        {
            string alias = (type ?? "").Trim().ToLowerInvariant();

            // The friendly type names pick a sensible stat, so
            // "type": "dash" needs no targetProperty.
            string target;

            switch (alias)
            {
                case "speed":
                case "engine":
                    target = ModuleForgeShipStats.MaxSpeed;
                    break;
                case "dash":
                    target = ModuleForgeShipStats.DashCooldown;
                    break;
                case "iframes":
                    target = ModuleForgeShipStats.IFrames;
                    break;
                default:
                    target = ((string)entry["targetProperty"] ?? "MaxSpeed")
                        .Trim().ToLowerInvariant();
                    break;
            }

            if (!ModuleForgeShipStats.Known(target))
            {
                Log.LogWarning(
                    fileName + ": ship stat '" + target + "' is not one this " +
                    "effect handles. Valid: MaxSpeed, Acceleration, DashCost, " +
                    "DashCooldown, DashDuration, DashForce, IFrames, " +
                    "FuelCost, BoostFuelCost, BoostDelay, BoostKick, " +
                    "BoostKickCap, DashPush, and the fire resistance set - " +
                    "FireCooling, FireDamage, FireTickRate, FireThreshold, " +
                    "MaxBurn, IgniteSpike. Falling back to MaxSpeed.");

                target = ModuleForgeShipStats.MaxSpeed;
            }

            bool multiply = false;
            string op = (string)entry["operation"];

            if (!string.IsNullOrEmpty(op))
            {
                string o = op.Trim().ToLowerInvariant();

                if (o == "multiply" || o == "scale" || o == "x")
                    multiply = true;
                else if (o != "add" && o != "plus")
                {
                    Log.LogWarning(
                        fileName + ": operation '" + op + "' is not " +
                        "recognised - use \"Add\" or \"Multiply\". Using Add.");
                }
            }

            // The stats where LOWER is the upgrade. Saying so once here beats
            // every author discovering it by making their dash slower.
            if (!multiply &&
                (target == ModuleForgeShipStats.DashCooldown ||
                 target == ModuleForgeShipStats.DashCost ||
                 target == ModuleForgeShipStats.FuelCost ||
                 target == ModuleForgeShipStats.BoostFuelCost ||
                 target == ModuleForgeShipStats.BoostDelay ||
                 target == ModuleForgeShipStats.FireDamage ||
                 target == ModuleForgeShipStats.MaxBurn ||
                 target == ModuleForgeShipStats.IgniteSpike))
            {
                Log.LogInfo(
                    fileName + ": on '" + target + "' a NEGATIVE value is the " +
                    "upgrade - it is a cost, a delay or damage taken, so less " +
                    "is better.");
            }

            // THE SPRINT KICK HAS A TRAP WORTH NAMING AT BUILD TIME: it is a
            // multiply on the speed you ALREADY HAVE, guarded by the cap - so
            // raising the multiplier without raising the cap buys nothing at
            // all above cap/multiplier, and a standing start gets no kick
            // whatsoever because zero times anything is zero.
            if (target == ModuleForgeShipStats.BoostKick)
            {
                Log.LogInfo(
                    fileName + ": sprint kick - this MULTIPLIES the speed you " +
                    "already have when a sprint starts (ship default x3, " +
                    "capped at 25). Two things follow: a STANDING START GETS " +
                    "NO KICK, because zero times anything is zero; and above " +
                    "about 8.3 units/second the cap is already doing the " +
                    "limiting, so raising this without also raising " +
                    "\"BoostKickCap\" changes nothing. Floored at 1 - below " +
                    "that a sprint would brake you.");
            }
            else if (target == ModuleForgeShipStats.BoostKickCap)
            {
                Log.LogInfo(
                    fileName + ": kick cap - the ceiling the sprint kick can " +
                    "throw you to (ship default 25, against a boosted top " +
                    "speed of 19). It is ALSO the gate: no kick happens at all " +
                    "once you are already at or above this. Raising it is what " +
                    "makes a bigger multiplier mean anything.");
            }
            else if (target == ModuleForgeShipStats.DashPush)
            {
                Log.LogInfo(
                    fileName + ": dash push - the SUSTAINED force applied for " +
                    "the whole dash (ship default 500 over a 0.07s " +
                    "\"DashDuration\"). \"DashForce\" is the separate one-shot " +
                    "kick at the moment the dash starts (default 400). " +
                    "Lengthening \"DashDuration\" makes this one matter more.");
            }

            // THE FIRE STATS POINT IN THREE DIRECTIONS and it is genuinely not
            // guessable from the names, so spell it out per stat rather than
            // making the author work it out from a burning ship.
            if (ModuleForgeShipStats.IsBurnStat(target))
            {
                string how;

                if (target == ModuleForgeShipStats.FireCooling)
                    how = "HIGHER is better - burn is shed this fast per " +
                          "second, and the ship's own value is 5";
                else if (target == ModuleForgeShipStats.FireTickRate)
                    how = "HIGHER is better - it is the GAP between damage " +
                          "ticks in seconds, so a bigger number burns you less " +
                          "often. The ship's own value is 1";
                else if (target == ModuleForgeShipStats.FireThreshold)
                    how = "HIGHER is better - burn must exceed this before you " +
                          "catch fire at all, and the ship's own value is 10";
                else if (target == ModuleForgeShipStats.FireDamage)
                    how = "LOWER is better - damage taken per tick, and the " +
                          "ship's own value is 1";
                else if (target == ModuleForgeShipStats.MaxBurn)
                    how = "LOWER is better - burn cannot stack past this, and " +
                          "the ship's own value is 20. Take it to 10 or below " +
                          "and the ship can NEVER catch fire, because burn is " +
                          "clamped under the ignition threshold";
                else
                    how = "LOWER is better - this much extra burn is added the " +
                          "instant you catch fire, and the ship's own value is 5";

                Log.LogInfo(fileName + ": fire resistance on '" + target +
                            "' - " + how + ".");
            }

            return new ShipStatEffect
            {
                targetProperty = target,
                multiply = multiply,
                deltaMode =
                    ((string)entry["deltaCalculationMode"] ?? "Constant")
                        .Trim().ToLowerInvariant(),
                value = SeriesOr(entry["value"] ?? entry["amount"], 0f)
            };
        }

        // Terrain conversion. The names are CellType ASSET names and there are
        // sixteen of them, so a miss lists every one rather than making anyone
        // guess.
        private static readonly string[] CellNames =
        {
            "CellType Bark", "CellType Gold", "CellType_Blob",
            "CellType_Hazard", "CellType_Moss", "CellType_Night",
            "CellType_Shard", "CellType_Slime", "CellType_Solid",
            "CellType_Tuff", "CellType_Panel", "CellType_Obsidian",
            "CellType_Mud", "CellType Fog Mist", "CellType Fog Musk",
            "CellType Fog Steam"
        };

        // Keyword groups for "convert", chosen from the COLLIDER and the DROP
        // TABLE - the two things that actually decide what a terraforming
        // weapon does. Kept in step with Weapon Forge's copy deliberately: a
        // table of constant asset names is not worth a cross-mod dependency,
        // and each mod has to work with the other absent. Returns null for an
        // ordinary asset name.
        private static string[] CellGroup(string word)
        {
            switch ((word ?? "").Trim().ToLowerInvariant())
            {
                case "all":
                case "everything":
                    return new[]
                    {
                        "CellType Bark", "CellType Gold", "CellType_Blob",
                        "CellType_Hazard", "CellType_Moss", "CellType_Night",
                        "CellType_Shard", "CellType_Solid", "CellType_Tuff",
                        "CellType_Panel", "CellType_Obsidian", "CellType_Mud",
                        "CellType_Slime", "CellType Fog Mist",
                        "CellType Fog Musk", "CellType Fog Steam"
                    };

                case "solid":
                case "walls":
                case "rock":
                    return new[]
                    {
                        "CellType Bark", "CellType Gold", "CellType_Blob",
                        "CellType_Hazard", "CellType_Moss", "CellType_Night",
                        "CellType_Shard", "CellType_Solid", "CellType_Tuff",
                        "CellType_Panel", "CellType_Obsidian", "CellType_Mud"
                    };

                // The three that PAY when destroyed - i.e. a mining weapon.
                case "resources":
                case "resource":
                case "ore":
                case "mine":
                    return new[]
                    {
                        "CellType_Tuff", "CellType_Blob", "CellType Gold"
                    };

                case "soft":
                case "passable":
                    return new[]
                    {
                        "CellType_Slime", "CellType Fog Mist",
                        "CellType Fog Musk", "CellType Fog Steam"
                    };

                default:
                    return null;
            }
        }

        private static CellType ResolveCell(string name, string fileName)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            string want = name.Trim();

            // Friendly short forms, because "CellType_Fog Mist" is not a thing
            // anyone should have to type or remember the underscore of.
            var c = ForgeAssets.FindAsset(typeof(CellType), want) as CellType;

            if (c == null)
                c = ForgeAssets.FindAsset(
                    typeof(CellType), "CellType_" + want) as CellType;

            if (c == null)
                c = ForgeAssets.FindAsset(
                    typeof(CellType), "CellType " + want) as CellType;

            if (c == null)
            {
                Log.LogWarning(
                    fileName + ": cell type '" + name + "' not found. The " +
                    "sixteen are: " + string.Join(", ", CellNames) +
                    ". You can drop the \"CellType\" prefix - \"Fog Mist\", " +
                    "\"Solid\" and \"Slime\" all resolve. Note EMPTY is not " +
                    "one of them: it is id 0 and has no asset. Convert to a " +
                    "Fog instead - those have no collider at all, so it cuts a " +
                    "real tunnel.");
            }

            return c;
        }

        private static ModuleEffect BuildCellConvert(
            JObject entry, string fileName)
        {
            // Fog Mist by default, so a module that says nothing is a DIGGING
            // module - the most useful and most obviously fun of the sixteen.
            string into = (string)(entry["into"] ?? entry["result"] ??
                                   entry["resultCellType"]);

            CellType result = ResolveCell(
                string.IsNullOrEmpty(into) ? "CellType Fog Mist" : into,
                fileName);

            if (result == null)
            {
                Log.LogWarning(
                    fileName + ": terraform module has no valid \"into\" cell " +
                    "type, so it would rewrite terrain into nothing. Skipped.");
                return null;
            }

            var list = new List<CellType>();
            JToken conv = entry["convert"] ?? entry["convertableCells"];

            if (conv is JArray)
            {
                foreach (JToken t in (JArray)conv)
                {
                    CellType c = ResolveCell((string)t, fileName);

                    if (c != null)
                        list.Add(c);
                }
            }
            else if (conv != null && conv.Type == JTokenType.String)
            {
                // The same keywords Weapon Forge accepts, so one vocabulary
                // covers both mods. Naming sixteen cell types by hand to say
                // "everything" is not something anyone should have to do, and
                // the list is not discoverable from inside the game.
                string[] group = CellGroup((string)conv);

                if (group != null)
                {
                    for (int i = 0; i < group.Length; i++)
                    {
                        CellType c = ResolveCell(group[i], fileName);

                        if (c != null)
                            list.Add(c);
                    }

                    Log.LogInfo(
                        fileName + ": terraform \"convert\": \"" +
                        ((string)conv).Trim().ToLowerInvariant() +
                        "\" expanded to " + list.Count + " cell type(s).");
                }
                else
                {
                    CellType c = ResolveCell((string)conv, fileName);

                    if (c != null)
                        list.Add(c);
                }
            }

            Log.LogInfo(
                fileName + ": terraform - turns " +
                (list.Count > 0
                    ? list.Count + " named cell type(s)"
                    : "every SOLID cell type") +
                " into '" + result.name + "'" +
                (result.colliderType == CellType.ColliderType.None
                    ? " (no collider - this DIGS)"
                    : result.colliderType == CellType.ColliderType.Trigger
                        ? " (a trigger collider - passable)"
                        : " (a SOLID type - this BUILDS walls rather than " +
                          "digging)") +
                ". Only HITSCAN beams terraform in the base game; Weapon " +
                "Forge extends it to projectiles and lobbed shots.");

            if (result.contactDamage.amount > 0f)
            {
                Log.LogWarning(
                    fileName + ": terraform result '" + result.name +
                    "' does CONTACT DAMAGE (" +
                    result.contactDamage.amount.ToString("0.##") +
                    "). Nothing in the game filters cell contact damage by " +
                    "faction, so terrain you create will hurt YOU as happily " +
                    "as anything else. That is what the Crawler Laser does on " +
                    "purpose - just be sure you meant it.");
            }

            return new CellConvertEffect
            {
                result = result,
                convertable = list,
                radius = SeriesOr(entry["radius"], 2f)
            };
        }

        // Infinite ammo, and the instant reload that goes with it.
        //
        // EVERY scalar through Flat / FlatBool. A series-capable param arrives
        // as {baseValue, increaseMethod, change} the moment a "per level" box
        // is filled, and a plain (float?) cast on one of those THROWS - which
        // abandons the whole effect and builds a module with nothing in it.
        // That cost five tests once already.
        private static ModuleEffect BuildAmmo(JObject entry, string fileName)
        {
            string trig = ((string)entry["trigger"] ?? AmmoEffect.Kill)
                .Trim().ToLowerInvariant();

            if (trig == "passive" || trig == "on")
                trig = AmmoEffect.Always;

            if (trig != AmmoEffect.Always && trig != AmmoEffect.Kill)
            {
                Log.LogWarning(
                    fileName + ": ammo trigger '" + trig + "' is not one I " +
                    "know - use \"kill\" (a window after each kill) or " +
                    "\"always\" (passive). Using kill.");

                trig = AmmoEffect.Kill;
            }

            bool infinite = FlatBool(entry["infinite"], true);
            bool refill = FlatBool(entry["refill"], false);
            bool health = FlatBool(entry["includeHealth"], false);

            Resource only = null;
            string resName = (string)entry["resource"];

            if (!string.IsNullOrEmpty(resName))
                only = ForgeAssets.ResolveResource(resName);

            if (!infinite && !refill)
            {
                Log.LogWarning(
                    fileName + ": this ammo effect has \"infinite\" false AND " +
                    "\"refill\" false, so it does nothing at all. Turn one on.");
            }

            // THE ONE THAT NEEDS SAYING OUT LOUD. The game's own
            // RefillResources() fills every NOT-SHARED tank, and the only
            // shared resource in the game is Money - so health is in that list
            // and a naive refill is a full heal on every kill.
            if (refill && health)
            {
                Log.LogWarning(
                    fileName + ": \"includeHealth\" is on, so this refills " +
                    "HEALTH as well as ammo - a FULL HEAL on every kill. That " +
                    "is a much stronger module than a reload and it is worth " +
                    "being sure you meant it. Leave it off and only the ammo " +
                    "tanks are topped up.");
            }

            if (infinite && trig == AmmoEffect.Always)
            {
                Log.LogInfo(
                    fileName + ": infinite ammo as a PASSIVE - every weapon " +
                    "fires free, permanently. Health is never made infinite " +
                    "(that would be invulnerability, not ammo), so this is " +
                    "strong rather than unkillable.");
            }

            return new AmmoEffect
            {
                trigger = trig,
                seconds = SeriesOr(entry["seconds"] ?? entry["duration"], 3f),
                infinite = infinite,
                refill = refill,
                includeHealth = health,
                resource = only
            };
        }

        // Which resource the weapon spends.
        private static ModuleEffect BuildWeaponResource(
            JObject entry, string fileName)
        {
            Resource res = ForgeAssets.ResolveResource(
                (string)(entry["resource"] ?? entry["resourceUsed"] ??
                         entry["ammo"]));

            if (res == null)
            {
                Log.LogWarning(
                    fileName + ": an \"ammo\" effect needs a \"resource\" " +
                    "naming a resource this game has - e.g. " +
                    "\"Resource Electron\". Skipped.");

                return null;
            }

            bool free = FlatBool(entry["free"], false);

            // The silent-failure warning. Shooter gates every shot on
            // GetResource(ResourceUsed) >= Cost, so pointing a weapon at a
            // resource the ship has no tank of stops it firing with no error
            // at all - it just looks like the module broke the gun.
            if (!free)
            {
                Log.LogInfo(
                    fileName + ": ammo swapped to " + res.name + ". THE SHIP " +
                    "MUST CARRY THAT RESOURCE or the weapon will not fire at " +
                    "all - the game checks the tank before every shot and " +
                    "fails silently. Pair it with a capacity effect for the " +
                    "same resource, or set \"free\": true to zero the " +
                    "weapon's cost so it never consults a tank.");
            }

            return new WeaponResourceEffect
            {
                resource = res,
                free = free
            };
        }

        private static ModuleEffect BuildWeaponProperty(
            JObject entry, string fileName)
        {
            if (_mwpValue == null)
            {
                var t = typeof(ModifyWeaponProperty);
                var f = BindingFlags.NonPublic | BindingFlags.Instance;
                _mwpTarget = t.GetField("targetProperty", f);
                _mwpOperation = t.GetField("operation", f);
                _mwpDeltaMode = t.GetField("deltaCalculationMode", f);
                _mwpValue = t.GetField("value", f);
            }

            string targetStr =
                (string)entry["targetProperty"] ?? "FireRate";

            var target = (ModifyWeaponProperty.TargetProperty)
                Enum.Parse(typeof(ModifyWeaponProperty.TargetProperty),
                    targetStr, true);

            // Damage is genuinely broken in the game and stays blocked.
            // Verified in ModifyWeaponProperty's applyer table: eleven of the
            // twelve ASSIGN the computed result (weapon.FireRate = value),
            // but Damage does
            //     weapon.Damage = new Damage(weapon.Damage.amount + value, ...)
            // and `value` is ALREADY current+delta, so the current amount is
            // counted twice - a +2 gives you +2 plus the whole base again.
            if (target == ModifyWeaponProperty.TargetProperty.Damage)
            {
                Log.LogWarning(
                    fileName + ": weapon stat 'Damage' is broken in the " +
                    "game and is skipped. Its applyer adds the result to the " +
                    "current damage, but the result already includes it, so " +
                    "the base damage lands twice. Use an explosion / " +
                    "discharge / burn effect to add damage instead.");
                return null;
            }

            string modeStr =
                (string)entry["deltaCalculationMode"] ?? "Constant";

            var mode = (ModifyWeaponProperty.DeltaCalculationMode)
                Enum.Parse(typeof(ModifyWeaponProperty.DeltaCalculationMode),
                    modeStr, true);

            // Multiply IS implemented, contrary to the note that used to sit
            // here. ModifyWeaponProperty.Modify does
            //     (operation == Add) ? (current + delta) : (current * delta)
            // and every non-Damage applyer assigns that result cleanly, so
            // "operation": "Multiply" works on all eleven usable properties.
            // It is the natural way to write "+50% fire rate" instead of
            // having to know the weapon's base number.
            var operation = ModifyWeaponProperty.Operation.Add;
            string opStr = (string)entry["operation"];

            if (!string.IsNullOrEmpty(opStr))
            {
                try
                {
                    operation = (ModifyWeaponProperty.Operation)
                        Enum.Parse(
                            typeof(ModifyWeaponProperty.Operation),
                            opStr.Trim(), true);
                }
                catch
                {
                    Log.LogWarning(
                        fileName + ": operation '" + opStr + "' is not " +
                        "recognised - use \"Add\" or \"Multiply\". " +
                        "Falling back to Add.");
                }
            }

            var mwp = new ModifyWeaponProperty();

            _mwpTarget.SetValue(mwp, target);
            _mwpOperation.SetValue(mwp, operation);
            _mwpDeltaMode.SetValue(mwp, mode);
            _mwpValue.SetValue(mwp, Series(entry["value"] ?? entry["amount"]));

            // Multiply reads the value as a FACTOR, so the two easy mistakes
            // are worth naming: 0 wipes the stat out, and a value below 1
            // makes it worse rather than better.
            if (operation == ModifyWeaponProperty.Operation.Multiply)
            {
                float v = Flat(entry["value"] ?? entry["amount"], 0f);

                if (v == 0f)
                {
                    Log.LogWarning(
                        fileName + ": \"operation\": \"Multiply\" with a " +
                        "value of 0 sets " + target + " to ZERO. For " +
                        "\"+50%\" use 1.5.");
                }
                else if (v > 0f && v < 1f)
                {
                    Log.LogInfo(
                        fileName + ": Multiply by " + v + " REDUCES " +
                        target + " to " + (v * 100f) +
                        "% - intended for a downside?");
                }
            }

            return mwp;
        }

        // Series() with a non-zero default for an absent key. Series(null)
        // A scalar read that CANNOT be broken by a per-level value.
        //
        // WHY THIS EXISTS, because it cost five failed tests in one round and
        // the cause was three lines that only computed a WARNING:
        //
        //     float burn = (float?)entry["clearBurn"] ?? 0f;
        //
        // Every series-capable param can arrive as either a plain number or a
        // `{baseValue, increaseMethod, change}` object - the builder page emits
        // the object the moment you fill in a "per level" box. Casting that
        // object to float? throws `Can not convert Object to Single`, the whole
        // effect is abandoned, and the module builds with ZERO effects: no
        // payout, no pickup, no buff, and no card line either (an effect that
        // does not exist has nothing to describe). Four onKill modules failed
        // this way while their real assignments - all correctly using Series()
        // - were never reached. The same latent crash sat in
        // ModifyWeaponProperty's Multiply warning.
        //
        // So: no raw `(float?)entry[...]` on anything a page can make a series.
        // Route every scalar through here, which reads a number as itself and a
        // series as its baseValue. Cheaper than remembering which params are
        // series-capable, and this is the second round in a row lost to the mod
        // rejecting its own builder page's output.
        private static float Flat(JToken token, float fallback)
        {
            if (token == null || token.Type == JTokenType.Null)
                return fallback;

            if (token.Type == JTokenType.Object)
                return (float?)((JObject)token)["baseValue"] ?? fallback;

            if (token.Type == JTokenType.Array)
                return fallback;

            try
            {
                return (float?)token ?? fallback;
            }
            catch (Exception)
            {
                // A string, a bool, anything unexpected. The real assignment
                // through Series() reports its own problem; a warning helper
                // must never be the thing that kills the effect.
                return fallback;
            }
        }

        private static int FlatInt(JToken token, int fallback)
        {
            return Mathf.RoundToInt(Flat(token, fallback));
        }

        private static bool FlatBool(JToken token, bool fallback)
        {
            if (token == null || token.Type == JTokenType.Null)
                return fallback;

            if (token.Type == JTokenType.Boolean)
                return (bool)token;

            // A number or a series object standing in for a flag - 0 is false.
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float ||
                token.Type == JTokenType.Object)
            {
                return Flat(token, fallback ? 1f : 0f) != 0f;
            }

            if (token.Type == JTokenType.String)
            {
                bool parsed;

                if (bool.TryParse((string)token, out parsed))
                    return parsed;
            }

            return fallback;
        }

        // gives 0, which is right for an "amount" but wrong for something
        // like pierceCap whose historical default is 2.
        private static FloatSeries SeriesOr(JToken token, float fallback)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                var flat = new FloatSeries();
                flat.increaseMethod = FloatSeries.IncreaseMethod.Add;
                flat.change = 0f;
                flat.baseValue = fallback;
                return flat;
            }

            return Series(token);
        }

        // {baseValue, increaseMethod, change} or a plain number (flat).
        private static FloatSeries Series(JToken token)
        {
            var series = new FloatSeries();
            series.increaseMethod = FloatSeries.IncreaseMethod.Add;
            series.change = 0f;

            if (token == null)
            {
                series.baseValue = 0f;
                return series;
            }

            if (token.Type == JTokenType.Object)
            {
                var o = (JObject)token;
                series.baseValue = (float?)o["baseValue"] ?? 0f;
                series.change = (float?)o["change"] ?? 0f;

                string im = (string)o["increaseMethod"];

                if (!string.IsNullOrEmpty(im) &&
                    im.Trim().Equals("Multiply",
                        StringComparison.OrdinalIgnoreCase))
                {
                    series.increaseMethod =
                        FloatSeries.IncreaseMethod.Multiply;
                }
            }
            else
            {
                // Plain number -> flat, level-independent value.
                series.baseValue = (float)token;
            }

            return series;
        }
    }
}
