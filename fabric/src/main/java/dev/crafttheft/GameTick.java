package dev.crafttheft;

import net.fabricmc.fabric.api.event.lifecycle.v1.ServerLifecycleEvents;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.fabricmc.fabric.api.networking.v1.ServerPlayConnectionEvents;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;

import dev.crafttheft.bus.BattleBus;
import dev.crafttheft.bus.Skydive;
import dev.crafttheft.crime.CrimeSystem;
import dev.crafttheft.crime.PoliceDispatch;
import dev.crafttheft.crime.WantedSystem;
import dev.crafttheft.shield.ShieldSystem;
import dev.crafttheft.weapon.WeaponSystem;
import dev.crafttheft.world.WorldSetup;

/** hooks row server_tick: wanted/police, shield recharge, bus boarding, skydive, weapons, world placement. */
public final class GameTick {
	private GameTick() {}

	public static void register() {
		ServerTickEvents.END_SERVER_TICK.register(GameTick::tick);
		Hooks.mark("server_tick", "GameTick.tick");
		ServerPlayConnectionEvents.DISCONNECT.register((handler, server) -> {
			ServerPlayer p = handler.player;
			WeaponSystem.forget(p);
			ShieldSystem.forget(p);
			WantedSystem.forget(p);
			PoliceDispatch.forget(p);
			Skydive.forget(p);
		});
		// The systems keep per-player/per-world maps in statics (keyed by UUID, game time, block positions). A
		// singleplayer client opens several worlds in one JVM, so everything is dropped when a server stops: queued
		// village street cars, pending bus boardings or crime timestamps of world A must never act in world B.
		ServerLifecycleEvents.SERVER_STOPPED.register(server -> clearWorldState());
		Hooks.mark("server_stopped", "GameTick.clearWorldState");
	}

	public static void clearWorldState() {
		BattleBus.clear();
		WorldSetup.clear();
		WeaponSystem.clear();
		ShieldSystem.clear();
		WantedSystem.clear();
		PoliceDispatch.clear();
		CrimeSystem.clear();
		Skydive.clear();
	}

	public static void tick(MinecraftServer server) {
		BattleBus.tick(server);
		WorldSetup.tick(server);
		for (ServerPlayer p : server.getPlayerList().getPlayers()) {
			ShieldSystem.tick(p);
			WantedSystem.tick(p);
			PoliceDispatch.tick(p);
			Skydive.tick(p);
			WeaponSystem.tick(p);
		}
	}
}
