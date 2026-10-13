package dev.crafttheft.bus;

import java.util.HashMap;
import java.util.Iterator;
import java.util.Map;
import java.util.UUID;

import net.fabricmc.fabric.api.networking.v1.ServerPlayConnectionEvents;
import net.minecraft.core.BlockPos;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.level.GameType;
import net.minecraft.world.phys.Vec3;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.Hooks;
import dev.crafttheft.content.ModEntities;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.world.WorldState;

/**
 * hooks row player_join: on a player's first join in a world they board the Battle Bus, which flies a straight
 * route of systems.battle_bus.routeLength blocks centred on world spawn at the bus altitude. Leaving the bus (jump,
 * sneak or the end of the route) starts the skydive; the world remembers who has dropped.
 */
public final class BattleBus {
	/** Players waiting to board (joined, client still loading): uuid -> server tick to board at. */
	private static final Map<UUID, Long> PENDING = new HashMap<>();
	public static final int BOARD_DELAY_TICKS = 20;
	public static boolean ENABLED = true;

	private BattleBus() {}

	public static void register() {
		ServerPlayConnectionEvents.JOIN.register((handler, sender, server) -> onJoin(handler.player, server));
		Hooks.mark("player_join", "BattleBus.onJoin");
		Hooks.mark("bus_jump", "C2S crafttheft:bus_jump -> BattleBusEntity.requestDrop (+ LivingEntityAccessor jumping)");
	}

	public static void onJoin(ServerPlayer p, MinecraftServer server) {
		ServerLevel overworld = server.overworld();
		if (!ENABLED || p.level() != overworld || p.gameMode.getGameModeForPlayer() == GameType.SPECTATOR) return;
		if (WorldState.get(overworld).hasDropped(p.getUUID()) || p.getVehicle() instanceof BattleBusEntity) return;
		PENDING.put(p.getUUID(), overworld.getGameTime() + BOARD_DELAY_TICKS);
		CraftTheft.LOG.info("[bus] {} joins for the first time: boarding the Battle Bus", p.getName().getString());
	}

	/** Called from GameTick. */
	public static void tick(MinecraftServer server) {
		if (PENDING.isEmpty()) return;
		ServerLevel level = server.overworld();
		for (Iterator<Map.Entry<UUID, Long>> it = PENDING.entrySet().iterator(); it.hasNext(); ) {
			var e = it.next();
			if (level.getGameTime() < e.getValue()) continue;
			it.remove();
			ServerPlayer p = server.getPlayerList().getPlayer(e.getKey());
			// re-checked here: the drop may have been recorded since the join (e.g. a reconnect restored a bus ride)
			if (p != null && p.isAlive() && !p.isPassenger() && p.level() == level && !WorldState.get(level).hasDropped(p.getUUID())) board(p, level);
		}
	}

	/** Route: direction from the world seed, centred on spawn. Returns {start, unit direction}. */
	public static Vec3[] route(ServerLevel level) {
		BlockPos spawn = level.getSharedSpawnPos();
		double ang = (level.getSeed() * 0x9E3779B97F4A7C15L >>> 11) / (double) (1L << 53) * Math.PI * 2;
		Vec3 dir = new Vec3(Math.cos(ang), 0, Math.sin(ang));
		Vec3 centre = new Vec3(spawn.getX() + 0.5, Sheets.BATTLE_BUS_ALTITUDE, spawn.getZ() + 0.5);
		return new Vec3[]{centre.subtract(dir.scale(Sheets.BATTLE_BUS_ROUTE_LENGTH / 2.0)), dir};
	}

	public static BattleBusEntity board(ServerPlayer p, ServerLevel level) {
		Vec3[] r = route(level);
		Vec3 start = r[0], dir = r[1];
		BattleBusEntity bus = ModEntities.BUS.create(level);
		if (bus == null) return null;
		float yaw = (float) Math.toDegrees(Math.atan2(-dir.x, dir.z));
		bus.moveTo(start.x, start.y, start.z, yaw, 0);
		bus.setRoute(Sheets.BATTLE_BUS_ROUTE_LENGTH);
		level.addFreshEntity(bus);
		p.teleportTo(level, start.x, start.y + 1, start.z, yaw, 0);
		boolean ok = p.startRiding(bus, true);
		CraftTheft.LOG.info("[bus] {} boarded bus {} at {} heading {} deg ({} blocks to fly): {}", p.getName().getString(), bus.getId(),
				bus.blockPosition(), (int) yaw, Sheets.BATTLE_BUS_ROUTE_LENGTH, ok);
		return bus;
	}

	/** From BattleBusEntity.removePassenger: any way off the bus is a drop. */
	static void onDropped(ServerPlayer p, BattleBusEntity bus, Vec3 velocity) {
		WorldState.get(p.serverLevel()).markDropped(p.getUUID());
		if (p.isAlive()) Skydive.start(p, velocity);
	}

	public static boolean pending(UUID id) { return PENDING.containsKey(id); }

	public static void clear() { PENDING.clear(); }
}
