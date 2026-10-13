package dev.crafttheft.content;

import net.minecraft.core.Registry;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.world.level.block.SoundType;
import net.minecraft.world.level.block.entity.BlockEntityType;
import net.minecraft.world.level.block.state.BlockBehaviour;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.world.LootChestBlock;
import dev.crafttheft.world.LootChestBlockEntity;

public final class ModBlocks {
	public static LootChestBlock LOOT_CHEST;
	public static BlockEntityType<LootChestBlockEntity> LOOT_CHEST_BE;

	private ModBlocks() {}

	public static void init() {
		LOOT_CHEST = Registry.register(BuiltInRegistries.BLOCK, CraftTheft.id("loot_chest"),
				new LootChestBlock(BlockBehaviour.Properties.of().strength(2.5f).sound(SoundType.WOOD).noOcclusion()));
		LOOT_CHEST_BE = Registry.register(BuiltInRegistries.BLOCK_ENTITY_TYPE, CraftTheft.id("loot_chest"),
				BlockEntityType.Builder.of(LootChestBlockEntity::new, LOOT_CHEST).build(null));
	}
}
