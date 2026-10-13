package dev.crafttheft.client.render;

import java.util.HashSet;
import java.util.Set;

import dev.crafttheft.config.CacheManifest;
import dev.crafttheft.config.ModConfig;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.melty.MeltyChecks;

/**
 * Melty checks halo-content / gta-content / fortnite-content: reported the first time a mesh of that game is
 * actually drawn. Pass when it came from the player's cache pack; fail (with the manifest's reason) when the jar
 * placeholder was drawn instead.
 */
public final class ContentChecks {
	private static final Set<String> SEEN = new HashSet<>();

	private ContentChecks() {}

	public static void drawn(String meshKey, RuntimeMesh mesh) {
		int slash = meshKey.indexOf('/');
		if (slash < 0) return;
		String game = meshKey.substring(0, slash);
		if (!SEEN.add(game)) return;
		String check = switch (game) {
			case "halo" -> MeltyChecks.HALO_CONTENT;
			case "gta" -> MeltyChecks.GTA_CONTENT;
			case "fortnite" -> MeltyChecks.FORTNITE_CONTENT;
			default -> null;
		};
		if (check == null) return;
		if (mesh.fromCache()) {
			MeltyChecks.pass(check, meshKey + " drawn from " + mesh.sourcePack + " (" + mesh.triangleCount + " tris)");
		} else {
			String sourceId = sourceFor(meshKey);
			String why = CacheManifest.read(ModConfig.get().cacheDir).explain(sourceId == null ? meshKey : sourceId, game);
			MeltyChecks.fail(check, meshKey + " is the placeholder from " + mesh.sourcePack + "; " + why);
		}
	}

	static String sourceFor(String meshKey) {
		for (Sheets.Source s : Sheets.SOURCES) if (s.out().equals("meshes/" + meshKey + ".cthm")) return s.id();
		return null;
	}
}
