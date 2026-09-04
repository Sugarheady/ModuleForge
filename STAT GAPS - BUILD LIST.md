# Module Forge — stat gaps, as a ranked build list

Researched 2026-08-25 against the PUNK v0.12.9 decompile. **Base-game stats only** — nothing here
depends on any Weapon Forge invention.

The question this answers: *which of the game's own stats can a module not currently change?*

---

## The boundary, so the rest of this makes sense

The game's module system can reach exactly three surfaces:

| surface | how | covers |
|---|---|---|
| `ModifyWeaponProperty` | `IWeaponModifier.Modify(weaponBase)` | **12** weapon stats (11 usable) |
| 3 augmentations | same | Burn, Discharge, Explosion |
| `Unit.Data` | `OnRecalculateUnitStats(Unit.Data)` | resource tanks, recharge, shields, minions, burn level, invisibility |

Everything else on a weapon or a ship is unreachable. `WeaponBase` alone exposes **33** settable
properties and modules reach 15 of them.

**Module Forge already covers everything the game can express**, including all 12 target properties.
So every item below needs a **custom `ModuleEffect` subclass** — which is proven ground: the mod
already ships five (`PierceModuleEffect`, `PhasingModuleEffect`, `BurnRateModuleEffect`,
`KillRewardEffect`, `BurnColorEffect`).

---

## TIER 1 — BUILT 2026-08-25 (untested, R10 tests 91–93)

All three shipped as two classes: `ExtraWeaponStatEffect` (PushForce + WarmupTime, mirroring the
stock effect's Add/Multiply + Constant/FromOriginal/FromCurrent model) and `WeaponResourceEffect`
(the ammo swap, which is a `Resource` rather than a number and so could not share the table).
Shortcut types `"pushforce"` / `"warmuptime"` / `"ammo"` pick the stat for you.

Left below as written, because the traps are the useful part.

## TIER 1 — weapon stats, low cost, high payoff

Each hooks `IWeaponModifier.Modify(WeaponBase)` and writes one property. Copy
`PierceModuleEffect`'s shape. No new plumbing.

### 1. `PushForce` — shots that shove what they hit ⭐ the one that was asked for

**This is the real "knockback".** The game has two forces and they are easy to confuse:

- `KnockbackForce` — recoil on **your ship** (`Shooter` does `AddForce(-barrel.Direction * KnockbackForce)`).
  **Already fully supported**, it is one of the 12.
- `PushForce` — the shove applied to **what you hit**. `WeaponBase.PushForce` →
  `projectile.PushForce` (`ProjectileWeapon`) → `AddForceAtPosition` on the victim. Hitscan reads
  `PushForce` directly. **No module effect reaches it.**

**Trap, and it is a big one:** a **piercing shot never pushes at all**. `Projectile.OnObjectHit`
returns at `if (PiercingData.enabled && layer != groundLayer) { MoveForward(); return; }` — *before*
the push block. So a push module and Module Forge's own **pierce module cancel each other out**, and
would look broken rather than exclusive. The card text and the log both need to say so.

Second trap: pushes are rate-limited per rigidbody (`CanKnockBack` / `lastKnockbackTimes`), so
rapid fire does not stack shove.

Third: force fights **mass × damping**, which varies wildly (ship 1×0.5, a common Cross 5×6, a Queen
200×4). One number will not feel the same on everything.

### 2. `WarmupTime` — spin-up

`WeaponBase.WarmupTime`, settable, unreached. A module that shortens a minigun's spin-up, or a heavy
module that lengthens it as a downside. Immediately legible in play, and it is the stat behind the
"insane charge time" confusion from R9, so it is well understood.

**Note:** inert on gadget-fired weapons (gadgets bypass `Shooter` entirely) and on held/toggled orbit
rings. Worth a card note.

### 3. `ResourceUsed` — change what the weapon spends

`WeaponBase.ResourceUsed` is a settable `Resource`. A module that converts a Caps gun into an
Electron gun is a genuinely different kind of module from "+10% fire rate" — it changes what your
build is *made of*.

**Trap:** the unit must actually have a tank of the new resource or the weapon silently cannot fire
(`Shooter.Update` gates on `OwnerHasResource`). Needs a build-time warning, and probably pairs with
`ModifyResourceCapacity` on the same module.

---

## TIER 2 — ship and defence stats

**An entire surface with zero coverage.** Not one of the game's 11 effect classes references
`ShipMovement` or `DamagableResource`. This is the conspicuous hole: the game has weapon modules and
resource modules and nothing that changes how the ship *handles*.

### 0. FIRST: build the `Unit.Data` → component bridge

**Do this before any of 4–6.** Neither hook hands you the MonoBehaviour:

- `OnRecalculateUnitStats(Unit.Data)` gets **Data only**.
- `IWeaponModifier.Modify(weaponBase)` gets no `Unit` — and `weaponBase.Owner` is **null** at that
  moment, because `WeaponFactory.Create` runs the modifiers *before* `Equip` sets `Owner`.
- `ModuleEffect.ModifyWeapon(Unit owner, WeaponBase)` **looks** like the answer and is a **dead
  hook** — declared in `ModuleEffect` and called from nowhere in the game.

`Unit` is a `SavableComponent<Unit.Data>`, so a small registry (patch `Unit.Awake`/`SetOwner`, map
`Data → Unit`) is the honest fix, and every item below then becomes cheap. Budget this as its own
piece of work; it is the reason this tier ranks under Tier 1 despite being more exciting.

### 4. Engine — speed and acceleration

`ShipMovement.maxSpeed`, `maxSpeedWhileBoosted`, `acceleration`, `accelerationWhileBoosted` — all
**public fields**.

**Trap:** `MaxSpeed` and `Acceleration` (the capitalised properties) are **get-only** and pick
between the normal and boosted field based on `IsBoosted`. Write the **fields**, and write both
halves of each pair or the module only works while you are not sprinting.

### 5. Dash

`dashCost`, `dashCooldown`, `dashDuration`, `initialDashForce`, `constantDashForce` — all public.
A dash module is a natural, very felt upgrade, and `DashStarted` / `DashEnded` / `BoostStarted` are
public `Action` fields if a module wants to react rather than just re-tune.

### 6. `iFrameDuration` — invulnerability window

`DamagableResource.iFrameDuration`, public. **It is 0 on all 84 damageable prefabs**, so this is
pure upside with no existing balance to preserve: raising it makes you specifically tough against
fast weapons, which is a distinct defensive identity from more health or a shield.

Related on the same component: `damageBlockers` (a `List<GameObject>`; non-empty and active =
immune) is the game's own i-frame switch if a module ever wants true invulnerability windows.

### 7. Boost economy

`fuelCostPerSecond`, `boostedFuelCostPerSecond`, `boostStartDelay`, `boostExtraSpeedLimit`,
`boostExtraSpeedMultiplier`. Lower rank only because it is less immediately felt than 4–6.

---

## TIER 3 — narrower weapon stats

### 8. `AimAssistData` — assist cone and prediction

`WeaponBase.AimAssistData` is settable. 50 of 97 weapons ship it enabled (15° on most, 45° on
rockets). A module widening the cone is real.

**Trap:** the player can disable aim assist entirely in options, so the module becomes a no-op for
those players through no fault of the module. Say so on the card.

### 9. `BarrelLength`

Where shots spawn. Small but real on a shotgun.

### 10. `CellConvertData` — terrain conversion on hit

Settable and unreached. Niche, and worth checking what the stock converters actually do first.

---

## TIER 4 — cosmetic modules

`ShootSfx`, `ContinousShootSfx`, `StartSfx`, `ReleaseSfx`, `WarmupSfx`, `ReloadSfx`,
`MuzzleParticlePrefab`, `ReloadParticlePrefab`, `ShakePreset`, `RumblePreset`.

All settable, none reachable. A "sounds different" module is not a stat, but a **screen-shake**
module is a surprisingly good feel upgrade and costs one field.

---

## WHAT IS LEFT, at a glance (2026-08-25)

**BUILT:** Tier 1 (push / spin-up / ammo), Tier 2 (bridge + 10 ship stats + mercy invincibility),
and from Tier 5 the two headliners — **stealth** (6 triggers) and **last stand**.

**STILL OPEN, in the order I would take them:**

| # | stat | why it is still worth doing |
|---|---|---|
| 13 | `burnProperties` (6 fields) | the ship's OWN fire resistance — no bridge needed, and nothing else in either mod touches it |
| 14 | `HasInfiniteResource` | infinite ammo; wants a trigger rather than being passive |
| 15 | `RefillResources()` | instant top-up on a trigger (on kill, on room clear) |
| 16 | sprint kick — `boostExtraSpeedMultiplier` / `boostExtraSpeedLimit` | a separate, very felt thing from top speed |

| 8 | `AimAssistData` | wider cone / prediction — but the player can disable assist entirely in options |
| 9 | `BarrelLength` | small, real on a shotgun |
| 10 | `CellConvertData` | terrain conversion on hit; niche |
| — | Tier 4 cosmetics | sfx / muzzle prefab / **screen shake**, which is better value than it sounds |

## TIER 5 — found while building Tier 2, and NO BRIDGE NEEDED

These sit directly on `Unit.Data`, which every effect already receives. They were reachable the whole
time; the original sweep missed them because it was looking at `ShipMovement`. Cheapest work on this
page — an `OnRecalculateUnitStats` and one field.

### 11. `IsInvisible` — STEALTH ⭐ the standout

`Vision.Scan` ends with `return !component.ComponentData.IsInvisible`, so an invisible unit is
**literally not seen** by enemy vision. Fully implemented, working, and currently reachable **only
from the debug menu**. That is a whole play-style nobody can access.

Wants a cost or a trigger rather than being permanent — drains a resource while active, or a few
seconds after a dash.

### 12. `IsInvincible` — the last stand

`DamagableResource` line 286: `if (this.IsInvincible && tank.Value <= 0f)` — it **floors the tank at
1** rather than blocking damage. So it is not "immune", it is "cannot die", which is a much more
interesting module and a very different one from the `invuln` window already built.

### 13. `burnProperties` — the ship's OWN fire resistance

A public struct on `Unit.Data` with six fields: `fireThreshold`, `extraBurnLevelWhenCatchingFire`,
`coolingSpeed`, `fireTickRate`, `fireDmgPerTick`, `maxBurnLevel`.

**Distinct from the burn effects Module Forge already has**, which are about the burn you *inflict*
on enemies. This is how fast *you* stop burning and how much it hurts. `DamagableResource.Update`
does `BurnLevel -= burnProperties.coolingSpeed * Time.deltaTime`.

### 14. `HasInfiniteResource` — infinite ammo

Read by `Shooter.OwnerHasResource`. Honest but probably too strong as a plain module; better as a
short buff on some trigger.

### 15. `RefillResources()` — an instant top-up

A public method on `Unit.Data`. Interesting on a trigger (on kill, on room clear) rather than as a
passive.

## TIER 6 — ShipMovement fields Tier 2 did not take

### 16. The sprint kick — `boostExtraSpeedLimit` / `boostExtraSpeedMultiplier`

`StartBoosting` multiplies your current speed by `boostExtraSpeedMultiplier` (3) up to
`boostExtraSpeedLimit` (25) the moment a sprint begins. That burst is a separate, very felt thing
from top speed, and Tier 2 did not touch it.

### 17. `constantDashForce`

Tier 2 took `initialDashForce` (the kick) but not the sustained push during the dash.

---

## CORRECTION to an earlier assumption on this page

**"Give the ship a resource tank it does not have" is NOT a gap** — I nearly listed it.
`Unit.Data.IncreaseCapacity` opens with `if (!resourceTanks.ContainsKey(resource)) { InstallNewTank(...); return; }`,
so the **existing** `ModifyResourceCapacity` effect already creates a missing tank.

That also makes it the clean fix for the Tier 1 ammo-swap trap: pair `"type": "ammo"` with a
`ModifyResourceCapacity` for the same resource on the same module, and the module brings its own
tank rather than relying on the ship already having one.

## DO NOT BUILD — verified dead ends

| thing | why |
|---|---|
| `ShipMovement.boostImpactDamage` | **dead field** — public, serialized, read by nothing |
| `ProjectileImpactBehaviour.destroyVelocityThreshold` | **dead field** — set on 3 weapons, read by nothing |
| `MaxRotationSpeedWhileShooting` | only the **enemy** aimer (`AimAction`) reads it. The player aims through `Aimer`, which has no such clamp — so a player module would do **nothing** |
| `DamagableResource.shieldHasCollider` | **Looks like the best module on this page and is a trap.** All four ships ship `0`, so your shield is an invisible damage filter while an enemy's is a real surface — "make my shield a physical barrier" is the obvious module. But the shield prefab sits on layer **Default**, and the matrix has `EnemyProjectiles` **not** colliding with Default — so it would stop **no enemy fire at all**. Worse, `PlayerProjectiles` **does** collide with Default, so it would block **your own shots**. Verify the matrix before ever revisiting this |
| `Damage` via `ModifyWeaponProperty` | **broken in the game**: its applyer does `Damage.amount + value` where `value` is already `current + delta`, so the base lands twice. Module Forge already blocks it with an explanation. Add damage through explosion / burn / discharge instead |

---

## Suggested first slice

**1 (PushForce) + 2 (WarmupTime)** together: both are one-field `IWeaponModifier` effects, they share
all their plumbing, and PushForce is the one that prompted this list. That is one build, one card
pattern and one test round.

Then **Tier 2 item 0 (the bridge)** as its own piece, because everything in that tier waits on it and
it is the only genuinely unknown-shaped work here.
