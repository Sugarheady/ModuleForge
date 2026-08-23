using System;
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

                    case "modifyweaponproperty":
                    case "weaponstat":
                        return BuildWeaponProperty(entry, fileName);

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
                        Log.LogWarning(
                            fileName + ": unknown effect type '" +
                            type + "' - skipped.");
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
