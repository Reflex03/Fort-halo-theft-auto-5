package dev.crafttheft.content;

import net.minecraft.core.Registry;
import net.minecraft.core.component.DataComponentType;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.network.codec.ByteBufCodecs;
import net.minecraft.util.ExtraCodecs;

import dev.crafttheft.CraftTheft;

/** Ammo state lives on the weapon stack (persistent + synced to the client for the HUD). */
public final class ModComponents {
	public static DataComponentType<Integer> AMMO, RESERVE, RELOAD;

	private ModComponents() {}

	public static void init() {
		AMMO = component("ammo");
		RESERVE = component("reserve");
		RELOAD = component("reload");
	}

	private static DataComponentType<Integer> component(String name) {
		return Registry.register(BuiltInRegistries.DATA_COMPONENT_TYPE, CraftTheft.id(name),
				DataComponentType.<Integer>builder().persistent(ExtraCodecs.NON_NEGATIVE_INT).networkSynchronized(ByteBufCodecs.VAR_INT).build());
	}
}
