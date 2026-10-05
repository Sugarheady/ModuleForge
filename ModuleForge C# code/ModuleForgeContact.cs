using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // CONTACT DAMAGE - a module that makes your SHIP dangerous to touch.
    //
    // ---------------------------------------------------------------------
    // WHAT THE GAME ALREADY DOES, because I had this wrong once and it reached
    // three files before he corrected it
    // ---------------------------------------------------------------------
    //
    // "Nothing in this game does contact damage" is false. The narrower truth is
    // that the PLAYER'S SHIP does not. The game has two systems:
    //
    //   Hazard       twelve prefabs - Bouncer Red, Bouncer Worm, Fly Alfa and
    //                Fly Zapper (0 damage, pure bumpers), Turret Bouncer, Fruit
    //                White, Fish, the four beak enemies and Ammo Dandelion.
    //                Collision callbacks, plus a pushback force.
    //   ChargerHead  Larva, Child, Maggot, Swimmer Maggot. No collision
    //                callback at all: a swept CircleCast along the rigidbody's
    //                velocity every FixedUpdate, rate-limited per victim.
    //
    // The ship's own `ImpactDamage` is **self** damage, and it is switched off
    // unless you are sprinting. So "my ship hurts what it rams" is genuinely
    // absent, which is what makes this module worth having.
    //
    // ---------------------------------------------------------------------
    // ★ THE SWEPT CAST IS THE DEFAULT, AND IT IS THE REASON THIS NEEDED CODE
    // ---------------------------------------------------------------------
    //
    // **All 65 unit rigidbodies use DISCRETE collision detection** - only ammo
    // prefabs use Continuous - and the fixed timestep is 0.02s. So a body moving
    // faster than about 50 units/s skips a 1-unit object between physics steps.
    // A ship under boost is exactly that fast.
    //
    // **So a collision callback silently misses the hardest hits, which is
    // precisely when a ram matters.** ChargerHead is the game's own answer and
    // copying it costs nothing. `detect: "collision"` opts back into the stock
    // Hazard behaviour for comparison.
    //
    // ---------------------------------------------------------------------
    // ★ THE NAMED RISK ON THE PORT LIST, AND WHY IT CANNOT RECUR HERE
    // ---------------------------------------------------------------------
    //
    // Weapon Forge's version shipped as a **global world rule latched at build
    // time by a file merely existing**: `Register` ran once per weapon file at
    // startup and armed every unit in every run, whatever was equipped. Reported
    // as *"I loaded Kamikaze swarm [which has no contactDamage block]. When I
    // dash into enemies they take damage now and die."* A gameplay rule decided
    // by the presence of a FILE.
    //
    // **That shape is structurally unavailable to a module, and not by luck.** A
    // module has a real lifecycle the game itself drives - installed, uninstalled,
    // powered, unpowered - so there is no build-time registration to confuse with
    // live state. Arming is keyed to `OnInstalled` + `ModuleForgeLive`, and
    // **disarming revokes** rather than merely stopping, which is the other half
    // that bug needed: those grants have no expiry, so without a revoke the rule
    // would persist on a ship whose module had been pulled.
    //
    // The one thing carried across verbatim is the DISCIPLINE: `Any` answers
    // "does the reconciler need to run", and the per-unit registry answers "is
    // THIS ship armed". Conflating those two questions in one static is what
    // shipped the original bug.
    public static class ModuleForgeContact
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Contact");

        // ---- how contact is detected ---------------------------------------
        public const int DetectSwept = 0;      // default - ChargerHead's way
        public const int DetectCollision = 1;  // the stock Hazard's way

        public static int ParseDetect(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "collision":
                case "collide":
                case "callback":
                case "hazard":
                    return DetectCollision;
                default:
                    return DetectSwept;
            }
        }

        public static string DetectWord(int d)
        {
            return d == DetectCollision
                ? "collision callbacks (the stock Hazard's way)"
                : "a swept cast (ChargerHead's way - survives high speed)";
        }

        public class Options
        {
            // ---- damage ----------------------------------------------------
            //
            // FLAT by default, matching every stock Hazard - a Maggot's beak is
            // a flat 3. Predictable, and it keeps the module readable next to
            // the speed-scaled mode below.
            public float damage = 3f;
            public Resource damageType;

            // Damage from how fast you were going instead. `minSpeed` is a
            // floor you must beat to hurt anything at all, which is what turns
            // this from "touching things hurts them" into "ramming does".
            public bool scaleBySpeed;
            public float damagePerSpeed = 1f;
            public float minSpeed = 6f;
            public float maxDamage = 999f;

            // ---- shoving ---------------------------------------------------
            public float push;      // the victim, along the direction of travel
            public float recoil;    // and your ship, backwards

            // Per target, so ploughing through a crowd hits each of them once
            // rather than the first one repeatedly. The stock ChargerHeads use
            // 0.3 and 0.5.
            public float repeatDelay = 0.3f;

            public int detect = DetectSwept;

            // 0 = taken from the ship's own collider, which is almost always
            // right and means the number does not have to be looked up.
            public float radius;

            // ---- when it is armed ------------------------------------------
            //
            // OFF by default, so the module is a passive hull spike. On, it is
            // only dangerous while you are BOOSTING - which is the more
            // interesting design and is the game's own precedent: the ship's
            // `ImpactDamage` is enabled by `SetBoosted` and disabled the rest of
            // the time. A dash costs fuel, so a boost-gated ram is a decision
            // rather than a passive.
            public bool whileBoosting;

            // ---- surviving your own ram ------------------------------------
            //
            // The ship's `ImpactDamage` hurts YOU on a hard impact and is live
            // exactly while boosting. So a module that rewards ramming, used the
            // way it wants to be used, hurts its owner - and adding
            // invincibility would be the wrong fix because that component is the
            // only thing it needs to stop.
            //
            // Off by default: taking the crash damage is the honest default and
            // the player may want the trade. On, the component is disabled while
            // the module is live and **restored on the way out** - a world
            // change that is not restored is the shape that makes a module look
            // like it has no off switch.
            public bool ignoreSelfDamage;

            // ---- the tell ---------------------------------------------------
            public Color tint = Color.white;
            public bool hasTint;
            public float tintPulse;
            public float tintStrength = 1f;
            public int tintMode = ModuleForgeUnitTint.Multiply;

            public bool DoesAnything
            {
                get
                {
                    return damage > 0f || push != 0f || recoil != 0f ||
                           scaleBySpeed;
                }
            }
        }

        // ------------------------------------------------------------------
        // WHICH SHIPS ARE ARMED
        // ------------------------------------------------------------------
        //
        // Keyed by `Unit.Data` rather than by a live `Unit`, because that is
        // what every module callback is handed - `OnInstalled`, `OnUninstalled`
        // and the powered gate all take save data with no transform and no
        // components. The live object is resolved separately, and may not exist
        // yet at the moment a module is installed; see the reconciler.
        //
        // Keyed by the CONTRIBUTOR inside each entry so two contact modules on
        // one ship do not fight, and so a push is idempotent.
        private static readonly Dictionary<Unit.Data, Dictionary<object, Options>>
            _armed = new Dictionary<Unit.Data, Dictionary<object, Options>>();

        public static bool Any { get; private set; }

        private static void Refresh()
        {
            Any = _armed.Count > 0;

            if (Any)
                Reconciler.Ensure();
        }

        public static void Arm(Unit.Data owner, object key, Options options)
        {
            if (owner == null || key == null || options == null)
                return;

            Dictionary<object, Options> byKey;

            if (!_armed.TryGetValue(owner, out byKey))
            {
                byKey = new Dictionary<object, Options>();
                _armed[owner] = byKey;
            }

            byKey[key] = options;
            Refresh();
        }

        // DISARMING REVOKES rather than merely stopping, and that is the half
        // the original bug needed as much as the scoping. A grant here has no
        // expiry, so a controller left running on a ship whose module was pulled
        // is a module with no off switch.
        public static void Disarm(object key)
        {
            if (key == null)
                return;

            List<Unit.Data> emptied = null;

            foreach (var pair in _armed)
            {
                if (!pair.Value.Remove(key))
                    continue;

                if (pair.Value.Count == 0)
                {
                    if (emptied == null)
                        emptied = new List<Unit.Data>();

                    emptied.Add(pair.Key);
                }
            }

            if (emptied != null)
            {
                for (int i = 0; i < emptied.Count; i++)
                {
                    Revoke(emptied[i]);
                    _armed.Remove(emptied[i]);
                }
            }

            Refresh();
        }

        private static void Revoke(Unit.Data owner)
        {
            try
            {
                Unit live = ModuleForgeUnits.Find(owner);

                if (live == null)
                    return;

                var c = live.GetComponent<ModuleForgeContactController>();

                if (c != null)
                    UnityEngine.Object.Destroy(c);
            }
            catch (Exception e)
            {
                Log.LogError("Revoking contact damage failed: " + e);
            }
        }

        // THE STRONGEST DAMAGE WINS when two contact modules sit on one ship.
        //
        // Not summed, deliberately: contact damage is applied per victim per
        // `repeatDelay`, so summing two modules would also mean choosing whose
        // delay, whose push, whose radius and whose tint - a resolution pass for
        // a case nobody asked for. One set of numbers applies and the log says
        // so, which is the same call `grow` makes for the same reason.
        public static Options Resolve(Unit.Data owner)
        {
            Dictionary<object, Options> byKey;

            if (owner == null || !_armed.TryGetValue(owner, out byKey))
                return null;

            Options best = null;

            foreach (var pair in byKey)
            {
                if (pair.Value == null)
                    continue;

                if (best == null || pair.Value.damage > best.damage)
                    best = pair.Value;
            }

            return best;
        }

        public static int CountFor(Unit.Data owner)
        {
            Dictionary<object, Options> byKey;

            return (owner != null && _armed.TryGetValue(owner, out byKey))
                ? byKey.Count : 0;
        }

        // ------------------------------------------------------------------
        // THE RECONCILER - why this exists rather than attaching on install
        // ------------------------------------------------------------------
        //
        // A module is installed against a `Unit.Data`. The live `Unit` it
        // belongs to may not be bound yet at that moment (the restore path
        // recalculates stats early, before the objects exist), so resolving it
        // once at install would miss and the module would silently do nothing on
        // a loaded save while working perfectly on a fresh one - an intermittent
        // failure keyed to how the run started, which is the worst kind to
        // diagnose.
        //
        // So the state is declarative - "these Unit.Datas are armed" - and one
        // cheap tick reconciles it against what actually exists. It self-heals
        // whenever the object turns up.
        private class Reconciler : MonoBehaviour
        {
            private static Reconciler _instance;
            private float _next;

            public static void Ensure()
            {
                if (_instance != null)
                    return;

                var go = new GameObject("ModuleForge Contact Reconciler");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<Reconciler>();
            }

            private void Update()
            {
                // Four times a second, not every frame. Nothing here changes at
                // frame rate: a module is installed or it is not, and a Unit is
                // bound or it is not. The sweeping itself lives on the
                // controller's own FixedUpdate, which is where the timing
                // actually matters.
                if (Time.time < _next)
                    return;

                _next = Time.time + 0.25f;

                if (!Any)
                    return;

                try
                {
                    foreach (var pair in _armed)
                    {
                        Unit live = ModuleForgeUnits.Find(pair.Key);

                        if (live == null)
                            continue;

                        Options o = Resolve(pair.Key);

                        if (o == null)
                            continue;

                        var c = live.GetComponent<ModuleForgeContactController>();

                        if (c == null)
                            c = live.gameObject
                                .AddComponent<ModuleForgeContactController>();

                        // Re-handed every tick so a level change or a swapped
                        // module reaches a controller that already exists,
                        // rather than only a freshly created one.
                        c.Configure(live, o);
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("Contact reconcile failed: " + e);
                }
            }
        }

        // Per-RUN state. The controllers themselves are MonoBehaviours and die
        // with the scene for free; this table holds `Unit.Data` keys from
        // modules that are NOT uninstalled on teardown, so without the sweep a
        // run's worth would carry into the next one.
        public static void Reset()
        {
            _armed.Clear();
            Refresh();
        }
    }
}
