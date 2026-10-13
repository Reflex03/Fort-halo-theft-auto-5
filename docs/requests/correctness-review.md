# Requests from the correctness review (agent key: correctness-review)

The code is already changed to behave as described below; nothing here blocks a build. Each item says exactly what to
change in a sheet or in `docs/CONTRACTS.md`, and why.

## 1. `sheets/hooks.json`

### 1.1 `attack_entity`: ranged Halo weapon hits are reported through the same hook

`AttackEntityCallback` only sees melee. Shooting a villager or an iron golem from more than 16 blocks away (the
`gunfire` radius) was no crime at all, and a golem was never "assaulted" by a gun. The code now reports every Halo
weapon hit that damaged an entity (hitscan round, plasma bolt / needle, rocket / grenade blast) as the `attack_entity`
event (`CrimeSystem.onRangedHit`), so the crimes rows `assault_villager` and `assault_golem` fire for guns too (same
20-tick de-duplication as melee).

Replace the row with:

```json
{"id": "attack_entity", "side": "server", "event": "AttackEntityCallback.EVENT (melee) + Halo weapon hits (WeaponSystem hitscan, HaloProjectile.onHitEntity, Explosions.blast)", "handler": "CrimeSystem.onAttack / CrimeSystem.onRangedHit", "purpose": "villager/golem/police assault -> wanted stars (melee and ranged)"}
```

### 1.2 New row `server_stopped`

The systems keep per-world maps in statics (queued village street cars by block position, pending bus boardings,
crime de-duplication by game time, shield/wanted timers). A singleplayer client opens several worlds in one JVM, so
they are now cleared when a server stops (`GameTick.clearWorldState`); before, a street car queued in world A could
spawn in world B, and a pending bus boarding from world A could board a player in world B who had already dropped.

```json
{"id": "server_stopped", "side": "server", "event": "ServerLifecycleEvents.SERVER_STOPPED", "handler": "GameTick.clearWorldState", "purpose": "drop per-world state kept in statics (street-car queue, pending bus boardings, crime/shield/wanted/police timers) so the next world in the same game session starts clean"}
```

(`Hooks.mark("server_stopped", ...)` is registered; the harness lists it as "extra hook" until the row exists.)

## 2. `sheets/systems.json`: village street cars are extra and not sheet-driven

`cars.parkedCount` says "cars parked around spawn/villages on a new world", but the code plans exactly
`parkedCount` cars around spawn and **adds** up to 6 cars per generated village (35% chance per street piece,
`WorldSetup.STREET_CHANCE` / `MAX_PER_VILLAGE`, hard-coded). Either:

- add two rows and the code will read them (`Sheets.CARS_STREET_CHANCE`, `Sheets.CARS_MAX_PER_VILLAGE`):
  ```json
  {"system": "cars", "key": "streetChance", "value": 0.35, "doc": "chance a village street piece gets a parked car (extra to parkedCount)"},
  {"system": "cars", "key": "maxPerVillage", "value": 6, "doc": "most extra parked cars per village"}
  ```
- or change the `parkedCount` doc to "cars parked around spawn on a new world (villages get extra street cars)".

## 3. `docs/CONTRACTS.md` (mod behaviour that the sheets' units now guarantee)

Under "How the mod uses the cache" add:

- The stats files are found at the stats rows' `out` paths under the cache root (`sources.halo_stats_weapons.out`,
  `sources.halo_stats_shield.out`), not at hard-coded names.
- Halo rounds that are entities (plasma bolts, needles, rockets) fly at the weapons sheet's `projectileSpeed`
  (blocks/tick) without vanilla's 0.99 per-tick drag, for `range / projectileSpeed` ticks, so they reach `range`
  (rockets reached about 77% of it before). Water still slows them. Grenades keep vanilla gravity and drag; their
  `range` column is not used (the throw distance follows from `projectileSpeed` and gravity, about 24 blocks at 45°).
- Hitscan rays stop at the edge of the loaded chunks (a 192-block sniper ray never makes the server load or generate
  chunks on its thread).
- Rocket and grenade blasts damage GTA cars as well as living things (`vehicles.health`), so explosives can destroy a
  police car and count as `assault_police`.
- A dispatched police car remembers whom it chases across save/load (`DispatchedFor`), consistent with the wanted
  level, which is a persistent player attachment. Only dispatched police cars (not a parked or stolen one, not the car
  the player drives) count as "police sight" for `wanted.loseAfterTicks`. Damaging a police car the player stole and
  owns is not `assault_police`.
- Leaving the game while riding the Battle Bus is not a drop: vanilla saves the player with the bus as `RootVehicle`
  and re-seats them on rejoin; if the bus cannot be restored, the player boards a new bus (they are not marked as
  dropped, so `player_join` boards them again instead of leaving them at the bus altitude without a glider).
