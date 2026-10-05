using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // Rides the player's ship and makes it dangerous to touch. See
    // ModuleForgeContact for why the sweep is the default and why this cannot
    // become the global world rule its Weapon Forge twin once was.
    public class ModuleForgeContactController : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Contact");

        private ModuleForgeContact.Options cfg;
        private Unit self;
        private Rigidbody2D body;
        private float radius;
        private int mask;
        private bool ready;

        // The ship's own self-damage component, and what it was before we
        // touched it. A world change that is not restored is a module that
        // looks like it has no off switch.
        private Behaviour impactDamage;
        private bool impactWasEnabled;
        private bool impactSuppressed;

        private readonly Dictionary<HealthBase, float> lastHit =
            new Dictionary<HealthBase, float>();

        private static readonly RaycastHit2D[] _hits = new RaycastHit2D[24];
        private static ContactFilter2D _filter;

        // ONE-SHOT gates, named `_said*` so ModuleForgeDiagnosticGates sweeps
        // them at each run entry - what each describes is per RUN, so without
        // the sweep the run where you go looking for the line is exactly the one
        // that cannot produce it.
        private static bool _saidRadius;
        private static bool _saidNoType;
        private static bool _saidNoImpactComponent;

        public void Configure(Unit unit, ModuleForgeContact.Options options)
        {
            self = unit;

            // Re-derive only when the numbers that shape the sweep change.
            // `Configure` is called four times a second by the reconciler, and
            // Setup walks the hierarchy for a collider.
            bool reshape =
                cfg == null ||
                cfg.radius != options.radius;

            cfg = options;

            if (reshape)
                ready = false;

            ApplySelfDamagePolicy();
            ApplyTint();
        }

        // HELD, not one-shot. The tint owner expires a layer after its duration,
        // so a permanent tell has to be re-set - and re-setting the same key
        // with the same colour is idempotent by construction, because the table
        // keys by layer rather than accumulating.
        //
        // A duration slightly longer than the reconcile interval, so it never
        // flickers off between ticks but does clear on its own if this
        // controller dies without an OnDestroy ever running.
        private void ApplyTint()
        {
            if (cfg == null || !cfg.hasTint || self == null)
                return;

            try
            {
                ModuleForgeUnitTint.Set(
                    self, ModuleForgeUnitTint.Contact, cfg.tint,
                    0.6f, cfg.tintPulse, cfg.tintStrength, cfg.tintMode);
            }
            catch (Exception e)
            {
                // A tell must never cost the damage it is announcing.
                Log.LogError("Contact tint failed: " + e);
            }
        }

        // ------------------------------------------------------------------
        // Surviving your own ram
        // ------------------------------------------------------------------
        //
        // `ImpactDamage` on the ship reads the closing speed along the contact
        // normal and damages **its own unit** - it is crash damage, not ram
        // damage - and `ShipMovement.SetBoosted` enables it exactly while you
        // are boosting. So a module that rewards ramming, used the way it wants
        // to be used, hurts its owner.
        //
        // The right fix is to suppress that component rather than to add
        // invulnerability, which would also block everything else. Restored on
        // the way out, and the ORIGINAL value is what is restored rather than
        // `true` - the game toggles this field itself every time you start or
        // stop boosting, so writing back a guess would fight it.
        private void ApplySelfDamagePolicy()
        {
            try
            {
                if (cfg == null || !cfg.ignoreSelfDamage)
                {
                    RestoreSelfDamage();
                    return;
                }

                if (impactDamage == null)
                {
                    impactDamage =
                        GetComponentInChildren<ImpactDamage>(true) as Behaviour;

                    if (impactDamage == null)
                    {
                        if (!_saidNoImpactComponent)
                        {
                            _saidNoImpactComponent = true;

                            Log.LogWarning(
                                "contactDamage \"ignoreSelfDamage\" found no " +
                                "ImpactDamage component on this ship, so there " +
                                "is nothing to suppress. That is not a fault - " +
                                "only the four ship prefabs and the suicide " +
                                "drone carry one - but if you expected crash " +
                                "damage to stop, it was already off. Said once " +
                                "per run.");
                        }

                        return;
                    }

                    impactWasEnabled = impactDamage.enabled;
                }

                if (!impactSuppressed)
                {
                    impactSuppressed = true;
                    impactWasEnabled = impactDamage.enabled;
                }

                // Held down every tick rather than set once, because
                // `SetBoosted` turns it back on whenever you start boosting -
                // which is exactly when a ram happens. Setting it once would
                // work in the hangar and fail in the only situation it is for.
                if (impactDamage.enabled)
                    impactDamage.enabled = false;
            }
            catch (Exception e)
            {
                Log.LogError("Contact self-damage policy failed: " + e);
            }
        }

        private void RestoreSelfDamage()
        {
            if (!impactSuppressed || impactDamage == null)
                return;

            try
            {
                impactDamage.enabled = impactWasEnabled;
            }
            catch (Exception)
            {
                // A destroyed component is a perfectly ordinary way for this to
                // end; it is not worth a log line every time a run does.
            }

            impactSuppressed = false;
        }

        // ------------------------------------------------------------------
        private void Setup()
        {
            ready = true;

            body = GetComponent<Rigidbody2D>();

            if (body == null)
                body = GetComponentInChildren<Rigidbody2D>();

            radius = cfg.radius;

            if (radius <= 0f)
            {
                var col = GetComponentInChildren<Collider2D>();

                radius = (col != null)
                    ? Mathf.Max(0.1f, col.bounds.extents.magnitude * 0.7f)
                    : 0.5f;

                // Printed because it is DERIVED per ship rather than being a
                // number anyone could look up, so the honest answer to "what is
                // the default" is to show what it resolved to.
                if (!_saidRadius)
                {
                    _saidRadius = true;

                    Log.LogInfo(
                        "contact radius resolved to " +
                        radius.ToString("0.##") + " on '" + gameObject.name +
                        "' from its own collider" +
                        ((col != null)
                            ? (" (bounds extents " +
                               col.bounds.extents.ToString("0.##") + ")")
                            : " (none found - using 0.5)") +
                        ". Set \"radius\" yourself to override it. Said once " +
                        "per run.");
                }
            }

            // ENTITIES AND FRUITS, NEVER PLAYER. This rides the ship, so adding
            // the Player layer would mean ramming yourself - and the ship is on
            // Player rather than Entities, which is the same asymmetry that once
            // made the gravity well's `pullPlayer` dead code no amount of force
            // could revive.
            mask = LayerMask.GetMask("Entities", "Fruits");
        }

        private bool Armed
        {
            get
            {
                if (cfg == null)
                    return false;

                if (!cfg.whileBoosting)
                    return true;

                // `IsBoosted` is public and there is no BoostEnded event to
                // subscribe to, so polling is the intended route rather than a
                // shortcut.
                var move = GetComponentInChildren<ShipMovement>();

                return move != null && move.IsBoosted;
            }
        }

        private void FixedUpdate()
        {
            if (cfg == null)
                return;

            if (cfg.ignoreSelfDamage)
                ApplySelfDamagePolicy();

            if (!Armed)
                return;

            if (cfg.detect != ModuleForgeContact.DetectSwept)
                return;

            try
            {
                if (!ready)
                    Setup();

                if (body == null || mask == 0)
                    return;

                Vector2 v = body.linearVelocity;
                float speed = v.magnitude;

                if (speed < 0.01f)
                    return;

                // ★ EXACTLY ChargerHead's sweep: how far this body will travel
                // before the next physics step. **That is the gap a discrete
                // collision falls into**, and at boost speed on a 0.02s step it
                // is wider than an enemy.
                float distance = speed * Time.fixedDeltaTime;

                _filter.useTriggers = Physics2D.queriesHitTriggers;
                _filter.SetLayerMask(mask);

                int n = Physics2D.CircleCast(
                    transform.position, radius, v / speed, _filter, _hits,
                    distance);

                for (int i = 0; i < n && i < _hits.Length; i++)
                    Consider(_hits[i].collider, _hits[i].point, v / speed, speed);
            }
            catch (Exception e)
            {
                Log.LogError("A contact sweep failed: " + e);
                enabled = false;
            }
        }

        // The other detection mode - the stock Hazard's way. Kept so the two can
        // be compared, and honest for anything slow enough that a callback does
        // not miss.
        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (cfg == null || !Armed ||
                cfg.detect != ModuleForgeContact.DetectCollision)
            {
                return;
            }

            try
            {
                if (!ready)
                    Setup();

                if (collision == null || collision.collider == null)
                    return;

                Vector2 v = (body != null) ? body.linearVelocity : Vector2.zero;
                float speed = v.magnitude;
                Vector2 dir = (speed > 0.01f) ? v / speed : Vector2.right;

                Vector2 at = (collision.contactCount > 0)
                    ? collision.GetContact(0).point
                    : (Vector2)transform.position;

                Consider(collision.collider, at, dir, speed);
            }
            catch (Exception e)
            {
                Log.LogError("A contact collision failed: " + e);
            }
        }

        // ------------------------------------------------------------------
        private void Consider(
            Collider2D col, Vector2 at, Vector2 dir, float speed)
        {
            if (col == null)
                return;

            Unit other = col.GetComponentInParent<Unit>();

            if (other == null || ReferenceEquals(other, self))
                return;

            // Never your own side. A minion you summoned is on Entities and
            // would otherwise be rammed by its owner, which is the shape that
            // made a swarm eat itself once.
            if (self != null && self.IsFriendsWith(other))
                return;

            var hb = other.GetComponentInChildren<HealthBase>();

            // `IsDead` lives on DamagableResource rather than on HealthBase, so
            // a corpse has to be spotted by its health instead. Worth doing:
            // ploughing through corpses would burn the per-target budget on
            // things that cannot be hurt.
            if (hb == null || hb.CurrentHealth <= 0f)
                return;

            float last;

            if (lastHit.TryGetValue(hb, out last) &&
                Time.time - last < Mathf.Max(0.02f, cfg.repeatDelay))
            {
                return;
            }

            if (lastHit.Count > 32)
                Prune();

            lastHit[hb] = Time.time;

            float amount = cfg.damage;

            if (cfg.scaleBySpeed)
            {
                if (speed < cfg.minSpeed)
                    return;

                amount = Mathf.Min(
                    cfg.maxDamage, (speed - cfg.minSpeed) * cfg.damagePerSpeed);
            }

            if (amount > 0f)
                Hurt(hb, other, amount);

            Rigidbody2D rb = col.attachedRigidbody;

            if (cfg.push != 0f && rb != null)
                rb.AddForceAtPosition(dir * cfg.push, at, ForceMode2D.Impulse);

            // Your ship bounces off, exactly as ChargerHead recoils. Without it
            // a shoved enemy ploughs away and the impact reads as nothing having
            // happened to you.
            if (cfg.recoil != 0f && body != null)
                body.AddForceAtPosition(-dir * cfg.recoil, at, ForceMode2D.Impulse);
        }

        private void Hurt(HealthBase hb, Unit victim, float amount)
        {
            try
            {
                Damage d = new Damage();
                d.amount = amount;
                d.damageType = cfg.damageType;

                // ★ A BARE `return` HERE COST TWO TEST ROUNDS ON THE WEAPON
                // SIDE. Contact damage arrives with no weapon to borrow an
                // element from, so a null type used to swallow every point of
                // damage without a word - "they bump into each other and doesnt
                // seem to hurt one another". The builder resolves a type so this
                // cannot happen from a module file, and the guard stays anyway
                // and SAYS SO.
                if (d.damageType == null)
                {
                    if (!_saidNoType)
                    {
                        _saidNoType = true;

                        Log.LogWarning(
                            "contact damage has no damageType, so it cannot " +
                            "deal damage at all. Name a \"damageType\" in the " +
                            "module's contactDamage block. Said once per run.");
                    }

                    return;
                }

                hb.TakeDamage(d);

                // THE GAME CREDITS A KILL FROM EXACTLY THREE PLACES and none of
                // them is ours, so without this a ram kill is invisible to every
                // on-kill reward - including this mod's own gas cloud, which is
                // the combination somebody will try first.
                //
                // Note the game's own hazards do NOT do this, so a Larva's beak
                // kill credits nobody either; this is us being better behaved
                // than the thing we copied, deliberately.
                if (self != null && JustDied(hb))
                    ModuleForgeKills.CreditKill(self, victim, null);
            }
            catch (Exception e)
            {
                Log.LogError("Contact damage failed: " + e);
            }
        }

        private static bool JustDied(HealthBase hb)
        {
            var dr = hb as DamagableResource;
            return dr != null && dr.IsDead;
        }

        // A destroyed Unity object compares equal to null but is still a
        // perfectly good dictionary key, so entries have to be swept or the
        // table grows for the whole run.
        private void Prune()
        {
            List<HealthBase> dead = null;

            foreach (var kv in lastHit)
            {
                if (kv.Key == null || Time.time - kv.Value > 3f)
                {
                    if (dead == null)
                        dead = new List<HealthBase>();

                    dead.Add(kv.Key);
                }
            }

            if (dead == null)
                return;

            for (int i = 0; i < dead.Count; i++)
                lastHit.Remove(dead[i]);
        }

        private void OnDestroy()
        {
            // Both halves of the revoke. The tint has to be released by hand or
            // the colour sticks for the rest of the run, and the ship's own
            // crash damage has to go back to whatever the game had it at.
            RestoreSelfDamage();

            if (self != null && cfg != null && cfg.hasTint)
                ModuleForgeUnitTint.Clear(self, ModuleForgeUnitTint.Contact);
        }
    }
}
