using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // Pays out when the ship kills something.
    //
    // SHIP grid vs WEAPON grid falls out of the game's own architecture rather
    // than needing a flag. `WeaponFactory.Create` walks only the modules in a
    // WEAPON's cluster and calls `IWeaponModifier.Modify` on their effects - a
    // ship-grid module is never in a weapon cluster, so it never gets that
    // call. So:
    //
    //     Modify() was called  ->  we are in a weapon's grid, and `_weapon`
    //                              records which one. Only that weapon's kills
    //                              pay out.
    //     Modify() never came  ->  we are on the ship, `_weapon` stays null,
    //                              and any weapon's kills pay out.
    //
    // Which is exactly the rule that was asked for, at no cost.
    [Serializable]
    public class KillRewardEffect
        : ModuleEffect, IWeaponModifier, IHasDescriptionForUnit,
          ModuleForgeKills.IKillListener, ModuleForgeLive.IGated
    {
        // ---- resource ------------------------------------------------
        public Resource resource;
        public FloatSeries amount;

        // Spawn a pickup to fly through instead of granting it straight away.
        public bool asPickup;

        // ---- chance and cap ------------------------------------------
        // 0-1. A series so a level can buy reliability rather than size.
        public FloatSeries chance;

        // Ceiling on payouts per second. One explosion can kill a dozen
        // enemies at once, which is the case this exists for. 0 = no cap.
        public float maxPerSecond;

        // ---- burn relief ---------------------------------------------
        // Burn level removed from the ship per kill - as in the ship is ON
        // FIRE. NOT the overheat meter; that is `coolHeat` below, and the two
        // were one word apart on the card until R15 ("On kill: cools").
        // The only payout that can save you mid-fire.
        public FloatSeries clearBurn;

        // ---- heat relief ---------------------------------------------
        // Heat removed from Weapon Forge's `overheat` meter per kill.
        //
        // NEEDS BOTH MODS, and the check is structural rather than a flag:
        // `overheat` is a per-weapon block from a Weapon Forge weapon file, so
        // with that mod absent there is no meter in existence to take heat off
        // and `ForgeInterop.CoolWeaponHeat` resolves to nothing. Module Forge
        // deliberately does NOT grow its own heat system to fall back on -
        // per-weapon tuning is the other mod's half of the split, and a second
        // implementation would be two owners of one mechanic.
        //
        // Scope follows the same rule as everything else here: in a weapon's
        // grid only that weapon's meter cools, on the ship grid whichever gun
        // scored the kill does.
        public FloatSeries coolHeat;

        // ---- timed buff ----------------------------------------------
        // Per-module override of the global buff-indicator style: "off",
        // "feed", "icons", "both", or empty to follow the BepInEx config.
        public string buffIndicator = "";

        public float buffDuration;
        public FloatSeries buffFireRate;
        public FloatSeries buffDamage;
        public int buffMaxStacks = 1;

        // ---- gas on death ---------------------------------------------
        //
        // A lingering cloud left on the corpse. Null unless the module file
        // carries a `gas` block, and the block IS the description of the cloud
        // - see the header on `ModuleForgeGas` for why that differs from the
        // weapon side, where `onKill: { "gas": true }` is only a switch
        // pointing at the weapon's own `gas` block.
        //
        // NOT a FloatSeries anywhere inside it, deliberately. Every scalar in
        // this class is series-capable so it can answer to module level, and a
        // cloud could be too - but a per-level cloud means re-deriving the whole
        // config on every level change, and `Module.Level` moves with no
        // install callback (a BoosterCore changes it in place). That is the
        // invisible-booster trap from the other direction, so the honest
        // version is a flat cloud plus a log line saying so, rather than a
        // scaling one that silently does not scale.
        public ModuleForgeGas.Config gas;

        // ---- runtime -------------------------------------------------
        [NonSerialized] private Unit.Data _owner;
        [NonSerialized] private WeaponBase _weapon;
        [NonSerialized] private bool _registered;
        [NonSerialized] private float _windowStartedAt;
        [NonSerialized] private float _paidThisSecond;

        // Deterministic-ish rolling, seeded off the payout count so a run is
        // not affected by anything else consuming UnityEngine.Random.
        [NonSerialized] private int _rolls;

        public Unit.Data Owner { get { return _owner; } }
        public WeaponBase ScopedWeapon { get { return _weapon; } }

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        private float Amount { get { return Val(amount, 0f); } }
        private float Chance { get { return Mathf.Clamp01(Val(chance, 1f)); } }
        private float BurnRelief { get { return Val(clearBurn, 0f); } }
        private float HeatRelief { get { return Val(coolHeat, 0f); } }
        private float BuffFireRate { get { return Val(buffFireRate, 0f); } }
        private float BuffDamage { get { return Val(buffDamage, 0f); } }

        // FloatSeries is a STRUCT in this game, so there is no null to check
        // for - an unspecified one is simply all zeroes. EffectBuilder.SeriesOr
        // is what supplies a non-zero default (chance = 1), so by the time a
        // value reaches here "absent" already means 0.
        private float Val(FloatSeries s, float fallback)
        {
            float v = s.GetElement(Level - 1);
            return (v < 0f) ? 0f : v;
        }

        public override void OnInstalled(Unit.Data unit)
        {
            Engage(unit);
            ModuleForgeLive.Track(this, unit);
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            ModuleForgeLive.Untrack(this);
            Disengage();
        }

        // Unpowered or disconnected is the same thing as not being there, which
        // is what the game does with every stock module - see ModuleForgeLive.
        //
        // THIS EFFECT IS WHY THAT FILE EXISTS. `ModuleForgeKills` is a global
        // listener list, so an unpowered kill-reward module was still asked
        // about every kill and still paid out. And it failed in the worse
        // direction than "it works when it should not": never having been
        // handed a weapon (`WeaponFactory.Create` only walks the powered ones),
        // an unpowered module sitting in a WEAPON's cluster had `_weapon` null,
        // which this class reads as "any weapon" - so it paid out for every gun
        // on the ship, the exact opposite of what its position says.
        public void OnModuleLiveChanged(Unit.Data unit, bool live)
        {
            if (live)
                Engage(unit);
            else
                Disengage();
        }

        // The one owner of each direction, so install, uninstall and the
        // powered gate cannot drift apart. `_registered` makes both idempotent.
        private void Engage(Unit.Data unit)
        {
            _owner = unit;

            if (_registered)
                return;

            ModuleForgeKills.Register(this);
            _registered = true;
        }

        private void Disengage()
        {
            if (!_registered)
                return;

            ModuleForgeKills.Unregister(this);
            _registered = false;
            _weapon = null;
        }

        // Only ever called for a module in a WEAPON's cluster - that is the
        // whole scoping mechanism. Called again whenever the weapon is rebuilt,
        // so it re-captures rather than assuming the first one holds.
        public void Modify(WeaponBase weaponBase)
        {
            _weapon = weaponBase;
        }

        public void OnKill(Unit killer, Unit victim)
        {
            if (killer == null)
                return;

            if (!Allowed())
                return;

            // THE CLOUD IS INSIDE `Allowed()` WITH EVERY OTHER REWARD, and the
            // first cut of this had it outside.
            //
            // The argument for outside was that `chance` and `maxPerSecond`
            // read as belonging to the RESOURCE payout, and that a chain wants
            // to be deterministic. Both true, and both beaten by the trap it
            // opens: `chance` gates the resource, the burn relief, the heat
            // relief and the buff, so a module written with `"chance": 0.1`
            // means "this module is rare" - and a cloud that ignored it would
            // appear on every single corpse while everything else fired one
            // time in ten. A rare module that is not rare is a worse failure
            // than a chain that is thinner than it could be, and a stochastic
            // chain still chains.
            LeaveGas(killer, victim);

            float given = Amount;

            if (given > 0f && resource != null)
            {
                // A pickup that cannot be dropped falls through to paying the
                // tank, rather than paying nothing. The reward the file asked
                // for is "this much of this resource per kill"; the pickup is
                // how it arrives, and a missing prefab should cost the flourish
                // and not the payout.
                bool dropped =
                    asPickup &&
                    ModuleForgeKillPickup.Drop(
                        victim != null ? victim.transform.position
                                       : killer.transform.position,
                        resource, given);

                if (!dropped && killer.HasTank(resource))
                    Grant(killer.GetTank(resource), given);
            }

            float burn = BurnRelief;

            if (burn > 0f)
            {
                // The setter clamps at 0 and only adds the catching-fire bonus
                // when crossing UPWARD, so pulling it down is safe.
                killer.ComponentData.BurnLevel -= burn;
            }

            float heat = HeatRelief;

            if (heat > 0f)
            {
                // `_weapon` is null on the ship grid, which the bridge reads as
                // "whichever gun is hot" - the same meaning it has everywhere
                // else in this class. With Weapon Forge absent this returns 0
                // and says so once rather than every kill.
                int cooled = ForgeInterop.CoolWeaponHeat(killer, _weapon, heat);

                if (cooled == 0)
                    SayNoHeat();
            }

            if (buffDuration > 0f && (BuffFireRate > 0f || BuffDamage > 0f))
            {
                // The per-module override wins over the global default; empty
                // means "follow the config".
                bool indicate = string.IsNullOrEmpty(buffIndicator)
                    ? ModuleForgeBuffHud.style != ModuleForgeBuffHud.Style.Off
                    : ModuleForgeBuffHud.ParseStyle(buffIndicator) !=
                          ModuleForgeBuffHud.Style.Off;

                // The module's OWN card art - which is what he asked for
                // ("use the icon of the weapon or module buffing you") and needs
                // no new field: ModuleEffect.Module is public, and Icon/Color are
                // the same two the grid card and the ground pickup use.
                Sprite icon = (base.Module != null) ? base.Module.Icon : null;
                Color color = (base.Module != null)
                    ? (Color)base.Module.Color : Color.white;

                ModuleForgeKillBuff.Grant(
                    killer, _weapon, BuffFireRate, BuffDamage,
                    buffDuration, Mathf.Max(1, buffMaxStacks),
                    this, icon, color, indicate);
            }
        }

        // A cloud on the corpse.
        //
        // The position is read off the victim BEFORE anything else runs,
        // because the victim is on its way out - the same reason the pickup
        // drop above reads it where it does.
        private void LeaveGas(Unit killer, Unit victim)
        {
            if (gas == null || !gas.DoesAnything)
                return;

            // A cloud's OWN kill only seeds another cloud when the file asked
            // for the chain. Without this test a single gas module in a packed
            // room is a self-feeding reaction: cloud kills something, corpse
            // grows a cloud, that cloud kills something.
            //
            // It is bounded even when on - every enemy dies exactly once - but
            // "bounded" and "wanted" are different questions, so it stays a
            // switch. See `ModuleForgeGas.Config.chains`.
            if (ModuleForgeGasCloud.Crediting && !gas.chains)
                return;

            Vector2 where = (victim != null)
                ? (Vector2)victim.transform.position
                : (Vector2)killer.transform.position;

            // The weapon that scored the kill, so a kill scored by this cloud
            // is credited to it. `_weapon` is null on the ship grid, which
            // every reader treats as "whichever gun" - the same meaning it has
            // everywhere else in this class.
            WeaponBase credit = (_weapon != null)
                ? _weapon
                : ModuleForgeKills.Credit;

            // RESOLVED PER CLOUD AND NEVER WRITTEN BACK ONTO THE CONFIG.
            //
            // The tempting version is `if (gas.damageResource == null)
            // gas.damageResource = credit.Damage.damageType;` - one line, and
            // wrong in the way this project keeps paying for. A Config belongs
            // to the module DEFINITION while the weapon is whatever happened to
            // score this kill, so that line would latch the first gun's element
            // onto the module for the rest of the session: order-dependent,
            // invisible, and it would quietly undo the live-value behaviour it
            // was trying to provide. Same reason `ForgeLeech.Resolve` hands its
            // cap back as an out param instead of assigning it.
            //
            // So the config's own value means "the file named a type", and null
            // means "inherit, every time, from whoever just got the kill" -
            // which is what keeps one ship-grid module sensible across every
            // gun carried, including anything adaptive damage has just changed.
            Resource element = (gas.damageResource != null)
                ? gas.damageResource
                : ((credit != null) ? credit.Damage.damageType : null);

            // No inherited velocity: nothing arrived here travelling. A corpse
            // is not going anywhere, so a `drift` cloud just sits - which is
            // right, and is why `drifts` defaults OFF on this side.
            ModuleForgeGasCloud cloud =
                ModuleForgeGasCloud.Spawn(gas, where, Vector2.zero, killer);

            if (cloud == null)
                return;

            cloud.SetWeapon(credit);
            cloud.SetDamageType(element);
        }

        // ONE-SHOT, and named `_said*` so ModuleForgeDiagnosticGates sweeps it
        // at each run entry - a module that had no heat to cool in one run may
        // well have some in the next.
        private static bool _saidNoHeat;

        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge");

        private void SayNoHeat()
        {
            if (_saidNoHeat)
                return;

            _saidNoHeat = true;

            Log.LogWarning(
                "a kill-reward module set \"coolHeat\" but no " +
                "heat meter was found to take it off. Either Weapon Forge is " +
                "not installed - \"overheat\" is a per-weapon block from a " +
                "Weapon Forge weapon file, and this key does nothing without " +
                "it - or the gun that scored the kill simply has no " +
                "\"overheat\" block. On a WEAPON grid it only cools that " +
                "weapon; on the SHIP grid it cools whichever gun got the kill. " +
                "Said once per run.");
        }

        // Charge() clamps to [0, Capacity], which is right for a normal tank.
        // An INFINITE tank (Money, the run-wide currency) need not carry a
        // meaningful Capacity, and Charge would clamp the payout to it and grant
        // nothing at all. Its Value setter refuses decreases by itself, so
        // adding directly is both safe and the only thing that works there.
        private static void Grant(ResourceTank tank, float amount)
        {
            if (tank == null || amount <= 0f)
                return;

            if (tank.isInfinite)
                tank.Value = tank.Value + amount;
            else
                tank.Charge(amount);
        }

        // Chance roll and the per-second cap, in that order, so a failed roll
        // does not spend cap.
        private bool Allowed()
        {
            float c = Chance;

            if (c < 1f)
            {
                _rolls++;

                if (UnityEngine.Random.value > c)
                    return false;
            }

            if (maxPerSecond <= 0f)
                return true;

            if (Time.time - _windowStartedAt >= 1f)
            {
                _windowStartedAt = Time.time;
                _paidThisSecond = 0f;
            }

            // Caps the RESOURCE granted per second, not the number of payouts -
            // "amount 100, maxPerSecond 300" means 300 a second, not 300 kills a
            // second. Kept identical to Weapon Forge's copy on purpose. A config
            // granting no resource falls back to counting payouts so a buff-only
            // module still has a limiter.
            float amount = Amount;

            float cost = (amount > 0f && resource != null) ? amount : 1f;

            if (_paidThisSecond + cost > maxPerSecond)
                return false;

            _paidThisSecond += cost;
            return true;
        }

        // ------------------------------------------------------------------
        // The module's own card
        // ------------------------------------------------------------------

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            string scope = (_weapon != null) ? "this weapon" : "any weapon";

            if (Amount > 0f && resource != null)
            {
                properties.Add(new DisplayableProperty(
                    (asPickup ? "Drops " : "On kill ") + resource.SpriteTag,
                    "+" + Amount.ToString("0.##")));
            }

            // "puts out", not "cools" - the same rename as Weapon Forge's build
            // log, and for the same reason: this is the ship BURNING, and there
            // is now a separate line below for the overheat meter.
            if (BurnRelief > 0f)
            {
                properties.Add(new DisplayableProperty(
                    "On kill: puts out", "-" + BurnRelief.ToString("0.##")));
            }

            if (HeatRelief > 0f)
            {
                properties.Add(new DisplayableProperty(
                    "On kill: weapon heat",
                    "-" + HeatRelief.ToString("0.##") +
                    (ForgeInterop.HasWeaponOverheat ? "" : " (needs Weapon Forge)")));
            }

            if (buffDuration > 0f)
            {
                if (BuffFireRate > 0f)
                    properties.Add(new DisplayableProperty(
                        "On kill: fire rate",
                        "+" + (BuffFireRate * 100f).ToString("0.#") + "%"));

                if (BuffDamage > 0f)
                    properties.Add(new DisplayableProperty(
                        "On kill: damage",
                        "+" + (BuffDamage * 100f).ToString("0.#") + "%"));

                properties.Add(new DisplayableProperty(
                    "Buff lasts", buffDuration.ToString("0.#") + "s" +
                    (buffMaxStacks > 1
                        ? (", stacks " + buffMaxStacks) : "")));
            }

            float ch = Chance;

            if (ch < 1f)
            {
                properties.Add(new DisplayableProperty(
                    "Chance", Mathf.RoundToInt(ch * 100f) + "%"));
            }

            if (maxPerSecond > 0f)
            {
                properties.Add(new DisplayableProperty(
                    "Max per second", maxPerSecond.ToString("0.#")));
            }

            // Worth spelling out - a module that only counts one weapon's kills
            // is not something a player can guess from the grid.
            properties.Add(new DisplayableProperty("Counts kills by", scope));

            // THE CLOUD, because a new stat is not finished until the card says
            // it - the card is the only place a player can learn what a module
            // does, and this one leaves a thing on the battlefield.
            //
            // Gated on DoesAnything rather than on the block existing: a `gas`
            // block with a colour and no payload draws nothing and does
            // nothing, so a card line would be advertising a cloud that will
            // never appear.
            if (gas != null && gas.DoesAnything)
            {
                properties.Add(new DisplayableProperty(
                    "Leaves on kill",
                    "gas cloud, " + gas.duration.ToString("0.#") + "s"));

                // One line per payload, and only for payloads that are set.
                // Four numbers in one string is the shape nobody reads.
                if (gas.damagePerSecond > 0f)
                {
                    properties.Add(new DisplayableProperty(
                        "Cloud damage",
                        gas.damagePerSecond.ToString("0.#") + "/s"));
                }

                if (gas.burnPerSecond > 0f)
                {
                    properties.Add(new DisplayableProperty(
                        "Cloud burn",
                        gas.burnPerSecond.ToString("0.#") + "/s"));
                }

                if (gas.slow > 0f)
                {
                    properties.Add(new DisplayableProperty(
                        "Cloud slow",
                        Mathf.RoundToInt(Mathf.Clamp01(gas.slow) * 100f) +
                        "%/s"));
                }

                // Said on the card and not only in the log, because it is the
                // one setting here whose effect a player sees and cannot
                // explain: a room that keeps smoking after they stopped firing.
                if (gas.chains)
                {
                    properties.Add(new DisplayableProperty(
                        "Cloud kills", "leave more gas"));
                }
            }

            // His call: the rule is that anything settable in JSON shows on the
            // card, presentation options included. Only listed when the buff it
            // would announce actually exists.
            if (buffDuration > 0f && (BuffFireRate > 0f || BuffDamage > 0f))
            {
                properties.Add(new DisplayableProperty(
                    "Buff indicator",
                    string.IsNullOrEmpty(buffIndicator)
                        ? Described(ModuleForgeBuffHud.style)
                        : Described(
                            ModuleForgeBuffHud.ParseStyle(buffIndicator))));
            }
        }

        private static string Described(ModuleForgeBuffHud.Style style)
        {
            switch (style)
            {
                case ModuleForgeBuffHud.Style.Off:   return "off";
                case ModuleForgeBuffHud.Style.Icons: return "icon";
                case ModuleForgeBuffHud.Style.Both:  return "status + icon";
                default:                             return "status line";
            }
        }

        public override ModuleEffect Clone()
        {
            return new KillRewardEffect
            {
                resource = this.resource,
                amount = this.amount,
                asPickup = this.asPickup,
                chance = this.chance,
                maxPerSecond = this.maxPerSecond,
                clearBurn = this.clearBurn,
                coolHeat = this.coolHeat,
                buffDuration = this.buffDuration,
                buffFireRate = this.buffFireRate,
                buffDamage = this.buffDamage,
                buffMaxStacks = this.buffMaxStacks,

                // Was missing, so a per-module indicator override was silently
                // dropped on every real instance: `Module`'s constructor Clones
                // every effect off the ModuleData, so THIS is the copy the game
                // actually runs and anything not listed here reverts to the
                // global config.
                buffIndicator = this.buffIndicator,

                // THE REFERENCE IS SHARED ON PURPOSE - this is not a missing
                // deep copy.
                //
                // `ModuleForgeGasCloud.CountFor` groups the cloud budget by
                // config reference IDENTITY, so sharing is what makes `max` a
                // budget for the module rather than for each card: two copies
                // of the same module on one grid share one allowance instead of
                // doubling it. Copying the config per clone would give every
                // instance its own budget and quietly multiply the cap by how
                // many the player happens to be carrying - the same trap the
                // catalyst's node cap has, and the reason its Config is not
                // marked [Serializable].
                //
                // Safe because nothing writes to the config at runtime: the
                // damage type that varies per cloud is resolved in LeaveGas and
                // handed to the cloud instance, never folded back in here.
                gas = this.gas
            };
        }
    }
}
