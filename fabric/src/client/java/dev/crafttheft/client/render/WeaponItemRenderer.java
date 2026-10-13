package dev.crafttheft.client.render;

import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.math.Axis;

import net.fabricmc.fabric.api.client.rendering.v1.BuiltinItemRendererRegistry;
import net.minecraft.client.renderer.MultiBufferSource;
import net.minecraft.world.item.ItemDisplayContext;
import net.minecraft.world.item.ItemStack;

import dev.crafttheft.content.SourceRows;
import dev.crafttheft.weapon.HaloWeaponItem;

/**
 * hooks row item_renderer: draws a Halo weapon mesh (grip at the origin, barrel towards +Z, true size) for every
 * item context. The item model's parent is minecraft:builtin/entity with no display transforms, so vanilla only
 * translates by (-0.5,-0.5,-0.5) before calling us and every placement is done here.
 */
public class WeaponItemRenderer implements BuiltinItemRendererRegistry.DynamicItemRenderer {
	/** First person: hand offset (blocks) and size factor. */
	public static float FP_X = -0.05f, FP_Y = 0.12f, FP_Z = 0.02f, FP_SCALE = 0.9f;
	/** Third person: offset in the hand's frame and size factor. */
	public static float TP_Y = 0.15f, TP_Z = 0.05f, TP_SCALE = 0.85f;

	@Override
	public void render(ItemStack stack, ItemDisplayContext mode, PoseStack ps, MultiBufferSource buffers, int light, int overlay) {
		if (!(stack.getItem() instanceof HaloWeaponItem w)) return;
		String key = SourceRows.meshKey(w.row.model());
		RuntimeMesh mesh = Meshes.forDraw(key, 0.12f, 0.2f, 0.7f);
		ps.pushPose();
		ps.translate(0.5f, 0.5f, 0.5f);
		switch (mode) {
			case FIRST_PERSON_RIGHT_HAND, FIRST_PERSON_LEFT_HAND -> {
				boolean left = mode == ItemDisplayContext.FIRST_PERSON_LEFT_HAND;
				ps.translate(left ? -FP_X : FP_X, FP_Y, FP_Z);
				ps.mulPose(Axis.YP.rotationDegrees(180f)); // barrel +Z -> away from the camera
				float s = FP_SCALE * fitHeld(mesh);
				ps.scale(s, s, s);
			}
			case THIRD_PERSON_RIGHT_HAND, THIRD_PERSON_LEFT_HAND -> {
				ps.translate(0f, TP_Y, TP_Z);
				ps.mulPose(Axis.XP.rotationDegrees(90f));
				ps.mulPose(Axis.YP.rotationDegrees(180f));
				float s = TP_SCALE * fitHeld(mesh);
				ps.scale(s, s, s);
			}
			default -> {
				// GUI / ground / item frame / head: fit the whole mesh into the unit cube, barrel to the right
				if (mode == ItemDisplayContext.GUI) {
					ps.mulPose(Axis.XP.rotationDegrees(-15f));
					ps.mulPose(Axis.YP.rotationDegrees(90f));
				}
				float fit = switch (mode) { case GROUND -> 0.5f; case HEAD -> 0.6f; default -> 0.95f; };
				float s = fit / Math.max(mesh.sizeX(), Math.max(mesh.sizeY(), mesh.sizeZ()));
				ps.scale(s, s, s);
				ps.translate(-(mesh.minX + mesh.maxX) / 2f, -(mesh.minY + mesh.maxY) / 2f, -(mesh.minZ + mesh.maxZ) / 2f);
			}
		}
		MeshDraw.draw(mesh, ps, buffers, light, overlay, 0f, 0f);
		ps.popPose();
	}

	/** Weapons are true size; clamp absurd sizes (bad extraction) into 0.25..1.4 blocks long. */
	private static float fitHeld(RuntimeMesh m) {
		float len = Math.max(m.sizeZ(), Math.max(m.sizeX(), m.sizeY()));
		if (len <= 0) return 1f;
		if (len > 1.4f) return 1.4f / len;
		if (len < 0.1f) return 0.1f / len;
		return 1f;
	}
}
