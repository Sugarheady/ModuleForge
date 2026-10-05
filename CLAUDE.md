# Module Forge — working notes for Claude (and humans)

Guidance for anyone, human or AI, making changes here. Read it before writing code; most of it is
hard-won and not deducible from the source in this repo.

Target: **PUNK Playtest v0.12.9**, Unity **6000.3.4f1**, .NET Framework **4.7.2**, BepInEx + Harmony.

## What this mod is

A JSON-driven **grid module** framework — the counterpart to Weapon Forge. A module file describes a
card that lives on the ship or weapon grid: its icon, colour, description, power-core shape, and a
list of **effects** that modify the player's weapons or ship while it's placed.

**Design rule that matters:** a bad JSON file must never crash the game. It is skipped, the reason is
logged, and the run continues. Preserve that when adding anything.

## Build & test loop

See **BUILDING.md** — you point one property at your PUNK install; you never edit a `.csproj`.

```bash
msbuild ModuleForge.sln -p:Configuration=Debug
```

**There is no hot reload.** Files are read at startup, so every test is a full game restart.

Logs: **`BepInEx\ModuleForge.log`** (the launch before: `ModuleForge.prev.log`). Since 2026-09-25
`LogOutput.log` and the console keep only the plugin's own load lines plus a copy of every error. The
burn ceiling lives in `BepInEx\config\com.sugarheady.moduleforge.cfg`.

### The log file — `ModuleForgeLog`

A deliberate copy of Weapon Forge's `ForgeLog` (duplicate by default — only the names differ; a fix
to one is a lead in the other two, and Game Mode Forge carries a third).

- **Every log source is `ModuleForgeLog.Source("ModuleForge.X")`, never `Logger.CreateLogSource`.**
  In BepInEx 6.0.0-be.785 `CreateLogSource` is `new ManualLogSource` + `Logger.Sources.Add`, and that
  Add is the only wire to the shared listeners. An unregistered source reaches only this class, which
  writes the file and forwards Error/Fatal. `logfiletest.py` fails on a stray `CreateLogSource`.
- **The plugin's own `Logger` is the one registered source**, and `ModuleForgeLog.Start` is the FIRST
  line of `Awake` so its load lines are copied into the file too.
- **It copies the game's own errors whose stack trace has a `ModuleForge.` frame** — BepInEx leaves
  "Unity Log" out of LogOutput.log by default, so a throw out of one of our patches was in no file.
  The match is a frame START, not a Contains: "GameModeForge." and "ModuleForge." overlap as text.
- **Every failure falls back to LogOutput.log and says so**, and a relaunch is waited for (2s) before
  deciding the game is running twice (`ModuleForge.2.log`).
- `logfileprobe.py` compiles the shipped file against the real `BepInEx.Core.dll` and runs seven
  launch scenarios. `[Logging] OwnLogFile = false` restores the old behaviour.

## Do not commit game code

The findings below came from reading a local decompile of the game's assemblies. **That decompiled
source is not in this repo and must not be added to it** — it's the game's code. Referring to type and
member *names* (as this file does) is what makes collaboration possible; pasting game method bodies in
is not. Keep that line.

## How the code is organised

- Mod-owned effect classes are named `<Thing>ModuleEffect` / `<Thing>Effect`; patches are
  `<Thing>Patch`. Keep the pairing.
- `ModuleBuilder` builds the `ModuleData`; `EffectBuilder` turns a JSON effect entry into a real
  `ModuleEffect`. Add new effects in `EffectBuilder`, not inline.
- `ForgeAssets` resolves game assets by name (sprites, colours, resources).
- `ForgeInterop` is the one-way reflection bridge into Weapon Forge.
- Anything user-facing needs three updates in the *same* change: the C#, the `Module Builder.html`
  block, **and** `HOW TO MAKE MODULES.txt`.

## Engine facts that will cost you days if you don't know them

### Effects and levels

- Every stock effect reads its strength as `effectiveness.GetElement(Module.Level - 1)`.
- **An effect that should answer to levelling needs `OnRecalculateUnitStats`**, not just a `FloatSeries`.
  A BoosterCore changes `Module.Level` with no install/uninstall, so `OnInstalled` never re-runs. The
  chain is: `ModuleInstalled` → `modulesChanged` → next `ModuleGridOwner.OnUpdate` →
  `Unit.RecalculateStats` → `ModuleGrid.OnRecalculateStats` → each effect's `OnRecalculateUnitStats`.
  The level-delta pass runs before it, so `Module.Level` is already current when you read it. Effects
  that push a value into a global accumulator (burn rate, pierce cap) must re-sync there or the boost
  silently does nothing. `BurnRateModuleEffect` and `PierceModuleEffect` are the reference pair.
- **`Module Builder.html` can only express scaling through the per-param "per level" box** (`s:true` on
  the param spec). Adding a new `FloatSeries` param without marking it `s:true` leaves it flat-only from
  the page, which is the same invisible-booster trap from the other direction.
- **`FloatSeries.GetElement(i)` is the formula `baseValue + change * i`**, not an array lookup. A
  series with `change: 0` therefore returns the same value at every level and can *never* respond to
  levelling. A custom effect needs **both** `canBeBoosted` **and** a non-zero `change` to react.
- `Module`'s constructor rolls `PowerLevel = Random.Range(powerLevel.Min, Max)` **per instance**, so
  two drops of the same module differ, and `ModulePickup` shows the rolled value on the ground.

### `OnInstalled` IS NOT GATED ON POWER AND EVERY OTHER SEAM IS

**This is the single most copyable mistake in the mod, because the wrong version works.** The three
seams the game runs modules through all take `cluster.ConnectedAndPoweredModules`:

```
ModuleGrid.OnUpdate            -> ConnectedAndPoweredModules
ModuleGrid.OnRecalculateStats  -> ConnectedAndPoweredModules
WeaponFactory.Create           -> ConnectedAndPoweredModules
```

**`ModuleGrid.Install` does not.** It fires `ModuleInstalled` → `module.OnInstalled(unit)` for any
module dropped anywhere in the grid, powered or not, connected or not. So **twelve** effects here
registered themselves on placement and never asked again: an unpowered kill-reward module still paid
out, an unpowered `ShipStatEffect` still moved the stat, unpowered pierce and burn-rate
contributions still counted in their shared accumulators. Reported 2026-09-11 in one line:
*"The modules in general need to be powered and connected to the grid to work otherwise they dont do
anything."* He is describing the game, and he was right.

**`ModuleForgeLive` is the one owner.** A postfix on **`Unit.Data.RecalculateStats(IModuleGrid)`** —
the game's own "the grid changed" tick, and the only place that is handed both the unit and the grid
— sweeps the effects this mod registered on that unit and tells any whose live-ness changed. Each
effect then runs `Engage` / `Disengage` (or `Push` / `Release`), which are the **same** two methods
its install and uninstall call, so there is exactly one owner per action and nothing new to keep in
step. Five things are load-bearing:

- **Read `ConnectedAndPoweredModules`; do NOT count callbacks.** The tempting version is "an effect
  that got `OnRecalculateUnitStats` this generation is live". It is wrong and would have shipped
  quietly: `ModuleGrid.OnRecalculateStats` has a branch that, for a cluster whose `MainModule` is a
  `SpawnMinionModule`, calls the **main module only and skips every augmentation in that cluster**.
  Every module augmenting a drone gadget would have read as dead while the game ran its `OnUpdate`
  normally.
- **Postfix, not prefix** — the live modules must have re-pushed before the dead ones are withdrawn.
- **Interface types only** (`IModuleGrid` / `IModuleCluster`), or it cannot answer for a minion.
  `SimpleModuleCluster.ConnectedAndPoweredModules` returns everything it holds — no power geometry —
  so a minion's modules are always live, which is right.
- **When it cannot tell, the answer is LIVE.** This gate can only ever take a working module away
  from someone, so every uncertainty resolves to leaving it alone: no grid, no `Module`, a throw, or
  **no cluster anywhere with a main module** (the restore path calls `RecalculateStats` early). That
  last guard is deliberately *not* "the powered set is empty" — a cluster root need not sit in its
  own powered slots, so an empty set is a plausible reading of a real ship and would have argued for
  the wrong answer.
- **`Reset()` belongs in `BurnResetPatch.ResetAll`** for the same reason everything else there does:
  modules are not uninstalled on teardown, so the tracked list would carry a run's worth of dead
  effects into the next one forever. An untracked effect is simply ungated, which is the safe state.

**The corroboration is in the game's own UI, and it is worth knowing about:**
`HoveredModuleInfo` computes `isInstalled` as `grid.Contains(module) && grid.IsPoweredAndConnected(pos)`
and hands it to `GetPropertyList`, and `ModuleGridWidget` calls
`ModuleIconWidget.DisplayState(connected, powered, ...)`. So the game already tells the player twice.
**No card line was added for this** — a stock module says nothing either, and making ours louder than
stock would be the inconsistency, not the fix.

`modlivetest.py` enforces the rule by **derivation rather than by a list**: every `ModuleEffect`
subclass in the mod is classified from its own source, an effect that overrides `OnInstalled` must
implement `ModuleForgeLive.IGated` and Track/Untrack, and one that does not must be pure
`IWeaponModifier` (already gated by the cluster walk — `CellConvertEffect`, `ExtraWeaponStatEffect`,
`WeaponResourceEffect`). **The thirteenth effect is what this test exists for**, not the twelve.

> **A REGISTRATION SEAM AND AN EXECUTION SEAM CAN HAVE DIFFERENT PRECONDITIONS, and the registration
> one is always the laxer.** Same family as the build-time-vs-live split that armed `contactDamage`
> for the whole session in Weapon Forge: "this thing exists" and "this thing should act" are two
> questions, and answering the first is not answering the second.

**The twin was audited and deliberately NOT changed**, which is the rule working rather than being
skipped. Weapon Forge has exactly two `ModuleEffect` subclasses (`ForgeBurnRateEffect`,
`ForgeBurnColorEffect`) in the identical register-in-`OnInstalled` shape — but they ride the **weapon
module itself**, a cluster's MainModule, and `ModuleSlotWeaponHolder.RefreshWeapon` builds the gun
from `cluster.MainModule` with **no power test at all**. Gating them would make a weapon's burn
setting stricter than the weapon. Full reasoning, including the order-dependent edge it would have
opened, is in that repo's CLAUDE.md under "Modules & levels".

### Kills, and ship-vs-weapon scope for free

`Unit.RegisterKill` fires a **public** `KilledAnotherUnit` event and is called from **only three
places** (`DamagableResource.ProjectileCollided` / `OnHitByHitscanWeapon` / `OnExplosion`). Nothing
else credits a kill — not `Die()`, not burn ticks, and not mod-side damage. `ModuleForgeKills` tracks
the *weapon* too, which the game never records: stamped at `Shoot` for projectiles, read off the beam
for hitscan, and taken from the owning projectile for explosions.

> **An effect can tell which grid it is in without a flag.** `WeaponFactory.Create` walks only the
> modules in a **weapon's** cluster when it applies `IWeaponModifier`. So an effect whose `Modify` is
> called is in a weapon grid (and can capture *which* weapon); one whose `Modify` never comes is on the
> ship. `KillRewardEffect` uses exactly that to make "only this weapon's kills" versus "any weapon's
> kills" free. Reuse the trick rather than adding a target field.

> **★ BUT "NEVER CALLED" MEANS "EVERY WEAPON *THIS SHIP* CARRIES", NOT "EVERY WEAPON IN THE GAME"
> — and for six effects it meant the second, until R19.** Crit, execute, grow, leech, slow and
> windup each keep a GLOBAL bucket for a ship-grid module (a null weapon key), and each resolved it
> against whatever weapon had just fired. **An enemy's gun is a weapon.** So a grow module swelled
> enemy bullets to x3.5 (test 243), a windup module made enemies fire faster the longer they held on
> and posted one HUD line per enemy meter — the chirp storm of test 246 — and the four with a VICTIM
> guard ("never touches your own side") were guarding the wrong direction: to an enemy gun the
> player *is* the other side, so an enemy shot could slow you, crit you, leech off you, and with an
> execute module installed finish you under 20% health.
>
> **★ AND THE FLOOD SURVIVED THE SCOPE FIX, because it had a second cause** (R19 246/274, "spamming
> like crazy ... filling the whole left side of my screen", on the player's own guns only). In
> STEPPED mode the fraction reads 0 until the first step, and for that whole stretch `Announce`
> posted a "WOUND UP +0%" line and `Apply` then withdrew and CLEARED it in the same frame - a new
> row and a new chirp every frame, twice per trigger pull. Weapon Forge's `spinUp`, the feature
> this was ported FROM, has the exact guard (`Say` shows nothing at 0) and never flooded.
> **The port kept the shape and dropped the line that made the shape safe** - the same lesson as
> [copying a feature copies its bugs], pointing the other way: copying can also drop its fixes.
>
> **`ModuleForgeScope` is the one owner.** Every effect that can feed a global bucket records its
> owner (`OnInstalled` is handed exactly the unit whose grid holds it — `ModuleGridOwner.Data` passes
> its own `Unit.Data`) and every resolver asks `ModuleForgeScope.Applies(key, weapon)`: is this
> weapon's `Owner.ComponentData` that unit? One reference comparison. Held in a
> `ConditionalWeakTable`, so no run-entry reset can wipe the owner of a module that is still
> installed. An unknown owner, or a weapon with no owner, matches **nobody** — a module that does
> nothing is a bug report, a module firing on enemy guns is a death.
>
> **Weapon Forge had the identical bucket** — `ForgeCrit` / `ForgeLeech` `_global`, which is where a
> ship-grid crit or leech module lands when both mods are installed. It has a mirror,
> `WeaponForge.ForgeModuleScope`, and the two bridges tell it the owner by type name
> (`ForgeInterop.TellWeaponForgeOwner`) before a null-weapon push.
>
> Consequence worth knowing: a drone GADGET's own minions no longer get your ship-grid modules (their
> weapon's owner is the minion, which carries its own grid). Wingman copies still do — they are
> equipped to the ship. `scopetest.py` finds the registries **by shape**, so a seventh global
> bucket fails until it is scoped.

> **AND `"target"` IS PURELY PRESENTATIONAL — it does NOT decide where a module can be placed, which
> is what three of this project's own documents said.** Asked 2026-09-11 whether the on-kill heat
> relief could work on weapon upgrade modules "if you haven't done that already": it already did, and
> the question was fair because the how-to said *"a module attaches to ONE of two places, chosen by
> target"* and the builder page's caption read *"where the module attaches"*. Measured off the assets
> and the decompile rather than reasoned:
>
> - **`ModuleSlotType Normal.compatibleModuleTypes` is `{ Passive, WeaponAugmentation, PowerCore,
>   Booster }`**, and `ModuleGrid.GetSlotType` returns `Normal` for every cell except six
>   (`shipGridPosition`, the two weapon mounts, three ability slots). So both module types fit
>   anywhere an ordinary module fits.
> - **Nothing on the cluster path looks at `moduleType`.** `RefreshConnectedModules` collects by
>   adjacency (`GridHelper.CollectConnectedModulesRecursive`), `RefreshPoweredModules` filters by
>   power, and `WeaponFactory.Create` takes `cluster.ConnectedAndPoweredModules` whole.
>   `ModuleGridWidget` only calls `IsCompatible` for highlighting special slots and for gamepad
>   slot-hunting on `isMain` modules — and **neither Passive nor WeaponAugmentation is `isMain`**.
> - **The two shells are identical apart from `moduleType` and `color`** — same empty `powerCore`,
>   same `powerLevel` 0/0, same `canBeBoosted`.
>
> What `moduleType` actually buys: the word `HoveredModuleInfo` prints at the top of the card
> (`displayName` — **UPGRADES** vs **WEAPON MODS**), the shop section and `orderInShop`, and the icon
> background sprite. Where a module *acts* is the player's choice at the grid, which is exactly what
> makes the scoping trick above work from either target.
>
> **The lesson is not about modules.** A field whose name implies a constraint gets documented as
> enforcing one, and then nobody tests the constraint because the docs settle it. Same shape as the
> `lootFrom` note further down: a written-down limitation stops being read as a claim. When a doc
> says a value restricts something, find the line that does the restricting.

Two traps found building it:

- **`FloatSeries` is a STRUCT**, so `series == null` does not even compile. An unspecified one is
  all-zero, which means a non-zero default has to come from the builder — `EffectBuilder.SeriesOr`.
- **`Resources.FindObjectsOfTypeAll` sees PREFABS; `FindObjectsByType` does not.** This is what made
  `asPickup` fail: the pickup template was learned only from pickups that had already *spawned*, the
  fallback search could not see the eight prefabs the game ships, and the "searched" flag latched on
  the first attempt whether or not it found anything — so one kill before any crate had dropped
  something disabled the feature for the whole run. On top of that a pickup **destroys itself when
  collected**, and a destroyed Unity object compares equal to null, so a cached live one silently went
  null. Cache the prefabs, which are in memory because the drop tables reference them and are never
  destroyed.
- **There are NINE `ResourcePickup` prefabs and two are named nothing like their resource.**
  Fuel→`FuelPickup` (blue orb), Caps (orange orb), Purple (pink orb), White, **Health→
  `ResourcePickup_Red`**, Tech (purple squares), and Money at 10/20/50. **`Resource Electron` is the
  only stock resource with no pickup.** Fuel, Caps and Purple share one orb sprite and differ only by
  `SpriteRenderer.color`, which is why re-tinting the orb is a good fallback — `Resource.color` is a
  `ColorAsset` (public `color` field, implicit cast to `Color`) with `resourceBarUnitColorFull` as a
  backup. **Enumerate by COMPONENT, never by name:** this file twice claimed the game had no Fuel
  pickup, because both lists were built from the `ResourcePickup*` naming pattern. The type scan in
  the code always found it; only the documentation was wrong, in a way no test could catch.
- **AN EARLY-OUT ON "HAVE I GOT ANYTHING YET" IS NOT AN EARLY-OUT ON "HAVE I DONE THE WORK YET", and a
  second writer to the same cache is all it takes to prove it.** The prefab scan above was written and
  then never ran for a year of testing: it guarded itself with
  `if (_template != null && _byResource.Count > 0) return;`, and the `Pickup.Start` postfix — a
  completely separate path filling the same dictionary — makes both halves true the moment any crate
  drops anything. So the cache regressed to exactly the pre-fix behaviour (whatever spawned in the
  room) while the code that fixed it sat right there looking correct. **A flag guarding expensive work
  must be set by that work and by nothing else.** Give it its own bool, and set it only on success so
  a genuine miss retries. Prefabs now also *displace* live instances in the cache rather than merely
  filling gaps — a live pickup is destroyed on collection, carries its own velocity, and is whichever
  one happened to be lying around.
- **A borrowed pickup must say so.** When no prefab matches the resource, the drop wears
  `ResourcePickup_White` (fixed, not arbitrary, so the wrong art is at least consistent) and logs a
  warning naming both. The version without that line reached the author as "the drops are the wrong
  colour" with nothing in the log to explain it.
- **A cosmetic failure must not become a payout failure.** `Drop` used to warn that the resource was
  "being granted directly instead" and then `return` without granting anything. It returns a bool now
  and `KillRewardEffect` falls through to the tank.
- **`Clone()` must list every field.** `buffIndicator` was missing, so a per-module indicator override
  was silently dropped on every real instance — `Module`'s constructor Clones each effect off the
  ModuleData, so the clone is the copy the game actually runs.
- **`Pickup` has `Start`, not `Awake`**, and `ResourcePickup` declares no lifecycle method at all.
  Patching `ResourcePickup.Awake` makes Harmony **throw at `PatchAll`** and takes the whole mod down.
  Patch `Pickup.Start` and cast. `ResourcePickup.resource` / `amount` / `pickupSfx` are all public, so
  cloning one the game already spawned is the cheapest way to drop a custom pickup.

### A warning-only read can kill the whole effect

**Validation code must be the most defensive code in the function, not the least.** Three lines in
`BuildKillReward` read `clearBurn` / `buffFireRate` / `buffDamage` with a plain `(float?)` cast
*purely to decide whether to log a warning*. Every series-capable param can arrive as
`{baseValue, increaseMethod, change}` — the builder page emits that object the moment a "per level"
box is filled — and casting it to `float?` throws `Can not convert Object to Single`. The effect was
abandoned, the module built with **zero effects**, and so there was no payout, no pickup, no buff
**and no card line either**. It read as four separate bugs and cost five failed tests, while the real
assignments four lines below were all correct and never ran.

Every scalar read here now goes through **`Flat` / `FlatInt` / `FlatBool`**, which take a number as
itself and a series as its `baseValue` and fall back on anything unexpected. Not just the three that
broke — the identical latent crash was sitting in `ModifyWeaponProperty`'s Multiply warning.

**This is the second round lost to the mod rejecting JSON its own builder page wrote** (the first was
the missing `killrewardeffect` class name). After touching either side, check that no shape the page
can emit reaches a cast that throws — *including in code that only logs*. Series-capable params as of
2026-08-20: amount, buffDamage, buffFireRate, burn, chance, clearBurn, damageAmount, damageIncrement,
falloff, pierceCap, ticksPerSecond, value.

### Diagnostics have to survive a second run, and this mod's did not

Four log gates here (`_saidLookClash` twice, `_saidNoImpact`, `_warned`) are one-shot bools, so a
warning already said this session stays silent for the rest of it — and the thing each one
describes is per RUN. `ModuleForgeDiagnosticGates` sweeps them by naming convention at both run
entry points, a deliberate copy of Weapon Forge's version for the same reason as the buff HUD and
the unit tint: each mod must work with the other absent, and resetting your own diagnostics is not
a decoder worth a reflection bridge.

Two details carried across from that copy rather than rediscovered:

- **`HashSet<T>` does not implement the non-generic `System.Collections.ICollection`.** Weapon
  Forge's first cut filtered on exactly that and silently dropped every keyed gate while reporting
  a plausible total. Test the capability you are about to use — a public parameterless
  `Clear()` — which is also the method the reset then calls, so acceptance and action cannot
  disagree.
- **The log line prints the split, not one total**, because a count is only evidence when it can be
  checked against a count derived another way.

**And `BurnResetPatch` held its reset list TWICE, byte for byte, one copy per entry point** —
the same shape Weapon Forge deduplicated a week earlier and nobody thought to check here. Anything
added to a duplicated list is one paste from being half-wired, and a half-wired reset fails only on
the entry point nobody tested. One `ResetAll()` now, in both mods.

> **Auditing one mod and not its twin is how a fix ends up half applied.** These two repos
> deliberately duplicate rather than depend on each other, which means a bug found in one is a
> lead to check in the other — not a bug that was only ever in one place.

### Plugin load order — this mod loads FIRST

BepInEx loads Module Forge **before** Weapon Forge, so at `Awake` time Weapon Forge's types genuinely
do not exist yet. Any cross-mod check has to be **deferred** (the first `ShipHud.AssignShip` bind is a
good point), and it must look the type up **silently** — `AccessTools.TypeByName` logs a warning when
the type is absent, and "the other mod is not installed" is a completely normal state. Scan
`AppDomain.CurrentDomain.GetAssemblies()` instead.

### Execute stacking, and why the common case stays one pass (R20 close)

His design, 2026-09-29: *"it has the base 20% ... and a second value option that increases that
cap"*. `stackPercent` / `stackFlat` are added for every copy of the **same module file** past the
first (`ModuleForgeExecute.Groups`: group = `Module.Data.name`, base = the strongest copy, the
tightest boss guard wins, percent clamped to 1). Different files still answer "strongest wins".
**`ShouldFinish` runs on every damage call in the game**, so while no installed contribution
stacks (`_anyStacks`, recomputed in `Refresh`) it takes the old allocation-free loop; only a
stacking install pays for grouping. The card shows the stack being carried (`CopiesOn`).

### Windup says why it stopped (R20 246)

*"never gets to 100%"* could not be sourced — the meter reaches 1 after `windUpTime` of held
trigger. So `ModuleForgeWound` logs, once per gun per run, why a held trigger stopped winding (the
gun could not pay for its next shot, or the Shooter was blocked) and when a wind-up reaches FULL.
His other complaint, the 33% jumps, was **my test file**: R19_Windup.json used `"ramp": "stepped"`.
The default is smooth.

### The status feed is not an upsert

`ShipLogOutput.Log(id, ...)` looks like "replace the line with this id". **It is not, and the idiom
`Clear(id); Log(id, ...)` floods the screen.** Three parts:

- **`Log` always APPENDS** a new `ShipLogEntry`. There is no replace-by-id anywhere in the class.
- **`ShipLogDisplay.Show` then `Instantiate`s a fresh `LogEntry` row** *and* plays `newMessageSfx`.
  Every log call is a new row and a new chirp.
- **`Clear` does not remove the row promptly** — it fires `LogRemoved` → `LogEntry.Hide()`, which is
  **`async void`**: a close animation, destroyed later.

So any text that changes often (a stack count, a seconds countdown) stacks rows on top of ones still
fading out. That is exactly how the buff indicator came to cover the left of the screen and chirp
continuously in a group fight.

**Rewrite the live row's text in place instead.** `ShipHud.logDisplay` is public; only
`ShipLogDisplay.logEntries` (`Dictionary<ShipLogEntry, LogEntry>`) is private. `ShipLogEntry` is a
**class**, so it keys by reference — and `Log` does not hand it back, so subscribe to
`LogOutput.LogAdded` and cache `id → ShipLogEntry` yourself. Wrap the text in
`<uppercase>…</uppercase>`, which is what `LogEntry.Display` does. Keep a hard line cap anyway: the
status area sits beside the ammo / fuel / health readouts.

**Related UI trap, same feature:** a borrowed prefab brings its own `RectTransform` size, and a
`HorizontalLayoutGroup` with `childControlWidth/Height` **false** (the default) ignores a child's
`LayoutElement` entirely — so there is no sizing at all, not merely bad sizing. Set `childControl*`
true *and* add a `LayoutElement`.

### The level-boost system (green crates)

Green crates always drop one `Module BoosterCore` as a **fixed** item, not a weighted roll. Boosting
works by a pixel-mask **`LevelModificationField`** grid: each overlap with an adjacent module adds
+1 to its level, gated on that module's `canBeBoosted`. Because it's a mask, a booster can be placed
to level several neighbours — or the same neighbour multiple times.

### `ModifyWeaponProperty` — the richest stock effect

`targetProperty` covers FireRate, BurstSize, BurstDelay, ProjectileCount, Spread, AngleVariance,
AngleOffset, KnockbackForce, Cost, Range, Speed and Damage. `operation` is Add or Multiply;
`deltaCalculationMode` is Constant, FromOriginal or FromCurrent.

> **Damage is deliberately not exposed.** It genuinely misbehaves in the base game: eleven appliers
> *assign* the value while Damage *adds*, so it compounds. A stale comment in this repo once claimed
> "Multiply is unimplemented" — that was wrong; Multiply works, Damage is the broken one. Don't
> re-enable it without fixing the underlying interaction.

### Module cards

Per-effect description lines are rendered from `IHasDescriptionForUnit` / `IHasDescriptionForWeapon`
implementations — no patch is needed to get a line, you just implement the interface. **Lines only
render for a module sitting on the ship grid**; one in the shop list shows just name and description.
That's stock behaviour for every module, not a mod bug — don't go hunting for it.

> **`HoveredModuleInfo` calls BOTH interfaces if an effect implements both, so implementing both
> prints every line twice.** Pick one:
>
> - **`IHasDescriptionForUnit`** — always called, in either grid, and handed the `Unit`. **This is the
>   right choice for a `"both"`-group effect** (`KillRewardEffect` and `CritEffect` both use it).
> - **`IHasDescriptionForWeapon`** — handed a `WeaponBase`, but **it is not the "am I on a weapon?"
>   test it looks like**: for a module connected to neither weapon it falls back to
>   `ship.PrimaryWeapon` anyway, so a ship-grid module gets the call too. Use it only for an effect
>   that is weapon-grid-only *and* needs the weapon's numbers, like `ExtraWeaponStatEffect`.
>
> The real "which grid am I in?" test is whether `IWeaponModifier.Modify` was ever called — see the
> kills note above.

### Sprites and icons

- Stock icons are `HUD_GridTiles_0`..`_44` and `HUD_Modules_0`..`_35`, tinted by `module.color` — in
  *both* the places an icon is drawn. **They are NOT zero-padded**, and writing `HUD_GridTiles_NN`
  (which five places across the two repos did) teaches a two-digit form that resolves to nothing.
  R17's log caught it: `Icon 'HUD_GridTiles_07' is neither a game sprite nor ...`, printed by a
  warning that then restated the convention which caused it.
  > **A diagnostic that restates a convention is teaching it.** If the convention is wrong, the
  > warning is not the safety net — it is the bug, in the most authoritative voice the mod has.
  `ForgeAssets.PaddedRetry` (and `ForgeSpriteLibrary.PaddedIconRetry`, its twin) accept the padded
  form after the exact name misses, resolve it, and name the real asset.
  > **⚠ AND UNTIL R20'S CLOSE THIS ONE NEVER RAN.** `StockIcon` returned null for any name with no
  > `:` BEFORE reaching `PaddedRetry` — so a plain `HUD_GridTiles_07`, the exact case the retry was
  > written for, still warned on every launch (R20's log). The Weapon Forge twin was right. Fixed
  > 2026-09-29; `iconwindupr20test.py` pins the branch. **Read what RETURNS above your line.**
- This mod has **no sprite import pipeline of its own** — that lives in Weapon Forge
  (`ForgeSpriteLibrary`). `ForgeAssets.ResolveIcon` tries the game's assets, then borrows Weapon Forge's
  loader through `ForgeInterop.TryResolveIcon`, so one `sprites` folder serves both mods. Stock name
  wins a clash. Without Weapon Forge installed, only stock names resolve.
- **An icon is drawn by two renderers that disagree about size.** The card (grid/shop) is a UI `Image`,
  72×72, Simple, `preserveAspect` off — it stretches art and ignores pixels-per-unit entirely. The
  ground pickup is a world `SpriteRenderer` where pixels-per-unit is the *only* thing setting the size.
  Stock icons are **24×24 at PPU 40** (0.6 units) — *not* the 20 the rest of the game's art uses, so
  art imported at the projectile default comes out double size on the ground while looking perfect on
  the card. `ResolveIconSprite` rebuilds to the stock footprint to hide this.
- **An icon name takes the `:frames` suffix, and `ForgeAssets.StockIcon` is why.** `ResolveIcon` used
  to hand the whole string to `FindAsset`, so `"HUD_GridTiles_4:2"` matched no asset and fell through
  to the "not a sprite in the game" warning — loud, but with the suffix as an unstated reason. It now
  tries the exact name **first** (so nothing already written changes, and an unsuffixed icon never pays
  for a run scan) and only then splits and resolves a frame. The custom half comes free from Weapon
  Forge: `ResolveIconSprite` is spec-aware there and the signature is unchanged.
  **An icon still cannot animate** — the widget assigns a sprite and nothing ever ticks it — so `:4` is
  a *picker*, and `@fps` is dropped here rather than warned about (the `@` warning in
  `ResolveSpriteFrames` points at `orbFps`, which would be wrong advice for an icon).
- **Power-core shapes are parsed from sprite pixels** into a boolean grid, randomly mirrored and
  rotated. Width and height must **both be odd** or the game logs "Power core has invalid size".

### Other

- The game runs at **20 pixels per world unit**; stock projectile art is ~7×7 px.
- Burn is **plain Unity audio/particles**, and the burn engine here patches `DamagableResource.Update`
  plus the burn-particle emit methods. Tick rate is hard-capped so stacking can't run away.
### Loot

`LootDropper` → `DropTable` → `DropTableWeightedGroup`. There is no global "droppable" flag: a module
drops only if it is in a group that a crate's table references, so `ModuleLootPatch` injects into those
groups from a `LootSelector.SelectLoot` prefix (hooking the roll guarantees the groups are loaded).

- **`useGroup` stays serialized even when false**, so many tables point at a pool they never draw
  from — the Level 2 crate, every Box, ten enemy tables. Injecting into those does nothing while the
  log claims success. Check the flag; this mod shipped without that check and lied about it.
- Only **8 of 64** tables can drop a module and **5 pools** are ever rolled. Pool size decides real
  odds: **Tech ~5** stock entries vs **White ~18**, so `lootFrom: "tech"` is the best-odds choice.
- **Money and Level 2 roll no module at all.** `lootFrom: "money"` / `"level2"` *grafts* one on by
  appending a new `DropTableItem` — additive, so the crate keeps its normal contents. Grafting is
  explicit opt-in only; `"all"` must never trigger it, or any loot module would silently rewrite two
  stock crates for the whole run.
- **A CONFIRMATION PRINTED AFTER A REFUSAL READS AS THE REFUSAL BEING OVERRULED.** Straight out of
  his R15 log:
  ```
  [Warning] PiercingTest1.json: "lootFrom" was set but this module is not loot-enabled, so it can
            never drop.
  [Info   ] PiercingTest1.json: drops only from DropGroup Modules Crate Caps
  ```
  `ResolveLootPools` warned and then carried on to print its summary unconditionally. The second
  line is the more confident of the two and it is the one a reader believes - so he came back asking
  whether crate targeting had ever been built, having tested it against a file the mod had already
  refused. The summary says which of the two states the file is in now. **Weapon Forge had the
  identical code and the identical bug**, which is the duplicate-by-design rule working as intended:
  a bug found in one is a lead in the other.
- `DropTableItem` is a **struct with private `[SerializeField]` fields** — box it, fill by reflection,
  unbox into the list.
- Every module zeroes its own drop weight once you own one (`repeatedDropChanceMultiplyer`, 0 on ~120
  of ~145 stock modules), so duplicates need the explicit `lootRepeat` override. It edits our own
  clone, so it can never make a stock module repeat.

## Platform gotchas (.NET 4.7.2 against a Unity 6 game)

`Gradient.SetKeys`, `Texture2D.LoadImage` and `AudioClip.SetData` all bind to the `ReadOnlySpan<T>`
overloads, which this target framework can't satisfy. Use the array-taking setters, or resolve the
method by reflection.

Harmony's `FieldRefAccess<T,F>` **throws on a value-type mismatch** — e.g. a field typed `LayerMask`
is not an `int`. Use a plain `FieldInfo` when unsure.

## `Module Builder.html` conventions

One self-contained page, no build step, no network. Fields carry a **`data-path`** folded into nested
JSON by `deepSet`.

> **The effect list filters on `group === target`**, so an effect valid on *both* grids needs the
> `"both"` group and the `fitsTarget` helper — a group value the filter does not recognise puts the
> effect in **neither** dropdown, silently. That is how `KillRewardEffect` first shipped.

> **A DERIVED LIST IS ONLY RIGHT IF IT IS DERIVED FROM THE SAME SOURCE THE DECISION IS MADE FROM.**
> `EffectBuilder.KnownEffects()` builds the "the effect types available are..." message by scanning
> for `ModuleEffect` subclasses instead of hand-listing them, on the reasoning that a hand-written
> list would drift. Sound, and it advertised **five names the switch would reject** - found
> 2026-09-08 by reading the message in a real log, not by any test:
> - **Three were the house rule below simply not kept.** `BurnRateModuleEffect`,
>   `PhasingModuleEffect` and `PierceModuleEffect` are reached by the friendlier `burnrate` /
>   `phasing` / `piercecap`, and nobody went back for the canonical spelling - so the comment
>   asserting "a class name is always an accepted spelling" was false for exactly the three effects
>   whose class name nothing else uses.
> - **Two were ANOTHER MOD'S.** It walked `AppDomain.CurrentDomain.GetAssemblies()`, so with Weapon
>   Forge installed it listed `ForgeBurnColorEffect` and `ForgeBurnRateEffect` - real `ModuleEffect`
>   subclasses this switch has never heard of.
>
> **And scoping it to this mod's assembly was worse than the bug.** That was the first fix and a
> test caught it: **most of the effect types the switch builds are the GAME's own `ModuleEffect`
> subclasses** (`AddBurnEffect`, `ModifyWeaponProperty`, `AddShieldEffect` and nine more), so a
> this-assembly-only scan silently dropped twelve legitimate names from the very message that exists
> to list them. The boundary is **two** assemblies: the game's, identified as the one declaring
> `ModuleEffect` itself rather than by name, and this one. `effectnamestest.py` pins all of it -
> every class name resolves, the page's keys all resolve, and the scan reaches both assemblies and
> no others.

> **Every `case` in `EffectBuilder`'s switch must include the effect's own CLASS NAME, lowercased.**
> The page emits `"type": "KillRewardEffect"` — the class name — and the switch accepted only
> `killreward` / `onkill` / `resourceonkill`, so **the mod rejected JSON its own builder page had
> written**: `unknown effect type 'KillRewardEffect' - skipped`, module built with zero effects, and
> (because an effect that does not exist has nothing to describe) **no stats on its card either**, which
> made it look like two separate bugs. Every other case already listed its class name; this was the one
> omission. After adding or renaming an effect, diff the page's keys against the switch —
> page: `^\s*"([A-Za-z]+)":\s*\{\s*group:` in `Module Builder.html`; code: `case "([a-z0-9]+)":` in
> `EffectBuilder.cs`. All 25 line up as of 2026-08-28.

> **A new stat is not finished until it shows on the module card in game** — a standing request from
> the mod's author, and a fair one: the card is the only place a player can learn what a module does.
> That makes four updates, not three: C#, page, txt, **and** a `DisplayableProperty` line from
> `GetPropertyList`.

> **A `data-path` that is a prefix of another one throws**, and the exception escapes `build()`, so the
> page silently emits *nothing* and drops every field after it. Sweep all paths for prefix collisions
> after any change.

Round-trip matters: anything folded in `build()` must be unfolded on load. Booleans need explicit
`data-type="bool"` handling — the page had none at all until it was added.

**"Preset" means a one-click entry in this page** that fills the whole form — not an example `.json`.

## Cross-mod rules (Weapon Forge)

The two mods cooperate but must stay independently buildable and runnable:

- **Find each other by type name only** — `AccessTools.TypeByName("WeaponForge.ForgePierceCap")`.
  Never an assembly reference, never `BepInDependency`, never the plugin GUID.
- Exactly one owner per shared mechanic. When both are installed **Module Forge owns the burn engine
  and the pierce authority**, and Weapon Forge stands down; pierce caps add into one number and
  stat-card lines merge rather than double-report.
- **CRIT owns the other way round, and that is deliberate rather than inconsistent: the mod that owns
  a mechanic keeps it.** Crit's engine lives in Weapon Forge (per-weapon `crit` blocks from the weapon
  files), so when both mods are installed **Weapon Forge owns the crit patch** and
  `ModuleForgeCritPatch` early-returns (`ModuleForgeCritCompat.OwnsPatches`), while `CritEffect` pushes
  its chance and multiplier into `WeaponForge.ForgeCrit.SetBonus` by reflection. Two prefixes on
  `DamagableResource.Damage` would each roll their own dice on the same hit — roughly double the crit
  rate the player asked for, and two impact bursts on top of each other. Three things follow:
  - **Only the NUMBERS cross the bridge.** Roll mode and the four cues belong to whichever engine is
    running, so a module that sets them is announced once (`ModuleForgeCritCompat.Announce`) rather
    than looking broken to its author.
  - **`SetBonus` takes a NULLABLE weapon**, because a ship-grid module never gets
    `IWeaponModifier.Modify` and so has no `WeaponBase` to key on. Without that channel a ship-grid
    crit module would have been silently inert.
  - **The stat-card line is ONE number, not a sum of two halves like PIERCE.** With both mods the
    module contribution is already inside Weapon Forge's total, so `ForgeInterop.WeaponCritTotal`
    returns the whole thing and `WeaponStatsPatch` only falls back to `ModuleForgeCrit` when that mod
    is absent. Summing both would double-count.
- **LEECH owns the same way as crit, and its cap is the interesting part.** Weapon Forge holds the
  per-weapon `leech` blocks, so it owns the patch; `ModuleForgeLeechPatch` early-returns and
  `LeechEffect` pushes into `WeaponForge.ForgeLeech.SetBonus`. Two prefix/postfix pairs would each
  measure the same hit and each pay out — double healing and two orbs. What differs from crit:
  - **The CAP and the PER-ENEMY DELAY cross the bridge, not just the payout numbers**, and the
    resolution is deliberately asymmetric: the **tightest cap** and the **longest delay** win. A cap
    is a safety limit, so a module that ships one must protect the player even on a weapon whose
    author left it uncapped. Letting a generous module *loosen* a weapon's tight cap would let a
    module quietly create the immortality that author had ruled out.
  - **A module-granted leech on a weapon with no `leech` block gets a DEFAULT CAP** (`ForgeLeech`'s
    `GrantedCapPerSecond`). A weapon author who writes `maxPerSecond: 0` has been warned by their own
    build log; a player slotting a module onto an arbitrary gun has seen no warning at all — and on
    the ship grid the module reaches every weapon they carry, including a beam.
  - **`ForgeLeech.Resolve` hands the cap back as an OUT PARAM rather than writing it onto the Config.**
    A Config belongs to a `WeaponData` (a template) while contributions are keyed to a live
    `WeaponBase`, so two weapons sharing one template with different modules would overwrite each
    other's cap on every hit. Same reason `GrantedFor` clones per weapon instead of sharing one
    `Defaults` — a Config carries per-weapon runtime state (the budget, the per-enemy timers).
- **OVERHEAT IS WEAPON FORGE'S, AND `coolHeat` DOES NOT GROW A LOCAL FALLBACK.** His R15 ask was
  *"can you add the onkill effect for moduleforge to cool for overheat feature? maybe add a check to
  make sure both mods are in-place before implementing it?"* - so `KillRewardEffect.coolHeat` reaches
  `WeaponForge.ForgeHeat.CoolOn(unit, weapon, amount)` by reflection and does nothing without it.
  The check is **structural rather than a flag**: `overheat` is a per-weapon block from a Weapon
  Forge weapon *file*, so with that mod absent there is no meter in existence to act on. Crit and
  leech duplicate here precisely because a module must still work alone; this one must not, because
  duplicating it would mean two engines owning one meter and there is no standalone case to serve.
  Two details:
  - **The lookup latches on SUCCESS only.** This mod loads BEFORE Weapon Forge, so "the type is not
    there" has a different answer depending on when it is asked, and caching the first answer
    forever is exactly the trap the load-order note above exists for. A failure is retried once per
    run (`ForgeInterop.ResetHeatLookup` from `BurnResetPatch.ResetAll`), so it costs one assembly
    scan per run rather than one per kill. Module building runs from a game-side startup postfix,
    long after the chainloader has finished - which makes the first answer already right, and makes
    that a happy accident rather than something to depend on.
  - **It is said at BUILD time and again on the first kill.** The build-time line can name the file;
    the runtime one covers "Weapon Forge is here but this gun has no overheat block", which is not
    knowable from the module.
- **`ForgeAssets.ResolveSpriteFrames` is STOCK-ONLY on purpose**, unlike everything else art-shaped
  here. Its one caller is the standalone leech orb, which by definition only runs with Weapon Forge
  absent — so reaching for the borrowed loader would be dead code, and `TryResolveIcon` rebuilds at the
  module-icon footprint (24x24 at PPU 40), the wrong size for anything that is not an icon.
- **`ModuleForgeUnitTint` is a deliberate copy of `ForgeUnitTint`**, same call as the buff HUD: a
  display layer is not a decoder worth a dependency, and the crit tint has to work with the other mod
  absent.
  **And the shared claim that `replace` "brightens as readily as it darkens" was wrong in both
  copies' docs.** `SpriteRenderer.color` is itself a *vertex multiply in the shader*, so the pixel is
  always `texture x colour` and the most any tint can do is `texel x yours` - in either mode.
  `Unit Fly Regular`'s body art averages **40/255 luminance** against 76 for its beak (same shared
  `SpriteLitAA` material), which is exactly why a flash reads on the mouth and barely on the body.
  What `replace` buys is the layer arithmetic, not a higher ceiling. Corrected in all four docs;
  the honest advice is a saturated colour or `colorPulse`, since motion is visible at any brightness.
  The two are never live at once (Weapon Forge owns the crit patch when present), so they
  cannot fight. Keep the two scoping rules identical in both — they encode which renderers a unit's
  prefab means to be recoloured, which is a fact about the **game**, not about either mod.
- This mod **borrows Weapon Forge's audio pipeline** when present, so there is one `sounds` folder and
  one set of names across both mods. Don't duplicate the decoder here. Same for the `sprites` folder
  behind custom icons.
- **But resolving one of the GAME's own sound NAMES is native here, and the line is the same one
  `ResolveIcon` and `ResolveColor` already draw:** custom-*file* loading is Weapon Forge's pipeline
  and is borrowed; looking a name up in an asset the **game** owns is not a decoder and has to work
  standalone. `Sfx` carries both a `guid` and a readable hierarchical `name` (`Cells/Fuel`,
  `UI/OK`), and until 2026-09-02 **both mods matched on `guid` only** - so all 259 of the game's
  sounds were reachable solely by typing a 36-character guid, with nothing anywhere mapping a name to
  one. `ForgeAssets.StockSound` closes it; **SOUNDS.txt** in both repos is the generated list. Keep it
  in step with `WeaponForge.ForgeSfxRegistry.StockGuid`, including the empty-entry warning: **70 of
  the 259 are declared with no clip at all** and resolve perfectly while playing nothing.
  This is the only reason the csproj references `UnityEngine.AudioModule` - reading
  `Sfx.audioClips.Items.Count` needs `AudioClip` to be a known type.
- **The buff indicator is duplicated too, for the same reason as loot.**
  `ModuleForgeBuffHud` is a deliberate copy of `ForgeBuffHud` — a display layer is not a decoder worth
  a dependency, and each mod must indicate its own buffs with the other absent. They coordinate through
  **two** shared constants: the icon-row GameObject name `Forge Buff Indicators` (found by
  `parent.Find`, so whichever mod builds it first wins and the other joins it — one row, not two), and
  **disjoint log-id blocks** — Weapon Forge owns 9100–9159, Module Forge 9200–9259, and the game itself
  uses 0–5. Disjoint ids are what let a weapon buffed by both sources show two lines, one per source,
  instead of the mods clearing each other's entries. Settings are deliberately *independent* (either
  mod alone must be configurable); `WarnOnIndicatorMismatch` logs once at startup if the two configs
  disagree, read by type name only.
- **Loot is the opposite call: duplicated, not borrowed.** `ModuleLootPools` is a deliberate copy of
  `ForgeLootPools` — a table of constant asset names isn't worth a dependency, and each mod must drop
  loot with the other absent. The two coordinate through **one shared string**: the created Money pool
  is named `Forge Modules Crate Money` in *both* mods, so `FindObjectsOfTypeAll` finds whichever
  instance exists and they fill one pool. Both also check `TableAlreadyRolls` before appending a graft,
  or a player with both mods gets two module rolls out of every Money crate. Rename that constant in
  one mod only and you reintroduce exactly that bug.
- **WEAPON TUNING (fire rate and damage) is owned by Weapon Forge when present, and this was a live
  bug rather than a precaution.** `ModuleForgeKillBuff` multiplied `WeaponBase.FireRate` and
  `Damage.amount` from its own captured baseline, re-capturing whenever it noticed the weapon had
  moved underneath it. That heuristic works against another one-shot writer and **fails completely
  against a per-frame one** — and Weapon Forge has two: `overheat` with `response: slow`, and now
  `spinUp`. Against either, "something else moved it" is true on every frame, so this mod's write was
  overwritten immediately *and* the re-capture adopted the other mod's modified number as the
  weapon's own baseline. The result is order-dependent and ratchets rather than merely failing; both
  mods log exactly what they intended. `ModuleForgeWeaponStats` publishes into
  `WeaponForge.ForgeWeaponStats` when it is there and keeps an identical local table when it is not.
  Two details worth keeping:
  - **The stat IDs are read out of the other mod by NAME, never assumed.** Both declare
    `FireRate` / `Damage` as `const int` and they agree today; hard-coding that agreement across a
    reflection boundary is exactly the silent fallback this project keeps paying for — a renumber
    there would route damage into the fire rate with nothing to read in either log. The bridge
    stands down if either constant is missing.
  - **A bonus of 0 WITHDRAWS rather than publishing a multiplier of 1**, so the owner forgets its
    captured base and a legitimate augmentation is picked up rather than pinned.
- **`ModuleForgeKills.Attribute(Component, WeaponBase)` is a public entry point for the other mod.**
  `StampShot` only records inside a `BeginShoot`/`EndShoot` bracket, which is right for every shot the
  game fires and no use for one born mid-flight. Weapon Forge creates those (split fragments, chain
  jumps, rallied reflects, catalyst copies) and calls this by type name. Without it a
  `KillRewardEffect` scoped to a weapon never matched such a kill and paid nothing.
- If the reflection fails, degrade to doing nothing — never risk two mods fighting over the same patch.

## House rules

- Verify against the decompile before building. Nearly every bug in this project's history was a wrong
  assumption about the engine, not a wrong line of C#.
- Check whether a game field already does the job before writing code.
- Assume any class instance you hand over is kept by reference — assign a fresh one rather than mutating
  a shared stock object.


### Buying a Forge MODULE with INGREDIENTS, not just money

Built 2026-09-20, both mods, R19 tests 269-270. **The entire mechanism was already in the game,
including the UI** - this change only stops flattening a list that was always a list.

- **`ShopItemConfig.price` is a `List<Price>`**, and a `Price` is either an `Ingredient` (held in the
  run's `Vault`) or a `Resource` (a tank on the ship). `Shop` deducts EVERY entry on purchase and
  `CanAfford` requires all of them.
- **`priceIncrement` is a second list**, and `ShopItem.IncreasePrice` matches each increment to the
  price of the **same currency** - so escalation is per-currency for free.
- **`ShopItemWidget` already does `foreach (Price price in shopItem.price)`** and instantiates one
  `PriceWidget` each, which picks `ingredient.iconSmall` or `resource.icon` and reddens the number
  when you cannot afford it. **A multi-currency price renders correctly with no UI work**, which is
  normally the expensive half.
- **`unlockRequirements` is a `List<Ingredient>`** that `RunData` reads as an "ever owned" gate -
  the item is not in the shop at all until you have held those. Seven stock items use it.

Keys: `shopCost` / `shopCostIncrement` (objects keyed by currency name) and `shopUnlock` (a name or
a list), in both mods. `shopPrice` is unchanged and ingredients stack ON TOP of it, which is the
stock convention: **all 62 entries in the game's own shop table carry a money price**, and 25 of
them carry ingredients as well.

> **⚠ AN AMOUNT BELOW 1 IS A CRASH, NOT A NO-OP, AND THE TWO HALVES OF THE GAME DISAGREE ABOUT IT.**
> `Price.CanAfford` asks `unit.GetResource(r)`, which returns **0** for a resource the ship has no
> tank for. `Shop`'s purchase line is `this.ship.Unit.GetTank(price.resource).Value -= amount` with
> **no null check**, and `Unit.Data.GetTank` is `resourceTanks.GetValueOrDefault(resource)`. Those
> disagree in exactly one place: an amount of 0 makes `0 >= 0` true, the check passes, and the buy
> dereferences null **inside the game's own Shop**. `Price.AmountFloored` is `FloorToInt`, so 0.5
> arrives as 0 too. Both mods refuse a floored amount below 1.
> **A check and its action can disagree, and the gap is where the crash lives** - the same asymmetry
> as `DamagableResource.Damage` guarding with `HasTank` while the `.Tank` property does not.

> **★ THE ID AND THE NAME ON SCREEN DIFFER FOR TWO OF THE NINE INGREDIENTS**, and they are the two a
> person is most likely to type: **`Coral` displays as "Fiber"** and **`Shell` as "Generator"**. The
> lookup takes id, `displayName` and asset name. Same family as the 259 sounds reachable only by
> GUID: **a lookup that cannot see the name on screen fails for the only spelling anyone will try.**

**Four of the nine are not in circulation.** Measured by reference count across the exported assets:
the five the stock shop trades in (Chip, Coral, Gland, Powerstar, Shell) are each referenced 3-17
times, while **Bond, Ex and Face are referenced once - their own registry - and Strange Ball not at
all.** A price in one can never be paid, so it is **warned about and kept** rather than refused (a
game update could start dropping them, and this project reports a questionable file rather than
silently dropping it).

**⚠ AN INCREMENT NEEDS AN ITEM THAT COMES BACK.** `Shop` removes a bought item from the list unless
`ModuleData.repeatInShop` is set, and `IncreasePrice` runs on what is left - so a rising price on a
one-shot item is a number nothing ever reads, printed in the build log as though it applied. Both
mods now turn repeat on when any increment is asked for. **Module Forge had this since it shipped
and it asked about the MONEY increment only** - correct for the only escalation that existed when it
was written, and a silent no-op the moment an ingredient could escalate too.

**Weapon Forge gained `shopPriceIncrement` in the same change.** It had been writing a hard-coded
`amount = 0f`, so a Forge WEAPON could never get more expensive while a Forge MODULE could. Found by
reading the twin, per duplicate-by-default.

**Two stock-game oddities found in passing and not acted on:** `SimpleModuleGrid.RestoreFromMemento`
restores `Active3` from `memento.active2`, and `ShopItemsConfig` pulls its CSV from a published
Google Sheets URL at runtime. (`SimpleModuleGrid` is the ENEMY/minion grid, not the player's.)

**R19's close (2026-09-27), both shop halves:**

- **`config.Get(id)` answers NULL for an item added this pass** - `ShopItemsConfig`'s dictionary is
  rebuilt by `config.Initialize()` only after the loop. So every Forge module's summary line said
  "for free" and the "hidden until you have found" half never printed. `EnsureConfig` hands the
  config back now. Weapon Forge had the identical read and paid more for it (its repeat-in-shop
  switch hung off the same null, so TOLLGATE vanished after one purchase). Duplicate-by-default
  working as intended: one bug, found once, fixed twice.
- **A gated item gets the price and NOT the pool** (270, STARGATE CORE on sale at the first station
  with no Powerstar). `RunData.RegisterShopUnlock` draws from the per-tier groups and never reads
  `unlockRequirements`; the gate lives only in `Shop` -> `AddShopItemsWhereRequirementsMet`.

