using System.Collections.Generic;

namespace ModuleForge
{
    // Owner-keyed registries for the projectile-modifying modules
    // (phasing + capped piercing). A module registers the ship it's
    // installed on; ModuleForgeProjectilePatch then applies the effect to
    // that ship's projectiles at spawn. Mirrors ModuleForgeBurn's
    // owner-refcount + per-run reset approach (modules aren't uninstalled
    // on death, so state is reset at each run entry).
    public static class ModuleForgeProjectile
    {
        // ---- Phasing (pass through terrain) ----
        private static readonly Dictionary<Unit.Data, int> _phasing =
            new Dictionary<Unit.Data, int>();

        public static void AddPhasing(Unit.Data owner)
        {
            if (owner == null) return;
            int n;
            _phasing.TryGetValue(owner, out n);
            _phasing[owner] = n + 1;
        }

        public static void RemovePhasing(Unit.Data owner)
        {
            if (owner == null) return;
            int n;
            if (_phasing.TryGetValue(owner, out n))
            {
                if (n <= 1) _phasing.Remove(owner);
                else _phasing[owner] = n - 1;
            }
        }

        public static bool IsPhasing(Unit.Data owner)
        {
            return owner != null && _phasing.ContainsKey(owner);
        }

        // ---- Capped piercing ----
        //
        // The cap is a FLOAT, not an int, so a module's level can buy a
        // FRACTION of a pierce: 1.5 means "one pierce always, a second one
        // half the time". The roll happens once per projectile at Shoot (see
        // ResolveCap), so a shot either has the extra pierce for its whole
        // flight or it doesn't - rolling per hit would make the same bullet
        // behave differently against each enemy, which reads as a bug.
        private class PierceInfo
        {
            public float cap;     // summed across equipped pierce modules
            public float falloff; // max across modules
            public bool explode;  // any module wants it
            public int refs;
        }

        private static readonly Dictionary<Unit.Data, PierceInfo> _pierce =
            new Dictionary<Unit.Data, PierceInfo>();

        public static void AddPierce(
            Unit.Data owner, float cap, float falloff, bool explode)
        {
            if (owner == null) return;

            PierceInfo info;
            if (_pierce.TryGetValue(owner, out info))
            {
                info.cap += cap;
                info.refs++;
                if (falloff > info.falloff) info.falloff = falloff;
                info.explode = info.explode || explode;
            }
            else
            {
                _pierce[owner] = new PierceInfo
                {
                    cap = cap, falloff = falloff, explode = explode, refs = 1
                };
            }
        }

        public static void RemovePierce(Unit.Data owner, float cap)
        {
            if (owner == null) return;

            PierceInfo info;
            if (_pierce.TryGetValue(owner, out info))
            {
                info.cap -= cap;
                info.refs--;
                if (info.refs <= 0)
                    _pierce.Remove(owner);
            }
        }

        // Re-sync one module's contribution after its LEVEL changed while it
        // stayed installed (a BoosterCore placed beside it). Only the cap can
        // move; falloff is a max across modules and cannot be un-maxed
        // without re-polling every module, so it is left alone.
        public static void AdjustPierce(Unit.Data owner, float delta)
        {
            if (owner == null || delta == 0f) return;

            PierceInfo info;
            if (_pierce.TryGetValue(owner, out info))
            {
                info.cap += delta;
                if (info.cap < 0f) info.cap = 0f;
            }
        }

        public static bool TryGetPierce(
            Unit.Data owner, out float cap, out float falloff, out bool explode)
        {
            cap = 0f; falloff = 0f; explode = false;

            PierceInfo info;
            if (owner != null && _pierce.TryGetValue(owner, out info))
            {
                cap = info.cap;
                falloff = info.falloff;
                explode = info.explode;
                return true;
            }
            return false;
        }

        // Turn a fractional cap into the whole number THIS projectile gets:
        // the guaranteed part, plus the fraction as a chance of one more.
        // 1.5 -> 1 or 2, evenly. 2.0 -> always 2.
        public static int ResolveCap(float cap)
        {
            if (cap <= 0f)
                return 0;

            int whole = (int)cap;
            float fraction = cap - whole;

            if (fraction > 0f && UnityEngine.Random.value < fraction)
                whole++;

            return whole;
        }

        // ---- Aggregates for the weapon stat card ----
        // Only the player installs these modules, so a global view == the
        // player's total (same assumption ModuleForgeBurn.Delta relies on).
        public static bool AnyPhasing
        {
            get { return _phasing.Count > 0; }
        }

        // Float, so the stat card can honestly say "1.5" rather than
        // rounding away the fractional pierce the player paid for.
        public static float PierceCapTotal
        {
            get
            {
                float total = 0f;
                foreach (var kv in _pierce)
                    total += kv.Value.cap;
                return total;
            }
        }

        public static void Reset()
        {
            _phasing.Clear();
            _pierce.Clear();
        }
    }
}
