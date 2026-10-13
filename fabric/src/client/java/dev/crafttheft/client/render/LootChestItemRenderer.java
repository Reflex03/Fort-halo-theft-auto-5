package dev.crafttheft.client.render;

import com.mojang.blaze3d.vertex.PoseStack;

import net.fabricmc.fabric.api.client.rendering.v1.BuiltinItemRendererRegistry;
import net.minecraft.client.Minecraft;
import net.minecraft.client.model.geom.ModelLayers;
import net.minecraft.client.model.geom.ModelPart;
import net.minecraft.client.renderer.MultiBufferSource;
import net.minecraft.core.Direction;
import net.minecraft.world.item.ItemDisplayContext;
import net.minecraft.world.item.ItemStack;

/** The supply chest as an item (inventory, hand): same drawing as the block (template_chest display transforms). */
public class LootChestItemRenderer implements BuiltinItemRendererRegistry.DynamicItemRenderer {
	private ModelPart lid, bottom, lock;

	@Override
	public void render(ItemStack stack, ItemDisplayContext mode, PoseStack ps, MultiBufferSource buffers, int light, int overlay) {
		if (lid == null) {
			ModelPart root = Minecraft.getInstance().getEntityModels().bakeLayer(ModelLayers.CHEST);
			bottom = root.getChild("bottom"); lid = root.getChild("lid"); lock = root.getChild("lock");
		}
		LootChestRenderer.draw(Direction.SOUTH, 0f, ps, buffers, light, overlay, lid, bottom, lock);
	}
}
