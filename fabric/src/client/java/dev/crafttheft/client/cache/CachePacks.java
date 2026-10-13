package dev.crafttheft.client.cache;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;
import java.util.Set;
import java.util.function.Consumer;
import java.util.stream.Stream;

import net.minecraft.network.chat.Component;
import net.minecraft.server.packs.PackLocationInfo;
import net.minecraft.server.packs.PackSelectionConfig;
import net.minecraft.server.packs.PackType;
import net.minecraft.server.packs.PathPackResources;
import net.minecraft.server.packs.repository.Pack;
import net.minecraft.server.packs.repository.PackSource;
import net.minecraft.server.packs.repository.RepositorySource;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.Hooks;
import dev.crafttheft.config.ModConfig;

/**
 * hooks row cache_pack: every folder under &lt;cache_dir&gt;/packs/ (halo, gta, fortnite, ...) with a pack.mcmeta becomes
 * a required, top-priority client resource pack "crafttheft_cache_&lt;name&gt;". Re-evaluated on every pack reload, so
 * a fresh extraction shows up after reloadResourcePacks(). Folders starting with "." (staging/swap) are skipped.
 */
public final class CachePacks implements RepositorySource {
	public static final String PREFIX = "crafttheft_cache_";
	public static volatile List<String> lastMounted = List.of();

	/** Called by PackRepositoryMixin for the client repository. */
	public static void addSources(Set<RepositorySource> sources) {
		sources.add(new CachePacks());
		Hooks.mark("cache_pack", "CachePacks.addSources (PackRepositoryMixin)");
	}

	@Override
	public void loadPacks(Consumer<Pack> out) {
		Path packs = ModConfig.reload().cacheDir.resolve("packs");
		List<String> mounted = new ArrayList<>();
		if (!Files.isDirectory(packs)) {
			CraftTheft.LOG.warn("[cache-pack] {} does not exist: running with the jar's placeholders", packs);
			lastMounted = List.of();
			return;
		}
		List<Path> dirs;
		try (Stream<Path> s = Files.list(packs)) {
			dirs = s.filter(Files::isDirectory).sorted().toList();
		} catch (IOException e) {
			CraftTheft.LOG.error("[cache-pack] cannot list {}: {}", packs, e.toString());
			return;
		}
		for (Path dir : dirs) {
			String name = dir.getFileName().toString();
			if (name.startsWith(".")) continue;
			if (!Files.isRegularFile(dir.resolve("pack.mcmeta"))) {
				CraftTheft.LOG.warn("[cache-pack] {} has no pack.mcmeta, skipped", dir);
				continue;
			}
			String id = PREFIX + name.toLowerCase(java.util.Locale.ROOT).replaceAll("[^a-z0-9_.-]", "_");
			PackLocationInfo info = new PackLocationInfo(id, Component.literal("Craft Theft Halo: " + name + " (from your install)"), PackSource.BUILT_IN, Optional.empty());
			Pack pack = Pack.readMetaAndCreate(info, new PathPackResources.PathResourcesSupplier(dir), PackType.CLIENT_RESOURCES,
					new PackSelectionConfig(true, Pack.Position.TOP, false));
			if (pack == null) {
				CraftTheft.LOG.error("[cache-pack] {} has an unreadable pack.mcmeta, skipped", dir);
				continue;
			}
			out.accept(pack);
			mounted.add(id);
			CraftTheft.LOG.info("[cache-pack] mounted {} from {} (compatibility {})", id, dir, pack.getCompatibility());
		}
		lastMounted = List.copyOf(mounted);
	}
}
