package dev.crafttheft.client.render;

import net.minecraft.resources.ResourceLocation;

import dev.crafttheft.CraftTheft;

/** Mesh lookup with the last-resort code-built box, plus the content check on every lookup for drawing. */
public final class Meshes {
	public static final ResourceLocation BOX_TEXTURE = CraftTheft.id("textures/placeholder/generic.png");

	private Meshes() {}

	/** The mesh to draw for a key (cache pack, else jar placeholder, else a box of the given size). */
	public static RuntimeMesh forDraw(String key, float w, float h, float d) {
		RuntimeMesh m = MeshCache.get(key);
		if (m == null) {
			m = RuntimeMesh.box(w, h, d, BOX_TEXTURE);
			m.key = key;
		}
		ContentChecks.drawn(key, m);
		return m;
	}
}
