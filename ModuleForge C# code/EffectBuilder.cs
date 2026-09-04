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
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge");

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

                    case "phasing":
                    case "phase":
                    case "noclip":
                        return new PhasingModuleEffect();

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
        private static string _known;

        private static string KnownEffects()
        {
            if (_known != null)
                return _known;

            _known = "Could not list the available effects.";

            try
            {
                var names = new List<string>();

                foreach (Assembly asm in
                         AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;

                    // A dynamic or partially-loaded assembly throws here, and
                    // a warning helper must never be the thing that breaks a
                    // build. Skip it and keep going.
                    try { types = asm.GetTypes(); }
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
        private static ModuleEffect BuildKillReward(
            JObject entry, string fileName)
        {
            var resource = ForgeAssets.ResolveResource((string)entry["resource"]);

            JToken amountTok = entry["amount"] ?? entry["resourceAmount"];

            float burn = Flat(entry["clearBurn"], 0f);
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
                buffDuration = dur,
                buffFireRate = SeriesOr(entry["buffFireRate"], 0f),
                buffDamage = SeriesOr(entry["buffDamage"], 0f),
                buffMaxStacks = FlatInt(entry["buffMaxStacks"], 1),
                buffIndicator = (string)entry["buffIndicator"] ?? ""
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

            if (!wantsResource && burn <= 0f && dur <= 0f)
            {
                Log.LogWarning(
                    fileName + ": onKill pays out nothing at all. Set at least " +
                    "one of \"amount\" (with a \"resource\"), " +
                    "\"clearBurn\", or a buff.");
            }

            return effect;
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
