package dev.crafttheft.world;

import java.util.List;

import org.jetbrains.annotations.Nullable;

import net.minecraft.core.BlockPos;
import net.minecraft.core.HolderLookup;
import net.minecraft.core.NonNullList;
import net.minecraft.nbt.CompoundTag;
import net.minecraft.network.chat.Component;
import net.minecraft.sounds.SoundEvents;
import net.minecraft.sounds.SoundSource;
import net.minecraft.world.Container;
import net.minecraft.world.ContainerHelper;
import net.minecraft.world.entity.player.Inventory;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.inventory.AbstractContainerMenu;
import net.minecraft.world.inventory.ChestMenu;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.entity.ChestLidController;
import net.minecraft.world.level.block.entity.ContainerOpenersCounter;
import net.minecraft.world.level.block.entity.LidBlockEntity;
import net.minecraft.world.level.block.entity.RandomizableContainerBlockEntity;
import net.minecraft.world.level.block.state.BlockState;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.content.ModBlocks;
import dev.crafttheft.content.ModSounds;
import dev.crafttheft.gen.Sheets;

/** Supply chest: 27 slots rolled from the loot sheet on first open; lid animation like a vanilla chest. */
public class LootChestBlockEntity extends RandomizableContainerBlockEntity implements LidBlockEntity {
	private NonNullList<ItemStack> items = NonNullList.withSize(27, ItemStack.EMPTY);
	private String pool = "chest";
	private boolean rolled;
	public static int opened;
	private final ChestLidController lid = new ChestLidController();
	private final ContainerOpenersCounter openers = new ContainerOpenersCounter() {
		@Override protected void onOpen(Level level, BlockPos pos, BlockState state) {
			level.playSound(null, pos, ModSounds.of("fn_snd_chest_open"), SoundSource.BLOCKS, 1f, 1f);
		}
		@Override protected void onClose(Level level, BlockPos pos, BlockState state) {
			level.playSound(null, pos, SoundEvents.CHEST_CLOSE, SoundSource.BLOCKS, 0.5f, 1f);
		}
		@Override protected void openerCountChanged(Level level, BlockPos pos, BlockState state, int before, int after) {
			level.blockEvent(pos, state.getBlock(), 1, after);
		}
		@Override protected boolean isOwnContainer(Player player) {
			return player.containerMenu instanceof ChestMenu m && m.getContainer() == LootChestBlockEntity.this;
		}
	};

	public LootChestBlockEntity(BlockPos pos, BlockState state) {
		super(ModBlocks.LOOT_CHEST_BE, pos, state);
	}

	public void setPool(String pool) { this.pool = pool; setChanged(); }
	public String pool() { return pool; }
	public boolean rolled() { return rolled; }

	@Override public int getContainerSize() { return 27; }
	@Override protected Component getDefaultName() { return Component.translatable("container.crafttheft.loot_chest"); }
	@Override protected NonNullList<ItemStack> getItems() { return items; }
	@Override protected void setItems(NonNullList<ItemStack> list) { this.items = list; }
	@Override protected AbstractContainerMenu createMenu(int id, Inventory inv) { return ChestMenu.threeRows(id, inv, this); }

	/** Rolls the loot sheet the first time anyone opens (or a hopper reads) the chest. */
	@Override
	public void unpackLootTable(@Nullable Player player) {
		super.unpackLootTable(player);
		if (rolled || level == null || level.isClientSide) return;
		rolled = true;
		List<ItemStack> loot = LootRoller.roll(pool, Sheets.CHESTS_ROLLS, level.random);
		List<Integer> free = new java.util.ArrayList<>();
		for (int i = 0; i < items.size(); i++) if (items.get(i).isEmpty()) free.add(i);
		java.util.Collections.shuffle(free, new java.util.Random(level.random.nextLong()));
		for (int i = 0; i < loot.size() && i < free.size(); i++) items.set(free.get(i), loot.get(i));
		CraftTheft.LOG.info("[loot] chest at {} (pool {}) rolled {}", worldPosition.toShortString(), pool,
				loot.stream().map(s -> s.getItem().toString() + "x" + s.getCount()).toList());
		setChanged();
	}

	@Override
	public void startOpen(Player player) {
		if (!this.remove && !player.isSpectator()) openers.incrementOpeners(player, getLevel(), getBlockPos(), getBlockState());
	}

	@Override
	public void stopOpen(Player player) {
		if (!this.remove && !player.isSpectator()) openers.decrementOpeners(player, getLevel(), getBlockPos(), getBlockState());
	}

	public void recheckOpen() {
		if (!this.remove && level != null) openers.recheckOpeners(level, getBlockPos(), getBlockState());
	}

	@Override
	public boolean triggerEvent(int id, int data) {
		if (id == 1) { lid.shouldBeOpen(data > 0); return true; }
		return super.triggerEvent(id, data);
	}

	public static void clientTick(Level level, BlockPos pos, BlockState state, LootChestBlockEntity be) { be.lid.tickLid(); }

	public static void serverTick(Level level, BlockPos pos, BlockState state, LootChestBlockEntity be) {
		if (level.getGameTime() % 10 == 0) be.recheckOpen();
	}

	@Override public float getOpenNess(float partialTick) { return lid.getOpenness(partialTick); }

	@Override
	protected void loadAdditional(CompoundTag tag, HolderLookup.Provider registries) {
		super.loadAdditional(tag, registries);
		items = NonNullList.withSize(getContainerSize(), ItemStack.EMPTY);
		if (!tryLoadLootTable(tag)) ContainerHelper.loadAllItems(tag, items, registries);
		if (tag.contains("Pool")) pool = tag.getString("Pool");
		rolled = tag.getBoolean("Rolled");
	}

	@Override
	protected void saveAdditional(CompoundTag tag, HolderLookup.Provider registries) {
		super.saveAdditional(tag, registries);
		if (!trySaveLootTable(tag)) ContainerHelper.saveAllItems(tag, items, registries);
		tag.putString("Pool", pool);
		tag.putBoolean("Rolled", rolled);
	}

	public static boolean isLootChest(Container c) { return c instanceof LootChestBlockEntity; }
}
