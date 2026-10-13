package dev.crafttheft.client;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.List;

import net.fabricmc.loader.api.FabricLoader;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.Hooks;

/**
 * hooks row first_options: runs from the ClientModInitializer, which Fabric calls before Minecraft creates its
 * Options (so before options.txt is read). A fresh instance gets sensible defaults, above all
 * onboardAccessibility:false so the first start goes straight to the title screen; an existing options.txt that
 * lacks the onboarding key gets just that key appended. Nothing else in an existing file is touched.
 */
public final class FirstRun {
	/** Defaults for a fresh instance (1.21.1 option keys; version = DataVersion 3955 so no options datafix runs). */
	public static final List<String> DEFAULTS = List.of(
			"version:3955",
			"onboardAccessibility:false",
			"tutorialStep:none",
			"skipMultiplayerWarning:true",
			"joinedFirstServer:true",
			"renderDistance:10",
			"simulationDistance:8",
			"maxFps:120",
			"soundCategory_music:0.3",
			"lang:en_us");
	public static String outcome = "not run";

	private FirstRun() {}

	public static void writeOptions() {
		Hooks.mark("first_options", "FirstRun.writeOptions");
		Path file = FabricLoader.getInstance().getGameDir().resolve("options.txt");
		try {
			if (!Files.exists(file)) {
				Files.createDirectories(file.getParent());
				Files.write(file, DEFAULTS, StandardCharsets.UTF_8);
				outcome = "wrote defaults";
			} else {
				List<String> lines = Files.readAllLines(file, StandardCharsets.UTF_8);
				if (lines.stream().noneMatch(l -> l.startsWith("onboardAccessibility:"))) {
					Files.writeString(file, System.lineSeparator() + "onboardAccessibility:false" + System.lineSeparator(), StandardCharsets.UTF_8, StandardOpenOption.APPEND);
					outcome = "appended onboardAccessibility:false";
				} else outcome = "left as is";
			}
			CraftTheft.LOG.info("[first-run] {}: {}", file, outcome);
		} catch (Exception e) {
			outcome = "failed: " + e;
			CraftTheft.LOG.warn("[first-run] could not write {}: {}", file, e.toString());
		}
	}
}
