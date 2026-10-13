package dev.crafttheft.tools;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.stream.Stream;

import dev.crafttheft.gen.Sheets;

/**
 * Build-time generator (Gradle task genSheetResources). Everything it writes is derived from the sheets (via the
 * generated Sheets class) or is original placeholder art drawn here; nothing comes from any game.
 * <ul>
 *   <li>assets/crafttheft/sounds.json: one event per sources sound row, mapped to the row's placeholder vanilla
 *       sound event (the cache packs replace them with "replace": true)</li>
 *   <li>item models for every weapons/consumables row, lang/en_us.json from the sheet names</li>
 *   <li>placeholder .cthm meshes at every sources mesh row's out path (except rows whose placeholder is a vanilla
 *       model) and their textures under textures/placeholder/</li>
 *   <li>placeholder icons at every sources icon row's out path</li>
 * </ul>
 */
public final class GenResources {
	private GenResources() {}

	public static void main(String[] args) throws IOException {
		Path out = Path.of(args[0]);
		if (Files.exists(out)) try (Stream<Path> s = Files.walk(out)) {
			for (Path p : s.sorted(Comparator.reverseOrder()).toList()) Files.delete(p);
		}
		Path assets = out.resolve("assets/crafttheft");
		writeSounds(assets);
		writeItemModels(assets);
		writeLang(assets);
		Placeholders.write(assets);
		System.out.println("[gen] wrote sheet resources and placeholders to " + out);
	}

	// ------------------------------------------------------------------ sounds.json
	/** Same rule as the extractor (ExtractContext.SoundEvent): out "sounds/halo/x.ogg" -> event "halo.x". */
	public static String soundEvent(Sheets.Source s) {
		String rel = s.out().substring("sounds/".length());
		return rel.substring(0, rel.lastIndexOf('.')).replace('/', '.');
	}

	/** "vanilla minecraft:block.chest.open (...)" -> "minecraft:block.chest.open". */
	public static String vanillaEvent(Sheets.Source s) {
		String p = s.placeholder().trim();
		if (!p.startsWith("vanilla ")) throw new IllegalStateException("sound row " + s.id() + " placeholder must start with 'vanilla <event>': " + p);
		String ev = p.substring("vanilla ".length()).trim();
		int sp = ev.indexOf(' ');
		return sp < 0 ? ev : ev.substring(0, sp);
	}

	private static void writeSounds(Path assets) throws IOException {
		StringBuilder sb = new StringBuilder("{\n");
		List<String> entries = new ArrayList<>();
		for (Sheets.Source s : Sheets.SOURCES) {
			if (!s.kind().equals("sound")) continue;
			String ev = soundEvent(s);
			entries.add("  " + q(ev) + ": {\"sounds\": [{\"name\": " + q(vanillaEvent(s)) + ", \"type\": \"event\"}], \"subtitle\": "
					+ q("subtitles.crafttheft." + ev) + "}");
		}
		sb.append(String.join(",\n", entries)).append("\n}\n");
		write(assets.resolve("sounds.json"), sb.toString());
	}

	// ------------------------------------------------------------------ item models
	private static void writeItemModels(Path assets) throws IOException {
		for (Sheets.Weapon w : Sheets.WEAPONS) {
			// Drawn in code by WeaponItemRenderer (the mesh from the cache or the jar placeholder).
			write(assets.resolve("models/item/" + w.id() + ".json"),
					"{\"parent\": \"minecraft:builtin/entity\", \"gui_light\": \"front\", \"textures\": {\"particle\": \"minecraft:block/gray_concrete\"}}\n");
		}
		for (Sheets.Consumable c : Sheets.CONSUMABLES) {
			String tex = textureId(source(c.icon()));
			write(assets.resolve("models/item/" + c.id() + ".json"),
					"{\"parent\": \"minecraft:item/generated\", \"textures\": {\"layer0\": " + q("crafttheft:" + tex) + "}}\n");
		}
		write(assets.resolve("models/item/loot_chest.json"),
				"{\"parent\": \"minecraft:item/chest\", \"textures\": {\"particle\": \"minecraft:block/oak_planks\"}}\n");
		write(assets.resolve("models/block/loot_chest.json"), "{\"textures\": {\"particle\": \"minecraft:block/oak_planks\"}}\n");
		write(assets.resolve("blockstates/loot_chest.json"),
				"{\"variants\": {\"facing=north\": {\"model\": \"crafttheft:block/loot_chest\"}, \"facing=south\": {\"model\": \"crafttheft:block/loot_chest\"}, "
						+ "\"facing=east\": {\"model\": \"crafttheft:block/loot_chest\"}, \"facing=west\": {\"model\": \"crafttheft:block/loot_chest\"}}}\n");
	}

	/** "textures/item/fortnite/bandages.png" -> "item/fortnite/bandages". */
	public static String textureId(Sheets.Source s) {
		String o = s.out();
		if (!o.startsWith("textures/") || !o.endsWith(".png")) throw new IllegalStateException("icon row " + s.id() + " out must be textures/...png");
		return o.substring("textures/".length(), o.length() - 4);
	}

	public static Sheets.Source source(String id) {
		for (Sheets.Source s : Sheets.SOURCES) if (s.id().equals(id)) return s;
		throw new IllegalStateException("unknown sources id " + id);
	}

	// ------------------------------------------------------------------ lang
	private static void writeLang(Path assets) throws IOException {
		Map<String, String> m = new LinkedHashMap<>();
		m.put("itemGroup.crafttheft", "Craft Theft Halo");
		for (Sheets.Weapon w : Sheets.WEAPONS) m.put("item.crafttheft." + w.id(), w.name());
		for (Sheets.Consumable c : Sheets.CONSUMABLES) m.put("item.crafttheft." + c.id(), c.name());
		for (Sheets.Vehicle v : Sheets.VEHICLES) m.put("entity.crafttheft." + v.id(), v.name());
		for (Sheets.Crime c : Sheets.CRIMES) m.put("crafttheft.crime." + c.id(), c.name());
		for (Sheets.Source s : Sheets.SOURCES) if (s.kind().equals("sound")) m.put("subtitles.crafttheft." + soundEvent(s), subtitle(s));
		m.put("entity.crafttheft.battle_bus", "Battle Bus");
		m.put("entity.crafttheft.halo_projectile", "Plasma Bolt");
		m.put("entity.crafttheft.halo_rocket", "Rocket");
		m.put("entity.crafttheft.halo_grenade", "Grenade");
		m.put("block.crafttheft.loot_chest", "Supply Chest");
		m.put("container.crafttheft.loot_chest", "Supply Chest");
		m.put("death.attack.crafttheft.ran_over", "%1$s was run over");
		m.put("death.attack.crafttheft.ran_over.player", "%1$s was run over by %2$s");
		m.put("death.attack.crafttheft.police_gunfire", "%1$s was shot by the police");
		m.put("death.attack.crafttheft.police_gunfire.player", "%1$s was shot by the police while fighting %2$s");
		m.put("death.attack.crafttheft.halo_explosion", "%1$s was blown up");
		m.put("death.attack.crafttheft.halo_explosion.player", "%1$s was blown up by %2$s");
		m.put("key.crafttheft.reload", "Reload");
		m.put("key.crafttheft.horn", "Car Horn");
		m.put("category.crafttheft", "Craft Theft Halo");
		m.put("crafttheft.hud.jump_to_drop", "Press %s to jump from the Battle Bus");
		m.put("crafttheft.hud.skydiving", "SKYDIVING");
		m.put("crafttheft.hud.gliding", "GLIDING");
		m.put("crafttheft.hud.reloading", "RELOADING");
		m.put("crafttheft.hud.using", "Using %s");
		m.put("crafttheft.wanted.crime", "Wanted: %s");
		m.put("crafttheft.wanted.lost", "Wanted level dropped");
		m.put("crafttheft.extract.title", "Reading your games...");
		m.put("crafttheft.extract.subtitle", "Craft Theft Halo copies models, textures and sounds from your own Halo MCC, GTA V and Fortnite installs. This happens once.");
		m.put("crafttheft.extract.waiting", "Starting the extractor...");
		m.put("crafttheft.extract.game", "Now reading: %s");
		m.put("crafttheft.extract.step", "%s");
		m.put("crafttheft.extract.count", "%s of %s items");
		m.put("crafttheft.extract.skip", "Play with placeholders");
		m.put("crafttheft.extract.done", "Done. Loading the new content...");
		m.put("crafttheft.extract.updated", "Craft Theft Halo: re-read %s, content reloaded");
		StringBuilder sb = new StringBuilder("{\n");
		List<String> lines = new ArrayList<>();
		for (var e : m.entrySet()) lines.add("  " + q(e.getKey()) + ": " + q(e.getValue()));
		sb.append(String.join(",\n", lines)).append("\n}\n");
		write(assets.resolve("lang/en_us.json"), sb.toString());
	}

	private static String subtitle(Sheets.Source s) {
		String ev = soundEvent(s);
		String what = ev.substring(ev.indexOf('.') + 1).replace('_', ' ');
		return Character.toUpperCase(what.charAt(0)) + what.substring(1);
	}

	// ------------------------------------------------------------------ helpers
	static String q(String s) {
		StringBuilder b = new StringBuilder("\"");
		for (char c : s.toCharArray()) {
			switch (c) {
				case '"' -> b.append("\\\"");
				case '\\' -> b.append("\\\\");
				case '\n' -> b.append("\\n");
				default -> {
					if (c < 0x20 || c > 0x7e) b.append(String.format("\\u%04x", (int) c));
					else b.append(c);
				}
			}
		}
		return b.append('"').toString();
	}

	static void write(Path file, String text) throws IOException {
		Files.createDirectories(file.getParent());
		Files.writeString(file, text, StandardCharsets.UTF_8);
	}
}
