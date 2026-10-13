# Craft Theft Halo (Fabric mod `crafttheft`): licence

Copyright (c) 2026 the Craft Theft Halo authors.

The mod's code and its original placeholder art, meshes and sound mappings are licensed under the
**Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International** licence (CC BY-NC-SA 4.0):
https://creativecommons.org/licenses/by-nc-sa/4.0/legalcode

The mod ships no content from Halo: The Master Chief Collection, Grand Theft Auto V or Fortnite. Models, textures and
sounds from those games are read on the player's own PC from the player's own installs by the separate extractor
(GPL-3.0-only) into a local cache, which this mod loads at run time.

Exception: `tools/CthSynth/` (a test-only .NET tool, never shipped) links the extractor's `CthExtract.Core` library
and is therefore licensed GPL-3.0-only, like the extractor (`../extractor/LICENSE`).

Third-party components used at run time (not included in this jar): Minecraft (Mojang), Fabric Loader and
Fabric API (Apache-2.0).
