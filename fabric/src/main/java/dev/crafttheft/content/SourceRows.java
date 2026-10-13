package dev.crafttheft.content;

import java.util.HashMap;
import java.util.Map;

import dev.crafttheft.gen.Sheets;

/** Lookups over the sources sheet (Sheets.SOURCES): mesh keys, sound events, game short names. */
public final class SourceRows {
	private static final Map<String, Sheets.Source> BY_ID = new HashMap<>();
	static {
		for (Sheets.Source s : Sheets.SOURCES) BY_ID.put(s.id(), s);
	}

	private SourceRows() {}

	public static Sheets.Source get(String id) {
		Sheets.Source s = BY_ID.get(id);
		if (s == null) throw new IllegalArgumentException("unknown sources row " + id);
		return s;
	}

	/** "meshes/halo/assault_rifle.cthm" -> "halo/assault_rifle" (the MeshCache key). */
	public static String meshKey(String sourceId) {
		String o = get(sourceId).out();
		return o.substring("meshes/".length(), o.length() - ".cthm".length());
	}

	/** Same rule as the extractor: "sounds/halo/x.ogg" -> "halo.x". */
	public static String soundEvent(String sourceId) {
		String o = get(sourceId).out();
		String rel = o.substring("sounds/".length());
		return rel.substring(0, rel.lastIndexOf('.')).replace('/', '.');
	}

	/** "halo-mcc" -> "halo", "gta-v" -> "gta", "fortnite" -> "fortnite" (cache/packs/&lt;short&gt;). */
	public static String gameShort(String game) {
		return switch (game) {
			case "halo-mcc" -> "halo";
			case "gta-v" -> "gta";
			default -> game;
		};
	}

	/** "textures/gui/gta/wanted_star.png" -> "gui/gta/wanted_star" (texture id below textures/). */
	public static String texturePath(String sourceId) {
		String o = get(sourceId).out();
		return o.substring("textures/".length(), o.length() - ".png".length());
	}
}
