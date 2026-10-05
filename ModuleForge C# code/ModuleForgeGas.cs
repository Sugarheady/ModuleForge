using UnityEngine;

namespace ModuleForge
{
    // A lingering cloud of GAS, left on a corpse by a kill-reward module.
    //
    // ---------------------------------------------------------------------
    // WHY THIS IS A DUPLICATE AND NOT A BRIDGE
    // ---------------------------------------------------------------------
    //
    // It is a deliberate copy of Weapon Forge's `ForgeGas` / `ForgeGasCloud`,
    // and the decision follows the CRIT AND LEECH call rather than the
    // `coolHeat` one. The discriminator is not size or taste, it is whether a
    // standalone case exists:
    //
    //   - `coolHeat` BRIDGES and deliberately grows no local fallback, because
    //     an overheat meter only ever exists inside a Weapon Forge weapon
    //     *file*. With that mod absent there is no meter in the world to act
    //     on, so a local copy would have nothing to serve.
    //   - A CLOUD has no such dependency. `Spawn` takes a config, a position, a
    //     drift vector and an owner - and **not a weapon**. A gas module on a
    //     ship grid should leave clouds on corpses whether or not Weapon Forge
    //     is installed, killing things with a perfectly ordinary stock gun.
    //
    // So: duplicate, per [mods must stand alone]. The cost is the standing
    // obligation the twin rule already imposes - a bug found in one of these is
    // a lead to check in the other.
    //
    // ---------------------------------------------------------------------
    // WHAT WAS DELIBERATELY NOT PORTED - do not "restore" it from the twin
    // ---------------------------------------------------------------------
    //
    // Roughly a third of the weapon-side config describes a PROJECTILE, and a
    // module does not have one. Absent BY DESIGN, not by omission:
    //
    //   - **The TRAIL** (`trail`, `trailMode`, `trailEvery`, `trailInterval`,
    //     `trailRadius`, `trailDuration`, `trailPuffs`, `trailMax`,
    //     `trailCfg`). Gas laid along a flight path. There is no flight here -
    //     a kill happens at one point.
    //   - **`puffGravity` / `puffDrag` / `puffPierceTerrain`.** Their own
    //     comment over there says they "reach past the cloud and change the
    //     PROJECTILE". There is no projectile to reach.
    //   - **`impactOnly` / `bloomOnDeath` / `bloomOnImpact`.** These choose
    //     WHERE in a shot's life a cloud appears. A kill is one instant, so
    //     every one of them collapses to "yes".
    //
    // ---------------------------------------------------------------------
    // THE ONE REAL DESIGN DIFFERENCE, and it is the interesting one
    // ---------------------------------------------------------------------
    //
    // Weapon Forge's `onKill: { "gas": true }` is a **switch**: the cloud is
    // already described by the weapon's own `gas` block further up the same
    // file, and the switch only decides WHEN one appears. Its build log says
    // exactly that - *"this key only decides WHEN one appears"* - and it
    // refuses the switch on a weapon with no `gas` block.
    //
    // A module has no weapon file, so there is nothing to point at. **Here the
    // `gas` block IS the description**, which is why this config carries the
    // payload fields directly and why there is no "you switched this on but
    // described no cloud" refusal to port: describing it is the only way to
    // ask for it.
    public static class ModuleForgeGas
    {
        public class Config
        {
            // ---- shape and life -----------------------------------------
            public float radius = 1.6f;       // starting radius
            public bool grows = true;         // radius climbs to growTo
            public float growTo = 3.2f;       // radius at the end of growTime
            public float growTime = 1.2f;     // seconds radius -> growTo
            public float duration = 4f;       // seconds the cloud lives
            public float fade = 0.6f;         // seconds of fade-out at the end

            // A corpse is not travelling, so a drifting cloud has nothing to
            // inherit and simply sits. Kept anyway - `drift` still reads as a
            // slow spread once something gives it a direction, and leaving the
            // field out would make the two mods' vocabularies disagree for no
            // gain. Default OFF, unlike the weapon side, because the weapon
            // side's default was chosen for a shot in flight.
            public bool drifts;
            public float drift = 1.2f;        // units/second
            public float driftDamping = 0.4f; // how fast it settles

            // Total clouds THIS MODULE may have alive at once.
            //
            // Grouped by config reference identity (see `ModuleForgeGasCloud
            // .CountFor`), and `Clone` shares the reference on purpose, so the
            // budget belongs to the module DEFINITION rather than to each card
            // on the grid. Two copies of the same module therefore share one
            // budget, which is the reading that keeps a grid full of them from
            // carpeting the level.
            public int max = 24;

            // ---- payload (any combination) ------------------------------
            // The game's own fire. Cheap, stacks with everything, and shows the
            // burning visual for free - but its damage is FLAT (1/sec while
            // alight however high the level goes), so this buys DURATION rather
            // than intensity.
            public float burnPerSecond;

            // The cloud's own damage. Works on anything, including the
            // fireproof Tablet enemy, and is the only payload that scales.
            public float damagePerSecond;

            // Fraction of velocity bled off per second, 0-1.
            public float slow;

            // Outward shove per second. Negative pulls inward.
            public float push;

            // Burn level removed per second from UNITS inside. Enough of this
            // and the cloud is a fire extinguisher rather than a weapon.
            public float extinguish;

            // Burn level removed per second from burning TERRAIN inside. This
            // is the separate cell-fire system.
            //
            // CALIBRATION MATTERS AND SMALL NUMBERS DO NOTHING. Cell fire
            // thresholds are not normalised: CellType_Solid - the main terrain
            // - ignites at **45**, while Slime is 1, Tuff 1.3, Moss 2.5, Night
            // 5, Bark and Gold 10. A burning Solid cell sits above 45 and is
            // topped up by every burning neighbour each frame, so 6/second
            // could never win. 120 clears it in well under a second.
            public float extinguishTerrain;

            // The inverse: burn level ADDED per second to terrain inside the
            // cloud. The game only ever ignites terrain on a projectile
            // COLLISION, so a cloud hanging in the air has no other route to it.
            public float igniteTerrain;

            // ---- who it touches -----------------------------------------
            public bool hitEnemies = true;

            // Off by default, the same call as the gravity well's `pullPlayer`:
            // a payload that can kill its own user should be a decision. Note
            // `extinguish` reaches the owner EITHER WAY - putting your own fire
            // out is the whole point of an extinguisher gas, and refusing to do
            // it because `hitPlayer` is off would be the wrong reading.
            public bool hitPlayer;

            // Seconds between applications. Damage calls drive hit flashes,
            // sounds and the aggro event, so this is not free per frame.
            public float tickRate = 0.25f;

            // ---- the chain ----------------------------------------------
            //
            // Whether a kill scored BY A CLOUD seeds another cloud.
            //
            // ON by default, matching the weapon side, and the reasoning there
            // is his rather than mine: it reads like a runaway and is not one.
            // Every enemy dies exactly once and one death pays out once, so the
            // clouds a fight can produce are bounded by how many things are in
            // the fight. It walks through a crowd and stops. `max` is the
            // separate, harder ceiling.
            public bool chains = true;

            // ---- look ----------------------------------------------------
            public Color color = new Color(0.55f, 0.95f, 0.4f, 0.5f);

            // A GAME sprite name, and stock-only ON PURPOSE.
            //
            // This is the same line `ForgeAssets.ResolveSpriteFrames` already
            // draws: custom-FILE loading is Weapon Forge's pipeline and is
            // borrowed for icons, but `ForgeInterop.TryResolveIcon` rebuilds
            // what it returns at the module-icon footprint - 24x24 at PPU 40 -
            // which is the wrong size for anything that is not an icon. A puff
            // is not an icon. Empty means the generated soft blob below, which
            // is what nearly every cloud should use anyway.
            public string sprite = "";

            public string sfx = "";

            // Puffs drawn per cloud. More reads as thicker gas; each is a
            // sprite, so this is the main cost knob.
            public int puffs = 5;

            // How much the puffs churn, in degrees/second.
            public float swirl = 25f;

            // A named damage type for the cloud's own damage.
            //
            // Empty means "inherit from the weapon that scored the kill", which
            // is what the weapon side always does and is right far more often:
            // the cloud is then resisted and coloured like the gun that made
            // it. A module on the SHIP grid reaches every gun carried, so
            // inheriting keeps one module sensible across all of them.
            //
            // Set it explicitly when the module is the source of the element
            // rather than the gun - a poison core that should stay poison
            // whatever is bolted to it.
            public string damageType = "";

            // ---- resolved at build time ---------------------------------
            [System.NonSerialized] public Sprite[] artFrames;
            [System.NonSerialized] public float artFps;
            [System.NonSerialized] public string sfxGuid;
            [System.NonSerialized] public Resource damageResource;

            // Whether the sprite NAME opted this cloud's puffs into animating.
            // Same opt-in rule as the five one-sprite keys on the weapon side:
            // a bare name stays a still puff, so nothing already written starts
            // moving on the next launch.
            [System.NonSerialized] public bool animates;

            // Nothing to do, nothing to spawn. Checked before a cloud is made
            // rather than after, so a module that describes a purely decorative
            // cloud costs one branch instead of a GameObject and a tick loop.
            //
            // NOTE this deliberately does NOT include the look fields. A cloud
            // with a colour and no payload is a module doing nothing, and
            // saying so at build time is better than drawing it.
            public bool DoesAnything
            {
                get
                {
                    return burnPerSecond > 0f || damagePerSecond > 0f ||
                           slow > 0f || push != 0f ||
                           extinguish > 0f || extinguishTerrain > 0f ||
                           igniteTerrain > 0f;
                }
            }
        }
    }
}
