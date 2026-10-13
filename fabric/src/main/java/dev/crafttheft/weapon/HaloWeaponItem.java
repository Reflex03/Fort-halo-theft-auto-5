package dev.crafttheft.weapon;

import java.util.List;

import net.minecraft.ChatFormatting;
import net.minecraft.core.BlockPos;
import net.minecraft.network.chat.Component;
import net.minecraft.util.Mth;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.TooltipFlag;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.state.BlockState;

import dev.crafttheft.content.ModComponents;
import dev.crafttheft.gen.Sheets;

/**
 * A Halo 3 weapon (one per weapons sheet row). The server fires it (WeaponSystem) while the client reports the
 * trigger; ammo/reserve/reload live in data components. Drawn by WeaponItemRenderer with the weapon's mesh.
 */
public class HaloWeaponItem extends Item {
	public final Sheets.Weapon row;

	public HaloWeaponItem(Sheets.Weapon row, Properties props) {
		super(props);
		this.row = row;
	}

	public String weaponId() { return row.id(); }

	public WeaponStats stats() { return WeaponStats.get(row.id()); }

	public boolean isGrenade() { return row.kind().equals("grenade"); }

	/** A stack with a full clip and the stats' reserve (loot, creative tab). */
	public ItemStack createLoaded() {
		ItemStack s = new ItemStack(this);
		if (!isGrenade()) {
			WeaponStats st = stats();
			s.set(ModComponents.AMMO, st.clip);
			s.set(ModComponents.RESERVE, st.reserve);
			s.set(ModComponents.RELOAD, 0);
		}
		return s;
	}

	public static int ammo(ItemStack s) { return s.getOrDefault(ModComponents.AMMO, 0); }

	public static int reserve(ItemStack s) { return s.getOrDefault(ModComponents.RESERVE, 0); }

	public static int reloading(ItemStack s) { return s.getOrDefault(ModComponents.RELOAD, 0); }

	@Override
	public boolean canAttackBlock(BlockState state, Level level, BlockPos pos, Player player) {
		return false; // left click fires; never breaks blocks
	}

	/** Ammo changes every shot; without this the re-equip bob would replay each time. */
	@Override
	public boolean allowComponentsUpdateAnimation(Player player, InteractionHand hand, ItemStack oldStack, ItemStack newStack) {
		return false;
	}

	@Override
	public boolean isBarVisible(ItemStack stack) {
		return !isGrenade() && stack.has(ModComponents.AMMO);
	}

	@Override
	public int getBarWidth(ItemStack stack) {
		return Math.round(13f * Mth.clamp(ammo(stack) / (float) Math.max(1, stats().clip), 0f, 1f));
	}

	@Override
	public int getBarColor(ItemStack stack) {
		return 0x40B8FF;
	}

	@Override
	public void appendHoverText(ItemStack stack, TooltipContext ctx, List<Component> lines, TooltipFlag flag) {
		WeaponStats s = stats();
		lines.add(Component.literal(String.format("%s, %.1f dmg%s", s.kind, s.mcDamage(), s.pellets > 1 ? " x" + s.pellets : "")).withStyle(ChatFormatting.GRAY));
		if (!isGrenade()) lines.add(Component.literal(ammo(stack) + " / " + reserve(stack)).withStyle(ChatFormatting.DARK_AQUA));
		if (!s.fromCache.isEmpty()) lines.add(Component.literal("stats from your Halo install").withStyle(ChatFormatting.DARK_GREEN));
	}
}
