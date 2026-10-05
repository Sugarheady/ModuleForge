using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // The one thing allowed to write a ship's movement and defence stats.
    //
    // WHY THIS IS NOT LIKE THE WEAPON EFFECTS, and it is the whole design.
    // A weapon is REBUILT FROM ITS TEMPLATE on every cluster refresh
    // (ModuleSlotWeaponHolder.RecreateWeapon -> WeaponFactory.Create), so a
    // weapon effect can safely write `current + delta` and let the next rebuild
    // wipe the slate. Nothing rebuilds ShipMovement. Its fields are live and
    // permanent, which means writing `current + delta` there would:
    //
    //   - COMPOUND on every stat recalculation, so a speed module would make
    //     the ship faster every time any module changed, without limit
    //   - never come back when the module is removed
    //
    // So: capture the base ONCE, have every module publish a contribution, and
    // recompute the total from the base each time. Order-independent, removal
    // is just dropping a contribution, and a double recalculation is harmless
    // because the result is recomputed rather than accumulated.
    //
    // Same contract as WeaponForge's ForgeDamageStack and ForgeUnitTint, for
    // the same reason: publish, never overwrite.
    public static class ModuleForgeShipStats
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.ShipStats");

        // ---- the stat table ------------------------------------------------
        public const string MaxSpeed = "maxspeed";
        public const string Acceleration = "acceleration";
        public const string DashCost = "dashcost";
        public const string DashCooldown = "dashcooldown";
        public const string DashDuration = "dashduration";
        public const string DashForce = "dashforce";
        public const string IFrames = "iframes";
        public const string FuelCost = "fuelcost";
        public const string BoostFuelCost = "boostfuelcost";
        public const string BoostDelay = "boostdelay";

        // ---- THE SPRINT KICK AND THE SUSTAINED DASH PUSH -------------------
        //
        // Tier 2 took the two INITIAL numbers and missed the two that carry
        // them. `DashForce` above is `initialDashForce`, the one-shot impulse
        // at the moment a dash begins; `constantDashForce` is what keeps
        // pushing for the whole `dashDuration` afterwards. And nothing touched
        // the sprint burst at all.
        //
        // THE SPRINT KICK IS NOT A SPEED, IT IS A MULTIPLY ON WHAT YOU HAVE:
        //
        //     // ShipMovement.StartBoosting
        //     float magnitude = rb.linearVelocity.magnitude;
        //     if (magnitude < boostExtraSpeedLimit)
        //         rb.linearVelocity = Math.Min(
        //             boostExtraSpeedLimit,
        //             magnitude * boostExtraSpeedMultiplier)
        //             * rb.linearVelocity.normalized;
        //
        // Three consequences, and the first is the one nobody guesses:
        //
        //   - **A STATIONARY SHIP GETS NO KICK AT ALL.** Zero times three is
        //     zero. The burst rewards sprinting while already moving, which is
        //     why it feels like a gear change rather than a launch.
        //   - **It does nothing once you are at or above the limit**, because
        //     of the `<` guard - so raising the multiplier without raising the
        //     limit buys you nothing above 25/3.
        //   - It preserves DIRECTION, so it is purely a speed event.
        //
        // The ship's factory numbers: multiplier 3, limit 25, cruising
        // `maxSpeed` 12.5, `maxSpeedWhileBoosted` 19. So a sprint from cruising
        // speed asks for 37.5, is capped to 25, and drag then drags you back to
        // 19 - the burst is real and deliberately temporary.
        public const string BoostKick = "boostkick";
        public const string BoostKickCap = "boostkickcap";
        public const string DashPush = "dashpush";

        // ---- the ship's own FIRE RESISTANCE --------------------------------
        //
        // `Unit.Data.burnProperties` is a public struct with six public floats,
        // sitting directly on Unit.Data - so unlike the movement stats there is
        // no component to find and no bridge to cross.
        //
        // NOT THE SAME THING as the burn effects this mod already has. Those
        // are about the burn you INFLICT. These are how fast YOU stop burning
        // and how much it hurts while you do. Nothing in the game's own module
        // system touches them.
        //
        // The ship's factory values, for reference when tuning:
        //
        //     fireThreshold 10   extraBurnLevelWhenCatchingFire 5
        //     coolingSpeed 5     fireTickRate 1
        //     fireDmgPerTick 1   maxBurnLevel 20
        //
        // Read that as: burn stacks up to 20; you IGNITE above 10; igniting
        // instantly adds 5 more (so crossing 10 puts you at 15); you shed 5 a
        // second; and while alight you take 1 damage a second. A single
        // ignition therefore burns for about a second - it is sustained
        // exposure that hurts.
        public const string FireCooling = "firecooling";
        public const string FireDamage = "firedamage";
        public const string FireTickRate = "firetickrate";
        public const string FireThreshold = "firethreshold";
        public const string MaxBurn = "maxburn";
        public const string IgniteSpike = "ignitespike";

        public static bool IsBurnStat(string stat)
        {
            switch (stat)
            {
                case FireCooling:
                case FireDamage:
                case FireTickRate:
                case FireThreshold:
                case MaxBurn:
                case IgniteSpike:
                    return true;
                default:
                    return false;
            }
        }

        // Cheap global gate so the per-frame reassert below costs one bool test
        // in every run that has no fire-resistance module in it.
        public static bool AnyBurnOwned { get; private set; }

        public static bool Known(string stat)
        {
            switch (stat)
            {
                case MaxSpeed:
                case Acceleration:
                case DashCost:
                case DashCooldown:
                case DashDuration:
                case DashForce:
                case IFrames:
                case FuelCost:
                case BoostFuelCost:
                case BoostDelay:
                case BoostKick:
                case BoostKickCap:
                case DashPush:
                    return true;
                default:
                    return IsBurnStat(stat);
            }
        }

        public static string Label(string stat)
        {
            switch (stat)
            {
                case MaxSpeed:      return "SPEED";
                case Acceleration:  return "ACCEL";
                case DashCost:      return "DASH COST";
                case DashCooldown:  return "DASH CD";
                case DashDuration:  return "DASH TIME";
                case DashForce:     return "DASH FORCE";
                case IFrames:       return "I-FRAMES";
                case FuelCost:      return "FUEL/S";
                case BoostFuelCost: return "BOOST FUEL/S";
                case BoostDelay:    return "BOOST DELAY";
                case BoostKick:     return "SPRINT KICK";
                case BoostKickCap:  return "KICK CAP";
                case DashPush:      return "DASH PUSH";

                case FireCooling:   return "FIRE COOLING";
                case FireDamage:    return "BURN DMG";
                case FireTickRate:  return "BURN INTERVAL";
                case FireThreshold: return "IGNITE AT";
                case MaxBurn:       return "MAX BURN";
                case IgniteSpike:   return "IGNITE SPIKE";

                default:            return stat.ToUpperInvariant();
            }
        }

        // ---- per-ship state ------------------------------------------------
        private class Contribution
        {
            public float add;
            public float mul = 1f;
        }

        private class ShipState
        {
            public readonly Dictionary<string, float> baseValue =
                new Dictionary<string, float>();

            // Keyed by the effect INSTANCE, so re-publishing at a new level
            // replaces rather than stacks - which is what makes a BoosterCore
            // raising Module.Level work without an install/uninstall.
            public readonly Dictionary<object, Dictionary<string, Contribution>>
                byOwner =
                    new Dictionary<object, Dictionary<string, Contribution>>();
        }

        private static readonly Dictionary<Unit.Data, ShipState> _ships =
            new Dictionary<Unit.Data, ShipState>();

        private static ShipState State(Unit.Data unit)
        {
            ShipState s;

            if (!_ships.TryGetValue(unit, out s))
            {
                s = new ShipState();
                _ships[unit] = s;
            }

            return s;
        }

        // ---- publish / withdraw --------------------------------------------
        public static void Publish(
            Unit.Data unit, object owner, string stat, float add, float mul)
        {
            if (unit == null || owner == null || !Known(stat))
                return;

            var state = State(unit);

            Dictionary<string, Contribution> mine;

            if (!state.byOwner.TryGetValue(owner, out mine))
            {
                mine = new Dictionary<string, Contribution>();
                state.byOwner[owner] = mine;
            }

            Contribution c;

            if (!mine.TryGetValue(stat, out c))
            {
                c = new Contribution();
                mine[stat] = c;
            }

            c.add = add;
            c.mul = mul;

            if (IsBurnStat(stat))
                AnyBurnOwned = true;

            Apply(unit, stat);
        }

        // ---- coordinating with the burn engine -----------------------------
        //
        // TWO SYSTEMS IN THIS MOD NOW WRITE `burnProperties.fireTickRate`, and
        // without this they would fight every frame.
        //
        // `ModuleForgeBurn.ApplyTo` runs from a prefix on
        // `DamagableResource.Update` - i.e. EVERY FRAME - and rewrites the tick
        // rate, including writing it back to its own captured base for units it
        // excludes (the player among them). A one-time write from a ship-stat
        // module would therefore be undone on the very next frame.
        //
        // The fix is ownership rather than ordering: this system owns the
        // VALUE, and the burn engine treats that value as the base it boosts
        // from. Returns a negative number when no module owns the stat, which
        // is the signal to keep using its own captured base.
        //
        // This also solves the reload problem. `Unit.CreateData()` rebuilds
        // `burnProperties` from the prefab on spawn and on continue, so any
        // stamped-once value is silently reverted - the same trap that had
        // minion colour and scale broken since the day they shipped.
        // Reasserting from the per-frame hook is what makes it stick.
        public static float Wanted(Unit.Data unit, string stat)
        {
            if (unit == null || !Known(stat))
                return -1f;

            ShipState state;

            if (!_ships.TryGetValue(unit, out state))
                return -1f;

            float baseValue;

            if (!state.baseValue.TryGetValue(stat, out baseValue))
                return -1f;

            float mul = 1f;
            float add = 0f;
            bool owned = false;

            foreach (var owner in state.byOwner)
            {
                Contribution c;

                if (owner.Value.TryGetValue(stat, out c))
                {
                    mul *= c.mul;
                    add += c.add;
                    owned = true;
                }
            }

            if (!owned)
                return -1f;

            float result = baseValue * mul + add;
            return (result < 0f) ? 0f : result;
        }

        // Re-write every burn field this system owns for one unit. Called once
        // a frame from the burn tick patch, and only when a fire-resistance
        // module exists at all.
        public static void ReassertBurn(Unit.Data unit)
        {
            if (unit == null || !AnyBurnOwned)
                return;

            Reassert(unit, FireCooling);
            Reassert(unit, FireDamage);
            Reassert(unit, FireThreshold);
            Reassert(unit, MaxBurn);
            Reassert(unit, IgniteSpike);

            // NOT FireTickRate. That one is deliberately left to the burn
            // engine, which reads `Wanted` for its base and writes the final
            // value itself - reasserting it here as well would mean two writers
            // again, in the same frame, in an order nobody could reason about.
        }

        private static void Reassert(Unit.Data unit, string stat)
        {
            float want = Wanted(unit, stat);

            if (want >= 0f && Read(unit, stat) != want)
                Write(unit, stat, want);
        }

        public static void Withdraw(Unit.Data unit, object owner)
        {
            if (unit == null || owner == null)
                return;

            ShipState state;

            if (!_ships.TryGetValue(unit, out state))
                return;

            Dictionary<string, Contribution> mine;

            if (!state.byOwner.TryGetValue(owner, out mine))
                return;

            // Remember which stats this owner touched, drop it, then recompute
            // exactly those - which restores the base when it was the only
            // contributor.
            var touched = new List<string>(mine.Keys);

            state.byOwner.Remove(owner);

            for (int i = 0; i < touched.Count; i++)
                Apply(unit, touched[i]);
        }

        // ---- the recompute --------------------------------------------------
        private static void Apply(Unit.Data unit, string stat)
        {
            try
            {
                var state = State(unit);

                float baseValue;

                if (!state.baseValue.TryGetValue(stat, out baseValue))
                {
                    // FIRST TOUCH captures the factory value. It has to happen
                    // before anything is written, which is why capture lives
                    // here rather than in the effect.
                    baseValue = Read(unit, stat);
                    state.baseValue[stat] = baseValue;

                    // Speed and acceleration each have a BOOSTED twin that has
                    // to move with them. Capture it at the same moment and
                    // anchor it to its own base, so the twin is always
                    // `twinBase * (result / base)` rather than a running
                    // multiply - the latter drifts if anything outside this
                    // system ever touches the field.
                    if (stat == MaxSpeed || stat == Acceleration)
                        state.baseValue[Twin(stat)] = ReadTwin(unit, stat);
                }

                float mul = 1f;
                float add = 0f;

                foreach (var owner in state.byOwner)
                {
                    Contribution c;

                    if (owner.Value.TryGetValue(stat, out c))
                    {
                        mul *= c.mul;
                        add += c.add;
                    }
                }

                float result = baseValue * mul + add;

                // None of these mean anything below zero, and several would be
                // actively harmful: a negative cooldown or duration puts the
                // ship in a bad state rather than a generous one.
                if (result < 0f)
                    result = 0f;

                Write(unit, stat, result);

                if (stat == MaxSpeed || stat == Acceleration)
                {
                    float twinBase;
                    state.baseValue.TryGetValue(Twin(stat), out twinBase);

                    float ratio = (baseValue > 0.0001f)
                        ? result / baseValue
                        : 1f;

                    WriteTwin(unit, stat, twinBase * ratio);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Applying ship stat '" + stat + "' failed: " + e);
            }
        }

        // Public so the card line can show where the ship actually ended up.
        public static float Current(Unit.Data unit, string stat)
        {
            return Read(unit, stat);
        }

        public static float BaseOf(Unit.Data unit, string stat)
        {
            ShipState state;
            float v;

            if (_ships.TryGetValue(unit, out state) &&
                state.baseValue.TryGetValue(stat, out v))
                return v;

            return Read(unit, stat);
        }

        // ---- the actual fields ---------------------------------------------
        private static float Read(Unit.Data unit, string stat)
        {
            // The burn fields sit directly on Unit.Data - a public struct in a
            // public field - so there is no component to look up at all.
            switch (stat)
            {
                case FireCooling:   return unit.burnProperties.coolingSpeed;
                case FireDamage:    return unit.burnProperties.fireDmgPerTick;
                case FireTickRate:  return unit.burnProperties.fireTickRate;
                case FireThreshold: return unit.burnProperties.fireThreshold;
                case MaxBurn:       return unit.burnProperties.maxBurnLevel;
                case IgniteSpike:
                    return unit.burnProperties.extraBurnLevelWhenCatchingFire;
            }

            if (stat == IFrames)
            {
                var dmg = ModuleForgeUnits.Component<DamagableResource>(unit);
                return (dmg != null) ? dmg.iFrameDuration : 0f;
            }

            var m = ModuleForgeUnits.Component<ShipMovement>(unit);

            if (m == null)
                return 0f;

            switch (stat)
            {
                case MaxSpeed:      return m.maxSpeed;
                case Acceleration:  return m.acceleration;
                case DashCost:      return m.dashCost;
                case DashCooldown:  return m.dashCooldown;
                case DashDuration:  return m.dashDuration;
                case DashForce:     return m.initialDashForce;
                case FuelCost:      return m.fuelCostPerSecond;
                case BoostFuelCost: return m.boostedFuelCostPerSecond;
                case BoostDelay:    return m.boostStartDelay;
                case BoostKick:     return m.boostExtraSpeedMultiplier;
                case BoostKickCap:  return m.boostExtraSpeedLimit;
                case DashPush:      return m.constantDashForce;
                default:            return 0f;
            }
        }

        // The boosted twins. Kept beside the main table rather than as extra
        // entries in it, because they are not separately authorable: a module
        // asks for "more speed" and gets both halves, always.
        private static string Twin(string stat)
        {
            return stat + "$boost";
        }

        private static float ReadTwin(Unit.Data unit, string stat)
        {
            var m = ModuleForgeUnits.Component<ShipMovement>(unit);

            if (m == null)
                return 0f;

            return (stat == MaxSpeed)
                ? m.maxSpeedWhileBoosted
                : m.accelerationWhileBoosted;
        }

        private static void WriteTwin(Unit.Data unit, string stat, float v)
        {
            var m = ModuleForgeUnits.Component<ShipMovement>(unit);

            if (m == null)
                return;

            if (v < 0f)
                v = 0f;

            if (stat == MaxSpeed)
                m.maxSpeedWhileBoosted = v;
            else
                m.accelerationWhileBoosted = v;
        }

        private static void Write(Unit.Data unit, string stat, float v)
        {
            // `burnProperties` is a STRUCT, but it is a FIELD of a class
            // (Unit.Data), so mutating one of its members in place is a plain
            // variable write and not a copy. `unit.burnProperties.x = v` is
            // exactly right here; it would silently do nothing if Data were a
            // struct or if this went through a property.
            switch (stat)
            {
                case FireCooling:
                    unit.burnProperties.coolingSpeed = v;
                    return;

                case FireDamage:
                    unit.burnProperties.fireDmgPerTick = v;
                    return;

                case FireTickRate:
                    // A zero interval means "tick every single frame", which is
                    // 50 ticks a second and not the fire-immunity anyone was
                    // reaching for. Floored.
                    unit.burnProperties.fireTickRate = Mathf.Max(0.02f, v);
                    return;

                case FireThreshold:
                    unit.burnProperties.fireThreshold = v;
                    return;

                case MaxBurn:
                    unit.burnProperties.maxBurnLevel = v;
                    return;

                case IgniteSpike:
                    unit.burnProperties.extraBurnLevelWhenCatchingFire = v;
                    return;
            }

            if (stat == IFrames)
            {
                // A unit has ONE DamagableResource per resource it can lose
                // (health, and any shield resources), each with its own
                // iFrameDuration. Writing only the first would leave the others
                // unprotected and read as "i-frames only sometimes work".
                var all = ModuleForgeUnits.Components<DamagableResource>(unit);

                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null)
                        all[i].iFrameDuration = v;

                return;
            }

            var m = ModuleForgeUnits.Component<ShipMovement>(unit);

            if (m == null)
                return;

            switch (stat)
            {
                // SPEED AND ACCELERATION ARE PAIRS, and writing only half is
                // the trap. `ShipMovement.MaxSpeed` (the property) is get-only
                // and returns maxSpeed OR maxSpeedWhileBoosted depending on
                // IsBoosted - so a module that wrote only `maxSpeed` would do
                // nothing at all while the player was sprinting, which reads as
                // "the module randomly stops working". The twin is written by
                // WriteTwin from Apply, proportionally to its own base.
                case MaxSpeed:      m.maxSpeed = v; break;
                case Acceleration:  m.acceleration = v; break;

                case DashCost:      m.dashCost = v; break;
                case DashCooldown:  m.dashCooldown = v; break;
                case DashDuration:  m.dashDuration = v; break;
                case DashForce:     m.initialDashForce = v; break;
                case FuelCost:      m.fuelCostPerSecond = v; break;
                case BoostFuelCost: m.boostedFuelCostPerSecond = v; break;
                case BoostDelay:    m.boostStartDelay = v; break;

                // FLOORED AT 1, because this is a MULTIPLIER on the speed you
                // already have and the game applies it as an outright velocity
                // write. Below 1 a sprint would BRAKE you; at 0 it would stop
                // the ship dead the instant you hit sprint. 1 is the honest
                // floor and already means "no kick at all", which is a
                // legitimate downgrade for a module to sell.
                case BoostKick:
                    m.boostExtraSpeedMultiplier = Mathf.Max(1f, v);
                    break;

                case BoostKickCap:  m.boostExtraSpeedLimit = v; break;
                case DashPush:      m.constantDashForce = v; break;
            }
        }
    }
}
