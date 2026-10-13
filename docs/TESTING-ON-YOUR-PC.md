# Finishing Craft Theft Halo on a Windows PC with the games

Everything that can be done without the games is done: the extractor, the Fabric mod and the Melty release
(v0.1.0, draft) are built, tested on synthetic data and uploaded. What is left needs a Windows PC that has
Minecraft: Java Edition, Halo: The Master Chief Collection (with Halo 3), GTA V Legacy and Fortnite installed,
plus the Melty app.

Melty listing: <https://melty.gg/studio/6790f53f-51be-4972-8b81-ff5b2f1f72bc> (draft)
- modId `6790f53f-51be-4972-8b81-ff5b2f1f72bc`, slug `craft-theft-halo`
- release v0.1.0: `5673dcc0-ddbe-418a-8cd0-f04af10b277b` (files `crafttheft-0.1.0.jar`,
  `CraftTheftHalo-Minecraft-0.1.0.zip`, `CraftTheftHalo-0.1.0-sources.zip`)

## Steps (a Claude Code session on that PC does them; the Melty connection is set up from Melty's Publish prompt)

1. Open the Melty app.
2. `test_release {"modId": "6790f53f-51be-4972-8b81-ff5b2f1f72bc"}`. Melty installs v0.1.0 the way players get it,
   runs the "game content" step (`cthextract.exe`, a few minutes, online) and starts Prism + Minecraft.
3. While each run plays, play it: ride the bus, drop, glide, open a chest, fire a Halo gun, drink a shield potion,
   get in a car, hit a villager until the stars come, let the police arrive. The mod reports the 12 test checks
   itself (`docs/requests/mod.md`). Poll `test_result` until it is done; fix what it lists.
4. Take a screenshot of the game window (game only) that shows real content: the bus, a Halo gun in hand with the
   shield bar, or a GTA car with the wanted stars. `add_screenshot` → PUT → `finish_screenshot`.
5. Read `%LOCALAPPDATA%\CraftTheftHalo\cache\manifest.json` and `extract.log`. Every sources row with
   `_unverified` columns (51 cells, `python3 tools/preflight.py` lists them) is checked there: what the extractor
   tried, found and picked. Update the sheets from it, re-run `tools/gen.py`, rebuild, and if anything changed,
   submit 0.1.1.

## Where to look when something fails

| What | Where |
|---|---|
| Content reader log and per-item results | `%LOCALAPPDATA%\CraftTheftHalo\cache\extract.log`, `manifest.json` |
| Minecraft log | `%LOCALAPPDATA%\CraftTheftHalo\Prism\instances\CraftTheftHalo\.minecraft\logs\latest.log` |
| Settings Melty wrote | `...\.minecraft\config\crafttheft.properties` |
| Re-run the reader by hand | `%LOCALAPPDATA%\CraftTheftHalo\bin\cthextract.exe --config <that properties file> --console` |
