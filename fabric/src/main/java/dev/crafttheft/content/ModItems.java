package dev.crafttheft.content;

import java.util.LinkedHashMap;
import java.util.Map;

import org.jetbrains.annotations.Nullable;

import net.fabricmc.fabric.api.itemgroup.v1.FabricItemGroup;
import net.minecraft.core.Registry;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.network.chat.Component;
import net.minecraft.world.item.BlockItem;
import net.minecraft.world.item.CreativeModeTab;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Rarity;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.heal.FortniteHealItem;
import dev.crafttheft.weapon.HaloWeaponItem;

/** One HaloWeaponItem per weapons row, one FortniteHealItem per consumables row, the supply chest item, a creative tab. */
public final class ModItems {
	private static final Map<String, HaloWeaponItem> WEAPONS = new LinkedHashMap<>();
	private static final Map<String, FortniteHealItem> HEALS = new LinkedHashMap<>();
	public static BlockItem LOOT_CHEST;

	private ModItems() {}

	public static Rarity rarity(String r) {
		return switch (r) {
			case "uncommon" -> Rarity.UNCOMMON;
			case "rare" -> Rarity.RARE;
			case "epic", "legendary" -> Rarity.EPIC;
			default -> Rarity.COMMON;
		};
	}

	public static void init() {
		for (Sheets.Weapon w : Sheets.WEAPONS) {
			int stack = w.kind().equals("grenade") ? 4 : 1; // Halo carries a few of each grenade
			WEAPONS.put(w.id(), Registry.register(BuiltInRegistries.ITEM, CraftTheft.id(w.id()),
					new HaloWeaponItem(w, new Item.Properties().stacksTo(stack).rarity(rarity(w.rarity())))));
		}
		for (Sheets.Consumable c : Sheets.CONSUMABLES) {
			HEALS.put(c.id(), Registry.register(BuiltInRegistries.ITEM, CraftTheft.id(c.id()),
					new FortniteHealItem(c, new Item.Properties().stacksTo(c.maxStack()).rarity(rarity(c.rarity())))));
		}
		LOOT_CHEST = Registry.register(BuiltInRegistries.ITEM, CraftTheft.id("loot_chest"), new BlockItem(ModBlocks.LOOT_CHEST, new Item.Properties()));
		Registry.register(BuiltInRegistries.CREATIVE_MODE_TAB, CraftTheft.id("main"), FabricItemGroup.builder()
				.title(Component.translatable("itemGroup.crafttheft"))
				.icon(() -> new ItemStack(WEAPONS.values().iterator().next()))
				.displayItems((params, out) -> {
					for (HaloWeaponItem w : WEAPONS.values()) out.accept(w.createLoaded());
					for (FortniteHealItem h : HEALS.values()) out.accept(h);
					out.accept(LOOT_CHEST);
				}).build());
	}

	public static HaloWeaponItem weapon(String id) {
		HaloWeaponItem w = WEAPONS.get(id);
		if (w == null) throw new IllegalArgumentException("no weapons row " + id);
		return w;
	}

	public static FortniteHealItem heal(String id) {
		FortniteHealItem h = HEALS.get(id);
		if (h == null) throw new IllegalArgumentException("no consumables row " + id);
		return h;
	}

	public static Map<String, HaloWeaponItem> weapons() { return WEAPONS; }

	public static Map<String, FortniteHealItem> heals() { return HEALS; }

	/** A weapons or consumables row id -> its item (loot rows refer to either). */
	@Nullable
	public static Item byRowId(String id) {
		Item i = WEAPONS.get(id);
		return i != null ? i : HEALS.get(id);
	}
}
