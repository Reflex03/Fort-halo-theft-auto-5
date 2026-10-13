package dev.crafttheft;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

import net.fabricmc.api.ModInitializer;
import net.minecraft.resources.ResourceLocation;

import dev.crafttheft.bus.BattleBus;
import dev.crafttheft.config.ModConfig;
import dev.crafttheft.content.ModAttachments;
import dev.crafttheft.content.ModBlocks;
import dev.crafttheft.content.ModComponents;
import dev.crafttheft.content.ModEntities;
import dev.crafttheft.content.ModItems;
import dev.crafttheft.content.ModSounds;
import dev.crafttheft.crime.CrimeSystem;
import dev.crafttheft.melty.MeltyChecks;
import dev.crafttheft.net.Payloads;
import dev.crafttheft.shield.ShieldSystem;
import dev.crafttheft.world.WorldSetup;

/** Craft Theft Halo: common (both sides) entrypoint. Every system is driven by the generated Sheets class. */
public class CraftTheft implements ModInitializer {
	public static final String MODID = "crafttheft";
	public static final Logger LOG = LoggerFactory.getLogger(MODID);

	public static ResourceLocation id(String path) {
		return ResourceLocation.fromNamespaceAndPath(MODID, path);
	}

	@Override
	public void onInitialize() {
		MeltyChecks.start();
		ModConfig.reload();
		ModSounds.init();
		ModComponents.init();
		ModAttachments.init();
		ModBlocks.init();
		ModEntities.init();
		ModItems.init();
		Payloads.register();
		CrimeSystem.register();
		ShieldSystem.register();
		GameTick.register();
		WorldSetup.register();
		BattleBus.register();
		LOG.info("[crafttheft] initialised: {} weapons, {} consumables, {} vehicles, {} loot rows, {} crimes, {} wanted levels, {} sources; hooks {}",
				dev.crafttheft.gen.Sheets.WEAPONS.size(), dev.crafttheft.gen.Sheets.CONSUMABLES.size(), dev.crafttheft.gen.Sheets.VEHICLES.size(),
				dev.crafttheft.gen.Sheets.LOOT.size(), dev.crafttheft.gen.Sheets.CRIMES.size(), dev.crafttheft.gen.Sheets.WANTED.size(),
				dev.crafttheft.gen.Sheets.SOURCES.size(), Hooks.registered().keySet());
	}
}
