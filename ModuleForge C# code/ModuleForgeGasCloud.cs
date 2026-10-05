using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // One cloud of gas, left on a corpse by a kill-reward module.
    //
    // A deliberate port of Weapon Forge's `ForgeGasCloud` - see the header on
    // `ModuleForgeGas` for why this duplicates rather than bridges, and for the
    // list of weapon-side fields that are absent by design.
    //
    // EVERY NON-OBVIOUS THING IN HERE WAS PAID FOR ONCE ALREADY, on the other
    // side, and is carried across deliberately rather than rediscovered:
    //
    //   - the per-target CLAIM, so overlapping clouds do not multiply damage
    //   - the claim table SWEEP, because a destroyed Unity object is still a
    //     perfectly good dictionary key
    //   - retiring the oldest cloud by FADING it rather than destroying it
    //   - `cfg` being read in `Start` and not `Awake`
    //   - the generated puff sprite, because atlas neighbours bleed
    //
    // [copying a feature copies its bugs] cuts the other way here: copy from
    // the FIXED version, which is what this is - the R15 overlap fix and the
    // R18 chain default are both already in it.
    public class ModuleForgeGasCloud : MonoBehaviour
    {
        private ModuleForgeGas.Config cfg;
        private Unit owner;
        private float bornAt;
        private float endsAt;
        private bool retiring;
        private float retireWindow;
        private float nextTick;
        private Vector2 velocity;
        private float seed;

        private readonly List<Transform> puffs = new List<Transform>();
        private readonly List<SpriteRenderer> puffArt = new List<SpriteRenderer>();

        private static readonly List<ModuleForgeGasCloud> _live =
            new List<ModuleForgeGasCloud>();

        private static readonly Collider2D[] _buffer = new Collider2D[64];
        private static ContactFilter2D _hitFilter;
        private static bool _filterReady;

        // One soft radial sprite shared by every cloud - the SpriteRenderer
        // tints it per cloud, so colour costs nothing.
        private static Sprite _puffArt;

        // Clouds already on their way out do not count against the budget, so a
        // new one can take a retiring one's place immediately rather than
        // waiting for the fade to finish.
        public static int CountFor(ModuleForgeGas.Config c)
        {
            int n = 0;

            for (int i = 0; i < _live.Count; i++)
                if (_live[i] != null && _live[i].cfg == c && !_live[i].retiring)
                    n++;

            return n;
        }

        // Retire the oldest cloud belonging to this module, so a module that
        // keeps scoring kills rolls its budget forward instead of refusing to
        // make more. It is asked to FADE rather than destroyed outright: a
        // cloud vanishing mid-frame in front of the player reads as a bug
        // rather than as a budget.
        public static void TrimOldest(ModuleForgeGas.Config c)
        {
            ModuleForgeGasCloud oldest = null;

            for (int i = 0; i < _live.Count; i++)
            {
                ModuleForgeGasCloud g = _live[i];

                if (g == null || g.cfg != c || g.retiring)
                    continue;

                if (oldest == null || g.bornAt < oldest.bornAt)
                    oldest = g;
            }

            if (oldest != null)
                oldest.Retire();
        }

        public static ModuleForgeGasCloud Spawn(
            ModuleForgeGas.Config c, Vector2 where, Vector2 inherited,
            Unit owner)
        {
            if (c == null)
                return null;

            if (c.max > 0 && CountFor(c) >= c.max)
                TrimOldest(c);

            var go = new GameObject("ModuleForge Gas");
            go.transform.position = where;

            var cloud = go.AddComponent<ModuleForgeGasCloud>();
            cloud.cfg = c;
            cloud.owner = owner;
            cloud.velocity = c.drifts
                ? inherited.normalized * c.drift
                : Vector2.zero;

            return cloud;
        }

        private void Awake()
        {
            _live.Add(this);
            bornAt = Time.time;

            // Varies the churn per cloud without Random, so two clouds born in
            // the same frame do not move as one.
            seed = (GetInstanceID() & 1023) * 0.0141f;
        }

        private void OnDestroy()
        {
            _live.Remove(this);
        }

        private void Start()
        {
            // NOT in Awake: `cfg` is assigned by the caller straight after
            // AddComponent, which is AFTER Awake has already run. Reading it
            // there gives a null every time.
            endsAt = bornAt + cfg.duration;

            EnsureFilter();
            BuildArt();

            if (!string.IsNullOrEmpty(cfg.sfxGuid))
                AudioManager.PlaySfx(cfg.sfxGuid, transform.position);
        }

        private static void EnsureFilter()
        {
            if (_filterReady)
                return;

            _filterReady = true;
            _hitFilter = new ContactFilter2D();
            _hitFilter.useTriggers = true;
            _hitFilter.SetLayerMask(
                LayerMask.GetMask("Entities", "Fruits", "Player"));
        }

        private float Age { get { return Time.time - bornAt; } }

        // Cut this cloud's life short and let it fade, rather than vanishing.
        // Idempotent - being asked twice does not restart the fade.
        public void Retire()
        {
            if (retiring)
                return;

            retiring = true;

            // Long enough to read as thinning out, short enough that the budget
            // genuinely frees up. Never longer than the cloud had left anyway.
            retireWindow = Mathf.Max(0.15f, Mathf.Min(cfg.fade, 0.35f));
            endsAt = Mathf.Min(endsAt, Time.time + retireWindow);
        }

        private float Radius
        {
            get
            {
                if (!cfg.grows)
                    return cfg.radius;

                float t = (cfg.growTime <= 0f)
                    ? 1f
                    : Mathf.Clamp01(Age / cfg.growTime);

                return Mathf.Lerp(cfg.radius, cfg.growTo, t);
            }
        }

        private float Alpha
        {
            get
            {
                float left = endsAt - Time.time;

                // A short fade IN as well, so a cloud blooms rather than pops.
                float a = Mathf.Clamp01(Age / 0.25f);

                if (cfg.fade > 0f && left < cfg.fade)
                    a = Mathf.Min(a, Mathf.Clamp01(left / cfg.fade));

                // A cloud cut short by the budget fades over its own shorter
                // window, so it thins out from wherever it had got to.
                if (retiring && retireWindow > 0f)
                    a = Mathf.Min(a, Mathf.Clamp01(left / retireWindow));

                return a;
            }
        }

        private void Update()
        {
            if (cfg == null || Time.time >= endsAt)
            {
                Destroy(gameObject);
                return;
            }

            if (cfg.drifts)
            {
                transform.position += (Vector3)(velocity * Time.deltaTime);

                // Settles rather than sailing away forever.
                velocity *= Mathf.Max(
                    0f, 1f - cfg.driftDamping * Time.deltaTime);
            }

            DrawPuffs();
            TickArt();
        }

        private void FixedUpdate()
        {
            if (cfg == null || !cfg.DoesAnything)
                return;

            if (Time.time < nextTick)
                return;

            float dt = Mathf.Max(0.02f, cfg.tickRate);
            nextTick = Time.time + dt;

            Affect(dt);
            Douse(dt);
        }

        // ------------------------------------------------------------------
        // ONE TICK PER TARGET PER MODULE, however many clouds cover it
        // ------------------------------------------------------------------
        //
        // Carried from the weapon side, where it was a reported bug rather than
        // a precaution. `damagePerSecond` did not mean damage per second: every
        // cloud is its own component with its own `nextTick`, so a target
        // standing where six clouds overlap took SIX ticks of
        // `damagePerSecond * dt` every `tickRate` - six times the damage asked
        // for, and six separate damage events each firing the victim's own hit
        // sound.
        //
        // That is MORE likely here, not less. A kill-reward cloud lands on a
        // corpse, and corpses in a group fight are close together - so the
        // overlap this guards against is the ordinary case for this feature
        // rather than a corner of it.
        //
        // Keyed by the CONFIG as well as the target, so two different gas
        // modules still stack with each other while one module's own
        // overlapping clouds do not. (The weapon side keys on the WeaponBase
        // for the same reason; a module's config is the equivalent identity,
        // and a ship-grid module has no weapon to key on at all.)
        private struct Claim
        {
            public ModuleForgeGas.Config cfg;
            public float until;
        }

        private static readonly Dictionary<HealthBase, Claim> _claims =
            new Dictionary<HealthBase, Claim>();

        private static float _nextSweep;

        private bool ClaimTick(HealthBase hb, float dt)
        {
            if (hb == null)
                return false;

            Sweep();

            Claim had;

            if (_claims.TryGetValue(hb, out had) &&
                ReferenceEquals(had.cfg, cfg) &&
                Time.time < had.until)
            {
                return false;
            }

            _claims[hb] = new Claim { cfg = cfg, until = Time.time + dt };

            return true;
        }

        // A destroyed Unity object compares equal to null but is still a
        // perfectly good dictionary key, so entries have to be swept or the
        // table grows for the whole run. Same trap the pickup cache had.
        private static void Sweep()
        {
            if (Time.time < _nextSweep)
                return;

            _nextSweep = Time.time + 5f;

            List<HealthBase> dead = null;

            foreach (var pair in _claims)
            {
                if (pair.Key == null || Time.time > pair.Value.until + 5f)
                {
                    if (dead == null)
                        dead = new List<HealthBase>();

                    dead.Add(pair.Key);
                }
            }

            if (dead == null)
                return;

            for (int i = 0; i < dead.Count; i++)
                _claims.Remove(dead[i]);
        }

        // TRUE only while a cloud is crediting a kill of its own. Read by the
        // kill-reward effect so a cloud's kill can be told from any other kill -
        // it is the only thing standing between "a cloud on the corpse" and a
        // self-feeding chain, and it is what `gas.chains` switches.
        public static bool Crediting;

        private void Affect(float dt)
        {
            float r = Radius;
            Vector2 here = transform.position;

            int n = Physics2D.OverlapCircle(here, r, _hitFilter, _buffer);

            for (int i = 0; i < n; i++)
            {
                Collider2D col = _buffer[i];

                if (col == null)
                    continue;

                var hb = col.GetComponentInParent<HealthBase>();

                if (hb == null)
                    continue;

                var unit = hb.GetComponent<Unit>();
                bool isOwner = unit != null && owner != null && unit == owner;

                // Whose gas this is decides who it is allowed to touch.
                if (isOwner && !cfg.hitPlayer)
                {
                    // Still allowed to put the owner's own fire out - that is
                    // the whole point of an extinguisher gas.
                    Extinguish(unit, dt);
                    continue;
                }

                if (!isOwner && !cfg.hitEnemies)
                    continue;

                // Damage is a plain {damageType, amount} struct. The type is
                // skipped entirely when there is none, because the resource
                // comparisons downstream have no null branch.
                //
                // Read off THIS CLOUD, not off the config: an unnamed type
                // inherits from whichever gun scored the kill that made this
                // cloud, and two clouds from one module can therefore carry
                // different elements. Writing it back onto the shared config
                // would latch the first one forever.
                if (cfg.damagePerSecond > 0f && _damageType != null &&
                    ClaimTick(hb, dt))
                {
                    Damage dmg = new Damage(
                        cfg.damagePerSecond * dt, _damageType);

                    if (dmg.amount > 0f)
                    {
                        // NO CRIT OPT-IN HERE, and its absence is deliberate.
                        // The weapon side wraps this in ForgeCrit.Begin/End so
                        // a cloud's damage can crit - but that is gated on the
                        // weapon file saying `crit: { "onExtras": true }`, and
                        // a module has no weapon file to say it in. Adding a
                        // module-side switch would be a new feature rather than
                        // a port, so it is left out and said out loud instead
                        // of half-built.
                        hb.TakeDamage(dmg);

                        if (JustDied(hb))
                        {
                            // Flagged ACROSS the credit so the kill-reward
                            // effect can tell a kill scored BY a cloud from any
                            // other kill. Restored rather than cleared, because
                            // one cloud's kill can reach this line while
                            // another's is still on the stack.
                            bool prevCrediting = Crediting;
                            Crediting = true;

                            try
                            {
                                // The game credits nothing that is not a
                                // projectile, beam or explosion, and a cloud is
                                // none of those - so without this the kill is
                                // invisible to every on-kill reward, including
                                // this module's own.
                                ModuleForgeKills.CreditKill(
                                    owner, unit, _weapon);
                            }
                            finally
                            {
                                Crediting = prevCrediting;
                            }
                        }
                    }
                }

                if (unit != null)
                {
                    if (cfg.burnPerSecond > 0f)
                        unit.ComponentData.BurnLevel += cfg.burnPerSecond * dt;

                    Extinguish(unit, dt);
                }

                Rigidbody2D rb = col.attachedRigidbody;

                if (rb == null)
                    continue;

                if (cfg.slow > 0f)
                {
                    float keep = Mathf.Clamp01(1f - cfg.slow * dt);
                    rb.linearVelocity *= keep;
                }

                if (cfg.push != 0f)
                {
                    Vector2 away = (Vector2)col.transform.position - here;

                    if (away.sqrMagnitude > 0.0001f)
                        rb.AddForce(away.normalized * cfg.push * dt,
                                    ForceMode2D.Impulse);
                }
            }
        }

        private static bool JustDied(HealthBase hb)
        {
            var dr = hb as DamagableResource;
            return dr != null && dr.IsDead;
        }

        private void Extinguish(Unit unit, float dt)
        {
            if (cfg.extinguish <= 0f || unit == null)
                return;

            // The setter clamps at 0 itself, and only ADDS the catching-fire
            // bonus when crossing upward - so pulling it down is safe.
            unit.ComponentData.BurnLevel -= cfg.extinguish * dt;
        }

        [System.NonSerialized] private WeaponBase _weapon;
        [System.NonSerialized] private Resource _damageType;

        // Carried so a kill by this cloud is credited to the gun that scored
        // the kill that made it. Null on the ship grid, which every reader here
        // treats as "whichever gun" - the same meaning it has throughout
        // KillRewardEffect.
        public void SetWeapon(WeaponBase w)
        {
            _weapon = w;
        }

        // PER CLOUD, never folded back into the config. The config's own
        // `damageResource` means "the module file named an element"; null there
        // means "inherit from whoever scored the kill", which is resolved once
        // per cloud in KillRewardEffect.LeaveGas and handed here.
        public void SetDamageType(Resource r)
        {
            _damageType = r;
        }

        // ------------------------------------------------------------------
        // Terrain fire - the OTHER burn system
        // ------------------------------------------------------------------
        //
        // Per-cell burn levels, simulated and spread by the game's own job.
        // Nothing else in this mod touches it.
        private void Douse(float dt)
        {
            if (cfg.extinguishTerrain <= 0f && cfg.igniteTerrain <= 0f)
                return;

            Level level;

            if (!ServiceLocator.TryGet<Level>(out level) || level == null)
                return;

            float r = Radius;
            Vector2 here = transform.position;

            int minX = Mathf.FloorToInt(here.x - r);
            int maxX = Mathf.CeilToInt(here.x + r);
            int minY = Mathf.FloorToInt(here.y - r);
            int maxY = Mathf.CeilToInt(here.y + r);

            float drop = cfg.extinguishTerrain * dt;
            float raise = cfg.igniteTerrain * dt;
            float sqr = r * r;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (!level.ContainsCell(x, y))
                        continue;

                    float dx = x - here.x;
                    float dy = y - here.y;

                    if (dx * dx + dy * dy > sqr)
                        continue;

                    float burn = level.GetBurnLevel(x, y);

                    if (raise > 0f && !level.IsEmpty(x, y))
                    {
                        // Setting the ground alight from the air. The game only
                        // ever does this on a projectile COLLISION, so a cloud
                        // has no other route to it.
                        level.SetBurnLevel(x, y, burn + raise);
                        continue;
                    }

                    if (drop <= 0f || burn <= 0f)
                        continue;

                    // Clamped at zero deliberately - and note SetBurnLevel
                    // REMOVES the cell's entry at 0 rather than storing a zero,
                    // which is what fully puts it out.
                    level.SetBurnLevel(x, y, Mathf.Max(0f, burn - drop));
                }
            }
        }

        // ------------------------------------------------------------------
        // Art
        // ------------------------------------------------------------------

        private void BuildArt()
        {
            int count = Mathf.Clamp(cfg.puffs, 1, 24);

            Sprite art =
                (cfg.artFrames != null && cfg.artFrames.Length > 0)
                    ? cfg.artFrames[0]
                    : SharedPuff();

            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("Puff");
                go.transform.SetParent(transform, false);

                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = art;
                sr.color = cfg.color;

                puffs.Add(go.transform);
                puffArt.Add(sr);

                // Out of phase on purpose: up to 24 puffs flipping in lock-step
                // reads as one strobing blob, while out of phase they read as a
                // churning cloud. The weapon side passes `randomStart: true`
                // into VisualCustomizer for exactly this - that class is Weapon
                // Forge's, so the phase is done here by hand instead.
                _frameOffset.Add(i * 0.37f);
            }
        }

        private readonly List<float> _frameOffset = new List<float>();

        // A minimal flipbook, and only when the sprite NAME opted in.
        //
        // Weapon Forge animates these through VisualCustomizer.AnimateRenderer,
        // which does not exist here - and borrowing it would mean a reflection
        // bridge for a display detail, which is the same call the buff HUD and
        // the unit tint already made the other way. Twenty lines beats a
        // dependency.
        private void TickArt()
        {
            if (!cfg.animates || cfg.artFrames == null ||
                cfg.artFrames.Length < 2 || cfg.artFps <= 0f)
            {
                return;
            }

            int n = puffArt.Count;
            int frames = cfg.artFrames.Length;

            for (int i = 0; i < n; i++)
            {
                SpriteRenderer sr = puffArt[i];

                if (sr == null)
                    continue;

                float off = (i < _frameOffset.Count) ? _frameOffset[i] : 0f;
                int f = Mathf.FloorToInt((Age + off) * cfg.artFps) % frames;

                if (f < 0)
                    f += frames;

                sr.sprite = cfg.artFrames[f];
            }
        }

        private void DrawPuffs()
        {
            float r = Radius;
            float a = Alpha;
            int n = puffs.Count;

            for (int i = 0; i < n; i++)
            {
                Transform t = puffs[i];

                if (t == null)
                    continue;

                // Puffs sit on a ring inside the cloud and rotate slowly, which
                // is what stops a cloud reading as one flat disc.
                float phase = (i / (float)n) * Mathf.PI * 2f + seed;
                float spin = (Age * cfg.swirl) * Mathf.Deg2Rad;
                float ang = phase + spin;

                // Alternate rings so the middle is not hollow.
                float ringR = (i % 2 == 0) ? r * 0.45f : r * 0.75f;

                t.localPosition = new Vector3(
                    Mathf.Cos(ang) * ringR, Mathf.Sin(ang) * ringR, 0f);

                // Each puff covers rather more than its share of the radius so
                // the ring reads as one mass rather than beads on a string.
                float size = r * 1.05f;
                t.localScale = new Vector3(size, size, 1f);

                SpriteRenderer sr = puffArt[i];

                if (sr == null)
                    continue;

                Color c = cfg.color;
                c.a *= a;
                sr.color = c;
            }
        }

        // A soft radial blob, built once. GENERATED rather than borrowed
        // because every stock particle sprite is packed tight in the shared
        // 1024x2048 atlas and its neighbours bleed in when it is scaled up.
        private static Sprite SharedPuff()
        {
            if (_puffArt != null)
                return _puffArt;

            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            var px = new Color[S * S];

            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    float dx = (x + 0.5f) / S * 2f - 1f;
                    float dy = (y + 0.5f) / S * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    // Smooth falloff to nothing at the rim, squared so the
                    // centre stays dense and the edge is genuinely wispy.
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a;

                    px[y * S + x] = new Color(1f, 1f, 1f, a);
                }
            }

            tex.SetPixels(px);
            tex.Apply();

            // 1 world unit across at scale 1, so localScale IS the diameter.
            _puffArt = Sprite.Create(
                tex, new Rect(0f, 0f, S, S), new Vector2(0.5f, 0.5f),
                S, 0, SpriteMeshType.FullRect);

            _puffArt.name = "ModuleForge Gas Puff";
            return _puffArt;
        }

        // Called from BurnResetPatch.ResetAll. The live list is per-RUN state:
        // clouds are MonoBehaviours and die with the scene for free, but the
        // CLAIM table is a plain static keyed by objects that are gone, and
        // `Crediting` is a latch that a teardown mid-credit could leave true
        // for the rest of the session.
        //
        // The configs themselves are NOT touched - those are registered once at
        // build time from the module files, and clearing them would unbuild
        // every gas module for the rest of the session. Same line ForgeCrit
        // draws around `_weapons`.
        public static void Reset()
        {
            _live.Clear();
            _claims.Clear();
            _nextSweep = 0f;
            Crediting = false;
        }
    }
}
