package dev.crafttheft.world;

import org.jetbrains.annotations.Nullable;

import com.mojang.serialization.MapCodec;

import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.world.Containers;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.item.context.BlockPlaceContext;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.BaseEntityBlock;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.HorizontalDirectionalBlock;
import net.minecraft.world.level.block.RenderShape;
import net.minecraft.world.level.block.entity.BlockEntity;
import net.minecraft.world.level.block.entity.BlockEntityTicker;
import net.minecraft.world.level.block.entity.BlockEntityType;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.block.state.StateDefinition;
import net.minecraft.world.level.block.state.properties.DirectionProperty;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.shapes.CollisionContext;
import net.minecraft.world.phys.shapes.VoxelShape;

import dev.crafttheft.content.ModBlocks;
import dev.crafttheft.melty.MeltyChecks;

/**
 * Supply chest. Drawn by LootChestRenderer: the Fortnite chest mesh from the cache, or the vanilla chest model as
 * the placeholder (sources row fn_mesh_chest). Opens a 27-slot chest rolled from the loot sheet.
 */
public class LootChestBlock extends BaseEntityBlock {
	public static final MapCodec<LootChestBlock> CODEC = simpleCodec(LootChestBlock::new);
	public static final DirectionProperty FACING = HorizontalDirectionalBlock.FACING;
	private static final VoxelShape SHAPE = Block.box(1.0, 0.0, 1.0, 15.0, 14.0, 15.0);

	public LootChestBlock(Properties properties) {
		super(properties);
		registerDefaultState(stateDefinition.any().setValue(FACING, Direction.NORTH));
	}

	@Override protected MapCodec<? extends BaseEntityBlock> codec() { return CODEC; }
	@Override protected void createBlockStateDefinition(StateDefinition.Builder<Block, BlockState> b) { b.add(FACING); }
	@Override public BlockState getStateForPlacement(BlockPlaceContext ctx) { return defaultBlockState().setValue(FACING, ctx.getHorizontalDirection().getOpposite()); }
	@Override protected VoxelShape getShape(BlockState s, BlockGetter g, BlockPos p, CollisionContext c) { return SHAPE; }
	@Override public BlockEntity newBlockEntity(BlockPos pos, BlockState state) { return new LootChestBlockEntity(pos, state); }
	/** Drawn only by the block entity renderer (mesh or vanilla chest model). */
	@Override protected RenderShape getRenderShape(BlockState state) { return RenderShape.ENTITYBLOCK_ANIMATED; }

	@Nullable
	@Override
	public <T extends BlockEntity> BlockEntityTicker<T> getTicker(Level level, BlockState state, BlockEntityType<T> type) {
		return level.isClientSide ? createTickerHelper(type, ModBlocks.LOOT_CHEST_BE, LootChestBlockEntity::clientTick)
				: createTickerHelper(type, ModBlocks.LOOT_CHEST_BE, LootChestBlockEntity::serverTick);
	}

	@Override
	protected InteractionResult useWithoutItem(BlockState state, Level level, BlockPos pos, Player player, BlockHitResult hit) {
		if (level.isClientSide) return InteractionResult.SUCCESS;
		if (level.getBlockEntity(pos) instanceof LootChestBlockEntity be) {
			player.openMenu(be); // unpackLootTable(player) runs in createMenu -> loot is rolled on the first open
			LootChestBlockEntity.opened++;
			MeltyChecks.pass(MeltyChecks.CHEST_OPENED, "supply chest at " + pos.toShortString() + " (pool " + be.pool() + ")");
		}
		return InteractionResult.CONSUME;
	}

	@Override
	protected void onRemove(BlockState state, Level level, BlockPos pos, BlockState newState, boolean moved) {
		Containers.dropContentsOnDestroy(state, newState, level, pos);
		super.onRemove(state, level, pos, newState, moved);
	}
}
