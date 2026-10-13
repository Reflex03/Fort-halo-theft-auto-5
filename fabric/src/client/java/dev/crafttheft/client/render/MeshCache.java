package dev.crafttheft.client.render;

import java.io.InputStream;
import java.util.HashMap;
import java.util.Map;
import java.util.TreeMap;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executor;

import org.jetbrains.annotations.Nullable;

import net.fabricmc.fabric.api.resource.SimpleResourceReloadListener;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.server.packs.resources.Resource;
import net.minecraft.server.packs.resources.ResourceManager;
import net.minecraft.util.profiling.ProfilerFiller;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.shield.ShieldTuning;
import dev.crafttheft.weapon.WeaponStats;

/**
 * hooks row mesh_reload: loads every assets/crafttheft/meshes/**.cthm from the active packs (cache packs on top of
 * the jar's placeholders) on a background thread during each resource reload and swaps the map on apply. Every
 * mesh logs where it came from; material textures that are missing resolve to the placeholder texture.
 */
public final class MeshCache implements SimpleResourceReloadListener<Map<String, RuntimeMesh>> {
	public static final MeshCache INSTANCE = new MeshCache();
	public static final ResourceLocation MISSING_TEXTURE = CraftTheft.id("textures/placeholder/generic.png");
	private static volatile Map<String, RuntimeMesh> meshes = Map.of();
	public static volatile int reloads, rejected, missingTextures;

	/** Key = path below meshes/ without extension, e.g. "halo/assault_rifle". */
	@Nullable
	public static RuntimeMesh get(String key) { return meshes.get(key); }

	public static Map<String, RuntimeMesh> all() { return meshes; }

	@Override public ResourceLocation getFabricId() { return CraftTheft.id("meshes"); }

	@Override
	public CompletableFuture<Map<String, RuntimeMesh>> load(ResourceManager manager, ProfilerFiller profiler, Executor executor) {
		return CompletableFuture.supplyAsync(() -> reload(manager), executor);
	}

	/** Parse all meshes (background thread). */
	public static Map<String, RuntimeMesh> reload(ResourceManager manager) {
		Map<String, RuntimeMesh> out = new HashMap<>();
		int bad = 0, noTex = 0;
		Map<ResourceLocation, Resource> found = new TreeMap<>(manager.listResources("meshes",
				id -> id.getNamespace().equals(CraftTheft.MODID) && id.getPath().endsWith(".cthm")));
		for (var e : found.entrySet()) {
			String path = e.getKey().getPath();
			String key = path.substring("meshes/".length(), path.length() - ".cthm".length());
			String pack = e.getValue().sourcePackId();
			try (InputStream in = e.getValue().open()) {
				RuntimeMesh m = RuntimeMesh.read(in.readAllBytes());
				m.key = key;
				m.sourcePack = pack;
				int missing = 0;
				for (RuntimeMesh.Submesh s : m.submeshes) {
					ResourceLocation tex = textureFor(s.material);
					if (tex == null || manager.getResource(tex).isEmpty()) {
						missing++;
						s.texture = MISSING_TEXTURE;
					} else s.texture = tex;
				}
				noTex += missing;
				out.put(key, m);
				CraftTheft.LOG.info("[mesh] {}: {} tris, {} submeshes, size {}x{}x{} from pack {}{}", key, m.triangleCount, m.submeshes.size(),
						String.format("%.2f", m.sizeX()), String.format("%.2f", m.sizeY()), String.format("%.2f", m.sizeZ()), pack,
						missing > 0 ? " (" + missing + " material textures missing -> placeholder)" : "");
			} catch (Exception ex) {
				bad++;
				CraftTheft.LOG.error("[mesh] {} from pack {} rejected: {}", e.getKey(), pack, ex.getMessage());
			}
		}
		rejected = bad;
		missingTextures = noTex;
		return out;
	}

	@Nullable
	public static ResourceLocation textureFor(String material) {
		if (material == null || material.isBlank()) return null;
		String m = material.replace('\\', '/');
		ResourceLocation rl = ResourceLocation.tryParse(m.contains(":") ? m : CraftTheft.MODID + ":" + m);
		return rl == null ? null : rl.withPath(p -> "textures/" + p + ".png");
	}

	@Override
	public CompletableFuture<Void> apply(Map<String, RuntimeMesh> data, ResourceManager manager, ProfilerFiller profiler, Executor executor) {
		return CompletableFuture.runAsync(() -> {
			meshes = data;
			reloads++;
			long cache = data.values().stream().filter(RuntimeMesh::fromCache).count();
			CraftTheft.LOG.info("[mesh] {} meshes loaded ({} from the cache packs, {} jar placeholders, {} rejected)", data.size(), cache, data.size() - cache, rejected);
			// client copies of the stats files (zoom, clip sizes in the HUD) follow the cache too
			WeaponStats.reload();
			ShieldTuning.reload();
		}, executor);
	}
}
