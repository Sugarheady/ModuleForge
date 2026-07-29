using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ModuleForge
{
    // Shared state for the burn-tick-rate booster.
    //
    // The game's burn damage-over-time lives on the VICTIM: an enemy's
    // DamagableResource.Update ticks fireDmgPerTick every
    // burnProperties.fireTickRate seconds (a smaller interval = faster
    // ticks). It is NOT a weapon/projectile stat, so we can't attach a
    // tick rate to a single burn. Instead a burn-rate module, while
    // equipped, raises the tick FREQUENCY on enemies additively:
    //
    //     newFrequency = baseFrequency + sum(equipped module deltas)
    //     newInterval  = 1 / min(newFrequency, MaxTicksPerSecond)
    //
    // e.g. base 1 tick/sec + two modules at +0.1 each => 1.2 ticks/sec.
    //
    // Gated to enemies: the ship(s) that carry a burn-rate module are
    // "excluded", so burn that enemies inflict on the player still ticks
    // at the game's normal rate. Clamped to MaxTicksPerSecond so stacking
    // can never run away.
    public static class ModuleForgeBurn
    {
        // The cap: burn can never tick faster than this many times/sec.
        // Set from the BepInEx config in ModuleForgePlugin. (In practice
        // the game can only tick burn once per frame, so values above the
        // frame rate just mean "every frame".)
        public static float MaxTicksPerSecond = 100f;

        // Sum of ticks/sec added by every currently-installed burn-rate
        // module (counting each equipped copy).
        public static float Delta { get; private set; }

        // True once we've actually retimed an enemy this session; lets the
        // Update patch skip all work until a booster is first used, and
        // keep running afterwards so values get restored when Delta drops.
        public static bool EverModified { get; private set; }

        // Ships that carry a burn-rate module (the player), refcounted so
        // multiple modules / co-op players compose. Excluded from the
        // speed-up. Cleared each run (see Reset) so it can't leak.
        private static readonly Dictionary<Unit.Data, int> _excluded =
            new Dictionary<Unit.Data, int>();

        // Each unit's ORIGINAL tick interval, captured before we ever
        // touch it. Weakly held, so dead units clean themselves up.
        private static readonly ConditionalWeakTable<Unit.Data, Box> _base =
            new ConditionalWeakTable<Unit.Data, Box>();

        private class Box { public float baseInterval; public bool set; }

        // Custom burn-color effects currently equipped (last one wins if
        // several). Also excludes their owner, like the tick booster, so
        // enemy-inflicted burn on the player isn't recolored either.
        private static readonly List<BurnColorEffect> _colors =
            new List<BurnColorEffect>();

        // Called from an installed tick booster. owner = the ship the
        // module is installed on (excluded from the speed-up).
        public static void AddBooster(Unit.Data owner, float amount)
        {
            Delta += amount;
            ExcludeOwner(owner);
        }

        public static void RemoveBooster(Unit.Data owner, float amount)
        {
            Delta -= amount;
            if (Delta < 0f)
                Delta = 0f;

            ReleaseOwner(owner);
        }

        // Owner exclusion is refcounted and shared by every ModuleForge
        // burn effect (tick rate AND color), so the player is excluded as
        // long as they carry any of them.
        public static void ExcludeOwner(Unit.Data owner)
        {
            if (owner == null)
                return;

            int n;
            _excluded.TryGetValue(owner, out n);
            _excluded[owner] = n + 1;
        }

        public static void ReleaseOwner(Unit.Data owner)
        {
            if (owner == null)
                return;

            int n;
            if (_excluded.TryGetValue(owner, out n))
            {
                if (n <= 1)
                    _excluded.Remove(owner);
                else
                    _excluded[owner] = n - 1;
            }
        }

        // Burn-color registration (from an installed BurnColorEffect).
        public static void AddColor(Unit.Data owner, BurnColorEffect effect)
        {
            if (effect != null && !_colors.Contains(effect))
                _colors.Add(effect);

            ExcludeOwner(owner);
        }

        public static void RemoveColor(Unit.Data owner, BurnColorEffect effect)
        {
            _colors.Remove(effect);
            ReleaseOwner(owner);
        }

        public static bool HasColor
        {
            get { return _colors.Count > 0; }
        }

        // Whether the active (most recent) color module also recolors
        // burning terrain/world fire. The color used is the same one
        // GetEmitColor returns.
        public static bool ColorTerrain
        {
            get
            {
                return _colors.Count > 0 &&
                       _colors[_colors.Count - 1].includeTerrain;
            }
        }

        // The color to tint an enemy's burn with right now (the most
        // recently equipped color module wins; rainbow cycles over time).
        public static Color GetEmitColor()
        {
            if (_colors.Count == 0)
                return Color.white;

            return _colors[_colors.Count - 1].GetEmitColor();
        }

        // Adjust just the summed delta (used when a module's level, hence
        // its per-copy value, changes while it stays installed).
        public static void AdjustDelta(float d)
        {
            Delta += d;
            if (Delta < 0f)
                Delta = 0f;
        }

        public static bool IsExcluded(Unit.Data data)
        {
            return data != null && _excluded.ContainsKey(data);
        }

        // Wipe per-run state so a booster equipped in a previous run (the
        // ship is destroyed on death WITHOUT uninstalling its modules)
        // can't carry its delta into the next run. Installs during the new
        // run's ship setup rebuild Delta via OnInstalled.
        public static void Reset()
        {
            Delta = 0f;
            EverModified = false;
            _excluded.Clear();
            _colors.Clear();
        }

        // Retime one unit's burn to the current boost (or restore it).
        // Called every frame from the DamagableResource.Update prefix.
        public static void ApplyTo(Unit.Data data)
        {
            if (data == null)
                return;

            Box box = _base.GetValue(data, Create);
            if (!box.set)
            {
                box.baseInterval = data.burnProperties.fireTickRate;
                box.set = true;
            }

            float baseInterval = box.baseInterval;
            float desired;

            if (Delta <= 0f || baseInterval <= 0f || IsExcluded(data))
            {
                // Not boosted (or the player, or an already-instant burn):
                // leave/restore the game's own interval.
                desired = baseInterval;
            }
            else
            {
                float freq = 1f / baseInterval + Delta;
                if (MaxTicksPerSecond > 0f && freq > MaxTicksPerSecond)
                    freq = MaxTicksPerSecond;
                desired = 1f / freq;
                EverModified = true;
            }

            if (data.burnProperties.fireTickRate != desired)
                data.burnProperties.fireTickRate = desired;
        }

        private static Box Create(Unit.Data key)
        {
            return new Box();
        }
    }
}
