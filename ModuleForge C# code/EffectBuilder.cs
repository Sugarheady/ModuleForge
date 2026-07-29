using System;
using System.Reflection;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;

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

                    case "modifyweaponproperty":
                    case "weaponstat":
                        return BuildWeaponProperty(entry, fileName);

                    case "increaseexplosionradiuseffect":
                    case "explosionradius":
                        return new IncreaseExplosionRadiusEffect
                        {
                            increaseAmount =
                                (float?)entry["increaseAmount"] ?? 1f
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
                                (float?)entry["costPerProjectile"] ?? 0f,
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
                            pierceCap =
                                (int?)entry["pierceCap"] ??
                                (int?)entry["cap"] ?? 2,
                            falloff =
                                (float?)entry["falloff"] ??
                                (float?)entry["pierceDamageFalloff"] ?? 0f,
                            explodeOnLimit =
                                (bool?)entry["explodeOnLimit"] ??
                                (bool?)entry["pierceExplodeOnLimit"] ?? false
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
                                (float?)entry["costPerProjectile"] ?? 0f,
                            costResource =
                                ForgeAssets.ResolveResource(
                                    (string)entry["costResource"]),
                            addImpactExplosion =
                                (bool?)entry["addImpactExplosion"] ?? true,
                            addTimeoutExplosion =
                                (bool?)entry["addTimeoutExplosion"] ?? false,
                            explosionRadiusIncrement =
                                (float?)entry["explosionRadiusIncrement"] ?? 0f,
                            burn = Series(entry["burn"])
                        };

                    case "adddischargeeffect":
                    case "discharge":
                    case "spark":
                        return new AddDischargeEffect
                        {
                            chainLengthIncrement =
                                (int?)entry["chainLengthIncrement"] ?? 1,
                            damageIncrement = Series(entry["damageIncrement"]),
                            impact = (bool?)entry["impact"] ?? true,
                            timeout = (bool?)entry["timeout"] ?? false,
                            costPerProjectile =
                                (int?)entry["costPerProjectile"] ?? 0,
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
                (bool?)entry["rgb"] ??
                (bool?)entry["rainbow"] ?? false;

            var effect = new BurnColorEffect
            {
                rgb = rgb,
                rgbSpeed =
                    (float?)entry["rgbSpeed"] ??
                    (float?)entry["speed"] ?? 0.5f,
                saturation = (float?)entry["saturation"] ?? 1f,
                brightness = (float?)entry["brightness"] ?? 1f,
                includeTerrain =
                    (bool?)entry["includeTerrain"] ??
                    (bool?)entry["terrain"] ?? false
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

            // The game's Damage applyer double-adds (known bug) - steer
            // damage through explosion/discharge/burn instead.
            if (target == ModifyWeaponProperty.TargetProperty.Damage)
            {
                Log.LogWarning(
                    fileName + ": weapon stat 'Damage' is broken in the " +
                    "game (double-adds) - use an explosion/discharge/burn " +
                    "effect for damage instead. Skipped.");
                return null;
            }

            string modeStr =
                (string)entry["deltaCalculationMode"] ?? "Constant";

            var mode = (ModifyWeaponProperty.DeltaCalculationMode)
                Enum.Parse(typeof(ModifyWeaponProperty.DeltaCalculationMode),
                    modeStr, true);

            var mwp = new ModifyWeaponProperty();

            _mwpTarget.SetValue(mwp, target);
            // Operation is forced to Add - Multiply is unimplemented.
            _mwpOperation.SetValue(mwp, ModifyWeaponProperty.Operation.Add);
            _mwpDeltaMode.SetValue(mwp, mode);
            _mwpValue.SetValue(mwp, Series(entry["value"] ?? entry["amount"]));

            return mwp;
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
