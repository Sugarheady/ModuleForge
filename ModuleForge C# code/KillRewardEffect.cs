using System;
using System.Collections.Generic;
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
          ModuleForgeKills.IKillListener
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
        // Burn level removed from the ship per kill. The only payout that can
        // save you mid-fire.
        public FloatSeries clearBurn;

        // ---- timed buff ----------------------------------------------
        // Per-module override of the global buff-indicator style: "off",
        // "feed", "icons", "both", or empty to follow the BepInEx config.
        public string buffIndicator = "";

        public float buffDuration;
        public FloatSeries buffFireRate;
        public FloatSeries buffDamage;
        public int buffMaxStacks = 1;

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
            _owner = unit;

            if (_registered)
                return;

            ModuleForgeKills.Register(this);
            _registered = true;
        }

        public override void OnUninstalled(Unit.Data unit)
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

            if (BurnRelief > 0f)
            {
                properties.Add(new DisplayableProperty(
                    "On kill: cools", "-" + BurnRelief.ToString("0.##")));
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
                buffDuration = this.buffDuration,
                buffFireRate = this.buffFireRate,
                buffDamage = this.buffDamage,
                buffMaxStacks = this.buffMaxStacks,

                // Was missing, so a per-module indicator override was silently
                // dropped on every real instance: `Module`'s constructor Clones
                // every effect off the ModuleData, so THIS is the copy the game
                // actually runs and anything not listed here reverts to the
                // global config.
                buffIndicator = this.buffIndicator
            };
        }
    }
}
