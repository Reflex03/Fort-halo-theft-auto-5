Craft Theft Halo $version
========================

A Minecraft mashup: ride Fortnite's Battle Bus over a Minecraft world, glide down, loot Halo 3 weapons and
Fortnite heals from chests, steal GTA V cars and outrun the police. Installed and started by Melty (melty.gg).
Project page and source: $repo

What is in this folder
  Prism/                         Prism Launcher $prism (portable). Melty starts
                                 Prism/prismlauncher.exe --launch CraftTheftHalo
  Prism/instances/CraftTheftHalo Minecraft $minecraft with Fabric Loader $loader, Fabric API and the mod
  bin/cthextract.exe             reads Halo 3, GTA V and Fortnite content from YOUR OWN installs
  cache/                         what it read (made on your PC on the first Play; never uploaded or shared)
  licenses/, THIRD-PARTY-NOTICES.md, SOURCE.txt   licences, notices and where to get the source code

Your games stay untouched
  cthextract.exe only reads files from Halo: The Master Chief Collection, GTA V Legacy and Fortnite. It never
  writes into a game folder, never starts a game and never touches GTA Online. Nothing from those games is
  included in this download: everything the mod shows from them is read on your PC, into cache/.

If something looks wrong
  cache/extract.log              what the extractor tried, found and picked, game by game
  cache/manifest.json            what was read: "ok", "missing" or "failed" for every item, with the reason
  Prism/instances/CraftTheftHalo/.minecraft/logs/latest.log   the Minecraft log
  Anything that could not be read is replaced by the mod's own stand-in art and sounds, so the game still plays.
  If a game was missing or could not be read, the mod tries it again by itself once that game is installed
  or updated.

Uninstall through Melty. Deleting this folder by hand removes Prism, its Minecraft files, your worlds and the cache.

Licences: the mod and our own files are CC BY-NC-SA 4.0; cthextract.exe is GPL-3.0; Prism Launcher is GPL-3.0;
see THIRD-PARTY-NOTICES.md. Fan project, not affiliated with Mojang, Microsoft, 343 Industries, Rockstar Games,
Take-Two or Epic Games.
