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

### Plugin load order — this mod loads FIRST

BepInEx loads Module Forge **before** Weapon Forge, so at `Awake` time Weapon Forge's types genuinely
do not exist yet. Any cross-mod check has to be **deferred** (the first `ShipHud.AssignShip` bind is a
good point), and it must look the type up **silently** — `AccessTools.TypeByName` logs a warning when
the type is absent, and "the other mod is not installed" is a completely normal state. Scan
`AppDomain.CurrentDomain.GetAssemblies()` instead.

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
- **An icon name takes the `:frames` suffix, and `ForgeAssets.StockIcon` is why.** `ResolveIcon` used
  to hand the whole string to `FindAsset`, so `"HUD_GridTiles_04:2"` matched no asset and fell through
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
- **`ForgeAssets.ResolveSpriteFrames` is STOCK-ONLY on purpose**, unlike everything else art-shaped
  here. Its one caller is the standalone leech orb, which by definition only runs with Weapon Forge
  absent — so reaching for the borrowed loader would be dead code, and `TryResolveIcon` rebuilds at the
  module-icon footprint (24x24 at PPU 40), the wrong size for anything that is not an icon.
- **`ModuleForgeUnitTint` is a deliberate copy of `ForgeUnitTint`**, same call as the buff HUD: a
  display layer is not a decoder worth a dependency, and the crit tint has to work with the other mod
  absent. The two are never live at once (Weapon Forge owns the crit patch when present), so they
  cannot fight. Keep the two scoping rules identical in both — they encode which renderers a unit's
  prefab means to be recoloured, which is a fact about the **game**, not about either mod.
- This mod **borrows Weapon Forge's audio pipeline** when present, so there is one `sounds` folder and
  one set of names across both mods. Don't duplicate the decoder here. Same for the `sprites` folder
  behind custom icons.
- **But resolving one of the GAME's own sound NAMES is native here, and the line is the same one
  `ResolveIcon` and `ResolveColor` already draw:** custom-*file* loading is Weapon Forge's pipeline
  and is borrowed; looking a name up in an asset the **game** owns is not a decoder and has to work
  standalone. `Sfx` carries both a `guid` and a readable hierarchical `name` (`Cells/Fuel`,
  `UI/Click`), and until 2026-09-02 **both mods matched on `guid` only** - so all 259 of the game's
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
