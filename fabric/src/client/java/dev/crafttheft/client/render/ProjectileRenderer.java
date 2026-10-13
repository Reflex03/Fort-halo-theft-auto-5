package dev.crafttheft.client.render;

import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.math.Axis;

import net.minecraft.client.renderer.LightTexture;
import net.minecraft.client.renderer.MultiBufferSource;
import net.minecraft.client.renderer.entity.EntityRenderer;
import net.minecraft.client.renderer.entity.EntityRendererProvider;
import net.minecraft.client.renderer.texture.OverlayTexture;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.util.Mth;
import net.minecraft.world.entity.Entity;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.content.SourceRows;
import dev.crafttheft.content.ModItems;
import dev.crafttheft.weapon.HaloGrenade;
import dev.crafttheft.weapon.HaloProjectile;

/**
 * hooks row entity_renderers: Halo rounds. Grenades draw their weapon mesh (frag/plasma grenade); plasma bolts,
 * needles and rockets are small glowing shapes (there is no sources row for round meshes).
 */
public class ProjectileRenderer<T extends Entity> extends EntityRenderer<T> {
	private static final ResourceLocation PLASMA = CraftTheft.id("textures/placeholder/plasma.png");
	private static final ResourceLocation NEEDLE = CraftTheft.id("textures/placeholder/needler.png");
	private static final ResourceLocation ROCKET = CraftTheft.id("textures/placeholder/gun_metal.png");
	private static final RuntimeMesh BOLT = RuntimeMesh.box(0.12f, 0.12f, 0.45f, PLASMA);
	private static final RuntimeMesh NEEDLE_MESH = RuntimeMesh.box(0.06f, 0.06f, 0.4f, NEEDLE);
	private static final RuntimeMesh ROCKET_MESH = RuntimeMesh.box(0.16f, 0.16f, 0.6f, ROCKET);

	public ProjectileRenderer(EntityRendererProvider.Context ctx) { super(ctx); }

	@Override
	public void render(T e, float entityYaw, float pt, PoseStack ps, MultiBufferSource buffers, int light) {
		ps.pushPose();
		ps.mulPose(Axis.YP.rotationDegrees(-Mth.rotLerp(pt, e.yRotO, e.getYRot())));
		ps.mulPose(Axis.XP.rotationDegrees(Mth.lerp(pt, e.xRotO, e.getXRot())));
		if (e instanceof HaloGrenade g && ModItems.weapons().get(g.weaponId()) instanceof dev.crafttheft.weapon.HaloWeaponItem w) {
			String key = SourceRows.meshKey(w.row.model());
			RuntimeMesh m = Meshes.forDraw(key, 0.15f, 0.15f, 0.15f);
			MeshDraw.draw(m, ps, buffers, light, OverlayTexture.NO_OVERLAY, 0, 0);
		} else if (e instanceof HaloProjectile p) {
			MeshDraw.draw(p.homing || p.weaponId().contains("needler") ? NEEDLE_MESH : BOLT, ps, buffers, LightTexture.FULL_BRIGHT, OverlayTexture.NO_OVERLAY, 0, 0);
		} else {
			MeshDraw.draw(ROCKET_MESH, ps, buffers, LightTexture.FULL_BRIGHT, OverlayTexture.NO_OVERLAY, 0, 0);
		}
		ps.popPose();
		super.render(e, entityYaw, pt, ps, buffers, light);
	}

	@Override public ResourceLocation getTextureLocation(T e) { return PLASMA; }
}
