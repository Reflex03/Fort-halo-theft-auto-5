package dev.crafttheft.world;

import java.util.ArrayList;
import java.util.List;

import net.minecraft.util.RandomSource;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.content.ModItems;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.weapon.HaloWeaponItem;

/** Loot sheet -> chest contents: systems.chests.rolls weighted picks from the chest's pool; guns come loaded. */
public final class LootRoller {
	private LootRoller() {}

	public static List<ItemStack> roll(String pool, int rolls, RandomSource rnd) {
		List<Sheets.Loot> rows = Sheets.LOOT.stream().filter(r -> r.pool().equals(pool) && r.weight() > 0).toList();
		List<ItemStack> out = new ArrayList<>();
		if (rows.isEmpty()) {
			CraftTheft.LOG.warn("[loot] pool '{}' has no rows in the loot sheet", pool);
			return out;
		}
		int total = rows.stream().mapToInt(Sheets.Loot::weight).sum();
		for (int i = 0; i < rolls; i++) {
			int pick = rnd.nextInt(total);
			Sheets.Loot row = rows.get(rows.size() - 1);
			for (Sheets.Loot r : rows) {
				if (pick < r.weight()) { row = r; break; }
				pick -= r.weight();
			}
			Item item = ModItems.byRowId(row.item());
			if (item == null) { CraftTheft.LOG.warn("[loot] loot row item '{}' is not a registered item", row.item()); continue; }
			int count = row.countMin() + (row.countMax() > row.countMin() ? rnd.nextInt(row.countMax() - row.countMin() + 1) : 0);
			ItemStack st = item instanceof HaloWeaponItem w ? w.createLoaded() : new ItemStack(item);
			st.setCount(Math.max(1, Math.min(count, st.getMaxStackSize())));
			out.add(st);
		}
		return out;
	}

	/** Distinct pools named in the loot sheet. */
	public static List<String> pools() {
		return Sheets.LOOT.stream().map(Sheets.Loot::pool).distinct().toList();
	}
}
