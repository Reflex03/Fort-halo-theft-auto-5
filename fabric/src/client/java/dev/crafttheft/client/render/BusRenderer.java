package dev.crafttheft.client.render;

import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.math.Axis;

import net.minecraft.client.renderer.MultiBufferSource;
import net.minecraft.client.renderer.entity.EntityRenderer;
import net.minecraft.client.renderer.entity.EntityRendererProvider;
import net.minecraft.client.renderer.texture.OverlayTexture;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.util.Mth;

import dev.crafttheft.bus.BattleBusEntity;

/** hooks row entity_renderers: the Fortnite Battle Bus mesh along the route heading. */
public class BusRenderer extends EntityRenderer<BattleBusEntity> {
	public BusRenderer(EntityRendererProvider.Context ctx) {
		super(ctx);
		this.shadowRadius = 0f;
	}

	@Override
	public boolean shouldRender(BattleBusEntity e, net.minecraft.client.renderer.culling.Frustum f, double x, double y, double z) {
		return true; // big mesh, small hitbox: never frustum-cull it away
	}

	@Override
	public void render(BattleBusEntity bus, float entityYaw, float pt, PoseStack ps, MultiBufferSource buffers, int light) {
		RuntimeMesh mesh = Meshes.forDraw(bus.meshKey(), 3f, 3f, 9f);
		ps.pushPose();
		ps.mulPose(Axis.YP.rotationDegrees(-Mth.rotLerp(pt, bus.yRotO, bus.getYRot())));
		MeshDraw.draw(mesh, ps, buffers, light, OverlayTexture.NO_OVERLAY, 0f, 0f);
		ps.popPose();
	}

	@Override public ResourceLocation getTextureLocation(BattleBusEntity e) { return Meshes.BOX_TEXTURE; }
}
