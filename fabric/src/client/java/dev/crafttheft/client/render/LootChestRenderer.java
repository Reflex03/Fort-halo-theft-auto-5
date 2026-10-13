package dev.crafttheft.client.render;

import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.blaze3d.vertex.VertexConsumer;
import com.mojang.math.Axis;

import net.minecraft.client.model.geom.ModelLayers;
import net.minecraft.client.model.geom.ModelPart;
import net.minecraft.client.renderer.MultiBufferSource;
import net.minecraft.client.renderer.RenderType;
import net.minecraft.client.renderer.blockentity.BlockEntityRenderer;
import net.minecraft.client.renderer.blockentity.BlockEntityRendererProvider;
import net.minecraft.core.Direction;

import dev.crafttheft.content.SourceRows;
import dev.crafttheft.world.LootChestBlock;
import dev.crafttheft.world.LootChestBlockEntity;

/**
 * hooks row chest_renderer: the Fortnite chest mesh from the cache (true size, front +Z, lowest point y=0; scaled
 * down if it would not fit about a block and a half), or, as the sources sheet's placeholder, the vanilla chest model
 * with its opening lid.
 */
public class LootChestRenderer implements BlockEntityRenderer<LootChestBlockEntity> {
	public static final String MESH_SOURCE = "fn_mesh_chest";
	private final ModelPart lid, bottom, lock;

	public LootChestRenderer(BlockEntityRendererProvider.Context ctx) {
		ModelPart root = ctx.bakeLayer(ModelLayers.CHEST);
		this.bottom = root.getChild("bottom");
		this.lid = root.getChild("lid");
		this.lock = root.getChild("lock");
	}

	@Override
	public void render(LootChestBlockEntity be, float pt, PoseStack ps, MultiBufferSource buffers, int light, int overlay) {
		Direction facing = be.getBlockState().hasProperty(LootChestBlock.FACING) ? be.getBlockState().getValue(LootChestBlock.FACING) : Direction.SOUTH;
		draw(facing, be.getOpenNess(pt), ps, buffers, light, overlay, this.lid, this.bottom, this.lock);
	}

	public static void draw(Direction facing, float openness, PoseStack ps, MultiBufferSource buffers, int light, int overlay, ModelPart lid, ModelPart bottom, ModelPart lock) {
		String key = SourceRows.meshKey(MESH_SOURCE);
		RuntimeMesh mesh = MeshCache.get(key);
		ps.pushPose();
		ps.translate(0.5f, 0f, 0.5f);
		ps.mulPose(Axis.YP.rotationDegrees(-facing.toYRot()));
		if (mesh != null) {
			ContentChecks.drawn(key, mesh);
			float big = Math.max(mesh.sizeX(), mesh.sizeZ());
			float s = big > 1.5f ? 1.5f / big : 1f;
			ps.scale(s, s, s);
			ps.translate(-(mesh.minX + mesh.maxX) / 2f, -mesh.minY, -(mesh.minZ + mesh.maxZ) / 2f);
			MeshDraw.draw(mesh, ps, buffers, light, overlay, 0f, 0f);
		} else {
			// vanilla chest model (placeholder): same geometry and texture as ChestRenderer
			ps.translate(-0.5f, 0f, -0.5f);
			float h = 1.0f - openness;
			h = 1.0f - h * h * h;
			VertexConsumer vc = net.minecraft.client.renderer.Sheets.CHEST_LOCATION.buffer(buffers, RenderType::entityCutout);
			lid.xRot = -(h * ((float) Math.PI / 2f));
			lock.xRot = lid.xRot;
			lid.render(ps, vc, light, overlay);
			lock.render(ps, vc, light, overlay);
			bottom.render(ps, vc, light, overlay);
		}
		ps.popPose();
	}
}
