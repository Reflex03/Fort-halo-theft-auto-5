package dev.crafttheft.client.render;

import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.math.Axis;

import net.minecraft.client.renderer.MultiBufferSource;
import net.minecraft.client.renderer.entity.EntityRenderer;
import net.minecraft.client.renderer.entity.EntityRendererProvider;
import net.minecraft.client.renderer.texture.OverlayTexture;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.util.Mth;

import dev.crafttheft.vehicle.CarEntity;

/** hooks row entity_renderers: a GTA V car mesh (front +Z = entity yaw 0), spinning/steering wheel nodes. */
public class CarRenderer extends EntityRenderer<CarEntity> {
	public CarRenderer(EntityRendererProvider.Context ctx) {
		super(ctx);
		this.shadowRadius = 1.0f;
	}

	@Override
	public void render(CarEntity car, float entityYaw, float pt, PoseStack ps, MultiBufferSource buffers, int light) {
		RuntimeMesh mesh = Meshes.forDraw(car.meshKey(), car.getBbWidth(), car.getBbHeight(), car.getBbWidth() * 2.2f);
		ps.pushPose();
		ps.mulPose(Axis.YP.rotationDegrees(-Mth.rotLerp(pt, car.yRotO, car.getYRot())));
		MeshDraw.draw(mesh, ps, buffers, light, OverlayTexture.NO_OVERLAY, car.wheelSpin(pt), car.steerAngle());
		ps.popPose();
		super.render(car, entityYaw, pt, ps, buffers, light);
	}

	@Override public ResourceLocation getTextureLocation(CarEntity e) { return Meshes.BOX_TEXTURE; }
}
