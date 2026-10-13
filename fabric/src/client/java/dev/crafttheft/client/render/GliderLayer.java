package dev.crafttheft.client.render;

import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.math.Axis;

import net.minecraft.client.model.PlayerModel;
import net.minecraft.client.player.AbstractClientPlayer;
import net.minecraft.client.renderer.MultiBufferSource;
import net.minecraft.client.renderer.entity.RenderLayerParent;
import net.minecraft.client.renderer.entity.layers.RenderLayer;
import net.minecraft.client.renderer.texture.OverlayTexture;

import dev.crafttheft.bus.Skydive;
import dev.crafttheft.content.ModAttachments;
import dev.crafttheft.content.SourceRows;

/**
 * Player render layer: the Fortnite glider mesh while gliding. The mesh origin is the attach point on the player's
 * back and its wing span runs along X (docs/CONTRACTS.md), so it is drawn from the back of the torso.
 */
public class GliderLayer extends RenderLayer<AbstractClientPlayer, PlayerModel<AbstractClientPlayer>> {
	public static final String MESH_SOURCE = "fn_mesh_glider";

	public GliderLayer(RenderLayerParent<AbstractClientPlayer, PlayerModel<AbstractClientPlayer>> parent) {
		super(parent);
	}

	@Override
	public void render(PoseStack ps, MultiBufferSource buffers, int light, AbstractClientPlayer player, float limbSwing, float limbSwingAmount,
					   float partialTick, float ageInTicks, float netHeadYaw, float headPitch) {
		if (player.getAttachedOrElse(ModAttachments.GLIDE, 0) != Skydive.GLIDE) return;
		RuntimeMesh mesh = Meshes.forDraw(SourceRows.meshKey(MESH_SOURCE), 3.0f, 0.1f, 1.2f);
		ps.pushPose();
		// layer space = model space: Y down, origin at the neck (~1.5 above the feet), front is -Z, back is +Z
		ps.translate(0f, 0.25f, 0.16f);
		ps.scale(-1f, -1f, 1f);
		ps.mulPose(Axis.YP.rotationDegrees(180f)); // mesh front (+Z) faces where the player faces
		MeshDraw.draw(mesh, ps, buffers, light, OverlayTexture.NO_OVERLAY, 0f, 0f);
		ps.popPose();
	}
}
