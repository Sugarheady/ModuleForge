# Module Forge

**Build custom grid modules for PUNK — the little upgrades you snap onto your ship and weapons — by writing small JSON files.**

No C#, no compiling. Module Forge clones a stock module of the right kind as a shell, swaps in the effects you listed, and registers it so it works with loot, the shop, and save/continue.

Built against **PUNK Playtest v0.12.9**.

---

## Requirements

- **BepInEx** — see [PunkMods](https://github.com/Osanchez/PunkMods) for a good walkthrough of setting up mods for PUNK.

## Install

1. Copy `ModuleForge.dll` into `...\PUNK Playtest\BepInEx\plugins\` (its own folder is fine).
2. Run the game once, then close it.
3. A **`modules`** folder now sits next to the DLL with `README.txt` and several example modules.
4. Drop your own `.json` files in there and restart.

> Files are read at **startup** — restart to see changes.
>
> A bad file is skipped, never fatal. Reasons are written to `BepInEx\LogOutput.log` — search for `ModuleForge`.

---

## The Builder (recommended)

Open **`Module Builder.html`** in any browser — a single self-contained page, no install needed.

Pick a target (ship or weapon), add as many effects as you like, and every parameter is documented inline. Then **SAVE .JSON FILE** and drop it in the `modules` folder.

**`HOW TO MAKE MODULES.txt`** is the full written reference.

---

## What you can make

A module attaches to one of two places, chosen by `target`:

- **`ship`** — the ship-body grid (tanks, regen, shields, drains)
- **`weapon`** — a weapon or gadget grid (fire rate, projectiles, explosions, burn)

One module can carry **several effects**, and you can freely mix buffs with drawbacks — e.g. *+1 projectile and −20% fire rate*.

### Ship effects
| Effect | Does |
|---|---|
| `ModifyResourceCapacity` | grow or shrink a tank's maximum |
| `ResourceAutoChargeEffect` | regenerate a resource per second |
| `DrainResourceEffect` | drain per second (negative = gain) |
| `AddShieldEffect` | block a fraction of one damage type |

### Weapon effects
| Effect | Does |
|---|---|
| `ModifyWeaponProperty` | change fire rate, burst, projectile count, spread, angles, knockback, cost, range or speed — flat or as a percentage |
| `IncreaseExplosionRadiusEffect` | bigger blasts |
| `AddImpactExplosionEffect` | make shots explode on hit |
| `AddExplosionEffect` | full explosion with its own damage, radius, burn and per-projectile cost |
| `AddDischargeEffect` | the chain-lightning "spark" arc |
| `AddBurnEffect` | apply burn per projectile |
| `BurnTickRateEffect` | make burn tick **faster** (enemies only — your own burn is unaffected) |
| `BurnColorEffect` | recolour enemy flames, fixed colour or animated **RGB**, optionally including burning terrain |
| `Phasing` | your projectiles pass through terrain but still hit enemies |
| `PierceCap` | turn piercing on and cap it — pierce *N* enemies then vanish, with optional damage falloff |

### Availability
- `source`: `loot`, `shop`, `both`, or **`none`** (built and registered but never offered)
- `lootWeight` — drop chance against other crate modules
- `shopPrice` and `shopUnlockLevel` — cost and which station tier it unlocks at
- **`shopPriceIncrement`** — make the price climb with each purchase, exactly the way the game does it (a flat amount added per buy, matching the stock stat modules: fire rate is 300 then +500 each time). Or express it as a percentage of the base with `shopPricePercent`.

### Module card
Set the icon sprite, tint it with a game colour or any `#hex`, and write your own name and description.

Your pierce and phasing modules also **report themselves on the card** — hover a module on your ship grid and it shows what *that* module contributes (`PIERCE +2`, and the running ship total), while the weapon card shows the combined total.

---

## Notes & known limitations

- **Save/continue is safe.** Modules are registered at startup and rebuilt from the registry on load.
- **Damage is deliberately unavailable** in `ModifyWeaponProperty` — modifying it triggers a bug in the base game.
- **Burn, explosion and discharge effects only fire on projectile weapons**, and the pierce/phasing modules affect projectiles (for a phasing *laser*, use Weapon Forge's per-weapon flag).
- **Burn tick rate is hard-capped** so stacking can't run away. The ceiling defaults to 100 ticks/sec and lives in `BepInEx\config\com.andy.moduleforge.cfg`.
- **Effect lines only render for a module on the ship grid** — one sitting in the shop list shows just its name and description. That's stock game behaviour for every module, not a mod quirk.
- Each shop tier rolls once as you pass it, so start a fresh run to see a new shop module.
- `Resource Money` is currency and can't be used as a module resource.

---

## Companion mod

**Weapon Forge** (separate repo — https://github.com/Sugarheady/WeaponForge) does the same thing for whole weapons. When both mods are installed they cooperate automatically:

- **One burn engine** — Module Forge owns it, Weapon Forge feeds into it, so tick rates and flame colours never fight
- **Pierce caps add up** — a weapon with a built-in cap of 2 plus a +1 pierce module gives you **3**, on one counter, shown as one number
- **Merged stat lines** — no duplicated or contradictory tooltips

---

