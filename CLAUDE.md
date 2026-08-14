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

Logs: `BepInEx\LogOutput.log`, search `ModuleForge`. The burn ceiling lives in
`BepInEx\config\com.sugarheady.moduleforge.cfg`.

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

### Sprites and icons

- Stock icons are `HUD_GridTiles_NN`, tinted by `module.color` — in *both* the places an icon is drawn.
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
- This mod **borrows Weapon Forge's audio pipeline** when present, so there is one `sounds` folder and
  one set of names across both mods. Don't duplicate the decoder here. Same for the `sprites` folder
  behind custom icons.
- **Loot is the opposite call: duplicated, not borrowed.** `ModuleLootPools` is a deliberate copy of
  `ForgeLootPools` — a table of constant asset names isn't worth a dependency, and each mod must drop
  loot with the other absent. The two coordinate through **one shared string**: the created Money pool
  is named `Forge Modules Crate Money` in *both* mods, so `FindObjectsOfTypeAll` finds whichever
  instance exists and they fill one pool. Both also check `TableAlreadyRolls` before appending a graft,
  or a player with both mods gets two module rolls out of every Money crate. Rename that constant in
  one mod only and you reintroduce exactly that bug.
- If the reflection fails, degrade to doing nothing — never risk two mods fighting over the same patch.

## House rules

- Verify against the decompile before building. Nearly every bug in this project's history was a wrong
  assumption about the engine, not a wrong line of C#.
- Check whether a game field already does the job before writing code.
- Assume any class instance you hand over is kept by reference — assign a fresh one rather than mutating
  a shared stock object.
