package dev.crafttheft.world;

import java.util.ArrayDeque;
import java.util.Deque;

import net.fabricmc.fabric.api.event.lifecycle.v1.ServerChunkEvents;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerLifecycleEvents;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.core.registries.Registries;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.tags.StructureTags;
import net.minecraft.util.Mth;
import net.minecraft.util.RandomSource;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.chunk.LevelChunk;
import net.minecraft.world.level.levelgen.Heightmap;
import net.minecraft.world.level.levelgen.structure.BoundingBox;
import net.minecraft.world.level.levelgen.structure.PoolElementStructurePiece;
import net.minecraft.world.level.levelgen.structure.StructurePiece;
import net.minecraft.world.level.levelgen.structure.StructureStart;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.Hooks;
import dev.crafttheft.content.ModBlocks;
import dev.crafttheft.content.ModEntities;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.shield.ShieldTuning;
import dev.crafttheft.vehicle.CarEntity;
import dev.crafttheft.weapon.WeaponStats;

/**
 * hooks rows server_started and chunk_generate. Once per world, systems.chests.count supply chests and
 * systems.cars.parkedCount parked cars are planned around spawn (seeded, kept in WorldState); each is placed when its
 * chunk is loaded, so world start never force-generates terrain. Village streets get extra parked cars.
 */
public final class WorldSetup {
	private record Street(BlockPos pos, float yaw, int age) {}

	private static final Deque<Street> STREETS = new ArrayDeque<>();
	public static float STREET_CHANCE = dev.crafttheft.gen.Sheets.CARS_STREET_CHANCE;
	public static int MAX_PER_VILLAGE = dev.crafttheft.gen.Sheets.CARS_MAX_PER_VILLAGE;
	public static int chestsPlaced, carsPlaced, streetCars;

	private WorldSetup() {}

	public static void register() {
		ServerLifecycleEvents.SERVER_STARTED.register(WorldSetup::onServerStarted);
		Hooks.mark("server_started", "WorldSetup.onServerStarted");
		ServerChunkEvents.CHUNK_GENERATE.register(WorldSetup::onChunkGenerated);
		Hooks.mark("chunk_generate", "WorldSetup.onChunkGenerated (ServerChunkEvents.CHUNK_GENERATE)");
	}

	public static void onServerStarted(MinecraftServer server) {
		WeaponStats.reload();
		ShieldTuning.reload();
		ServerLevel level = server.overworld();
		WorldState st = WorldState.get(level);
		if (st.planned) {
			CraftTheft.LOG.info("[world] plan exists: {} chests ({} placed), {} cars ({} placed)", st.chests.size(),
					st.chests.stream().filter(p -> p.done).count(), st.cars.size(), st.cars.stream().filter(p -> p.done).count());
			return;
		}
		BlockPos spawn = level.getSharedSpawnPos();
		RandomSource rnd = RandomSource.create(level.getSeed() ^ 0x5EEDC4E57L);
		boolean hasRare = LootRoller.pools().contains("rare_chest");
		for (int i = 0; i < Sheets.CHESTS_COUNT; i++) {
			double a = rnd.nextDouble() * Math.PI * 2, r = Mth.lerp(Math.sqrt(rnd.nextDouble()), 10, Sheets.CHESTS_RADIUS);
			String pool = hasRare && i % 8 == 7 ? "rare_chest" : "chest";
			st.chests.add(new WorldState.Plan(spawn.getX() + (int) Math.round(Math.cos(a) * r), spawn.getZ() + (int) Math.round(Math.sin(a) * r), pool, rnd.nextInt(4) * 90f));
		}
		int totalWeight = Sheets.VEHICLES.stream().mapToInt(Sheets.Vehicle::spawnWeight).sum();
		for (int i = 0; i < Sheets.CARS_PARKED_COUNT && totalWeight > 0; i++) {
			int pick = rnd.nextInt(totalWeight);
			Sheets.Vehicle v = null;
			for (Sheets.Vehicle x : Sheets.VEHICLES) {
				if (pick < x.spawnWeight()) { v = x; break; }
				pick -= x.spawnWeight();
			}
			if (v == null) continue;
			double a = rnd.nextDouble() * Math.PI * 2, r = Mth.lerp(Math.sqrt(rnd.nextDouble()), 12, Sheets.CARS_RADIUS);
			st.cars.add(new WorldState.Plan(spawn.getX() + (int) Math.round(Math.cos(a) * r), spawn.getZ() + (int) Math.round(Math.sin(a) * r), v.id(), rnd.nextFloat() * 360f));
		}
		st.planned = true;
		st.setDirty();
		CraftTheft.LOG.info("[world] planned {} supply chests (radius {}) and {} parked cars (radius {}) around spawn {}",
				st.chests.size(), Sheets.CHESTS_RADIUS, st.cars.size(), Sheets.CARS_RADIUS, spawn.toShortString());
	}

	/** From GameTick: materialise planned chests/cars and queued street cars whose chunks are loaded. */
	public static void tick(MinecraftServer server) {
		ServerLevel level = server.overworld();
		if (level.getGameTime() % 5 != 0) return;
		WorldState st = WorldState.get(level);
		int budget = 4;
		for (int i = 0; i < st.chests.size(); i++) {
			WorldState.Plan p = st.chests.get(i);
			if (p.done || budget <= 0 || !level.hasChunk(p.x >> 4, p.z >> 4)) continue;
			budget--;
			if (!placeChest(level, p)) st.chests.set(i, moveOrGiveUp(level, p, Sheets.CHESTS_RADIUS));
			st.setDirty();
		}
		for (int i = 0; i < st.cars.size(); i++) {
			WorldState.Plan p = st.cars.get(i);
			if (p.done || budget <= 0 || !level.hasChunk(p.x >> 4, p.z >> 4)) continue;
			budget--;
			if (!placeCar(level, p)) st.cars.set(i, moveOrGiveUp(level, p, Sheets.CARS_RADIUS));
			st.setDirty();
		}
		drainStreets(level);
	}

	public static final int MAX_MOVES = 6;

	/**
	 * The planned spot was water or blocked: plan it again somewhere else around spawn (it is placed when that chunk
	 * loads), up to MAX_MOVES times, so watery worlds still get their chests and cars.
	 */
	private static WorldState.Plan moveOrGiveUp(ServerLevel level, WorldState.Plan p, int radius) {
		if (p.moves >= MAX_MOVES) {
			p.done = true;
			CraftTheft.LOG.info("[world] {} planned at {} {}: no usable spot after {} moves; given up", p.kind, p.x, p.z, p.moves);
			return p;
		}
		BlockPos spawn = level.getSharedSpawnPos();
		double a = level.random.nextDouble() * Math.PI * 2, r = Mth.lerp(Math.sqrt(level.random.nextDouble()), 12, radius);
		WorldState.Plan n = p.movedTo(spawn.getX() + (int) Math.round(Math.cos(a) * r), spawn.getZ() + (int) Math.round(Math.sin(a) * r));
		CraftTheft.LOG.info("[world] {} planned at {} {} has no dry solid spot: moved to {} {} (move {})", p.kind, p.x, p.z, n.x, n.z, n.moves);
		return n;
	}

	private static boolean placeChest(ServerLevel level, WorldState.Plan p) {
		for (int t = 0; t < 24; t++) {
			int x = p.x + (t == 0 ? 0 : level.random.nextInt(17) - 8), z = p.z + (t == 0 ? 0 : level.random.nextInt(17) - 8);
			if (!level.hasChunk(x >> 4, z >> 4)) continue;
			int y = level.getHeight(Heightmap.Types.MOTION_BLOCKING_NO_LEAVES, x, z);
			BlockPos pos = new BlockPos(x, y, z);
			BlockState below = level.getBlockState(pos.below());
			if (!below.isSolid() || !level.getFluidState(pos).isEmpty() || !level.getFluidState(pos.below()).isEmpty() || !level.getBlockState(pos).canBeReplaced()) continue;
			Direction facing = Direction.from2DDataValue(Math.floorMod((int) (p.yaw / 90f), 4));
			level.setBlock(pos, ModBlocks.LOOT_CHEST.defaultBlockState().setValue(LootChestBlock.FACING, facing), 3);
			if (level.getBlockEntity(pos) instanceof LootChestBlockEntity be) be.setPool(p.kind);
			p.y = y;
			p.done = true;
			chestsPlaced++;
			CraftTheft.LOG.info("[world] supply chest ({}) at {}", p.kind, pos.toShortString());
			return true;
		}
		return false;
	}

	private static boolean placeCar(ServerLevel level, WorldState.Plan p) {
		if (!ModEntities.cars().containsKey(p.kind)) {
			CraftTheft.LOG.warn("[world] planned car '{}' at {} {} is not a vehicles row any more; dropped", p.kind, p.x, p.z);
			p.done = true;
			return true;
		}
		for (int t = 0; t < 24; t++) {
			int x = p.x + (t == 0 ? 0 : level.random.nextInt(17) - 8), z = p.z + (t == 0 ? 0 : level.random.nextInt(17) - 8);
			CarEntity car = spawnCar(level, p.kind, x, z, p.yaw);
			if (car != null) { carsPlaced++; p.done = true; p.y = car.getBlockY(); return true; }
		}
		return false;
	}

	static CarEntity spawnCar(ServerLevel level, String vehicleId, int x, int z, float yaw) {
		if (!level.hasChunk(x >> 4, z >> 4)) return null;
		if (!ModEntities.cars().containsKey(vehicleId)) return null; // a plan saved by an older sheet
		int y = level.getHeight(Heightmap.Types.MOTION_BLOCKING_NO_LEAVES, x, z);
		BlockPos below = new BlockPos(x, y - 1, z);
		if (!level.getFluidState(below).isEmpty() || !level.getBlockState(below).isSolid()) return null;
		CarEntity car = ModEntities.car(vehicleId).create(level);
		if (car == null) return null;
		car.moveTo(x + 0.5, y, z + 0.5, yaw, 0f);
		if (!level.noCollision(car)) return null;
		level.addFreshEntity(car);
		CraftTheft.LOG.info("[world] parked {} at {}", vehicleId, car.blockPosition().toShortString());
		return car;
	}

	/**
	 * hooks row chunk_generate: the village StructureStart (in its origin chunk) lists every jigsaw piece; street
	 * pieces give road positions. They may lie in chunks that are not generated yet, so spawns are queued.
	 */
	public static void onChunkGenerated(ServerLevel level, LevelChunk chunk) {
		if (level.dimension() != Level.OVERWORLD) return;
		var structures = level.registryAccess().registryOrThrow(Registries.STRUCTURE);
		for (var e : chunk.getAllStarts().entrySet()) {
			StructureStart start = e.getValue();
			if (!start.isValid() || !structures.wrapAsHolder(e.getKey()).is(StructureTags.VILLAGE)) continue;
			// own RNG: this event may run off the server thread, and level.random must not be shared across threads
			RandomSource rnd = RandomSource.create(level.getSeed() ^ start.getChunkPos().toLong());
			int n = 0;
			for (StructurePiece piece : start.getPieces()) {
				if (!(piece instanceof PoolElementStructurePiece pp) || !pp.getElement().toString().contains("streets")) continue;
				if (n >= MAX_PER_VILLAGE || rnd.nextFloat() > STREET_CHANCE) continue;
				BoundingBox bb = piece.getBoundingBox();
				float yaw = bb.getXSpan() >= bb.getZSpan() ? 90f : 0f;
				synchronized (STREETS) { STREETS.add(new Street(bb.getCenter(), yaw + (rnd.nextBoolean() ? 180f : 0f), 0)); }
				n++;
			}
			if (n > 0) CraftTheft.LOG.info("[world] village at {}: {} street cars queued", start.getChunkPos(), n);
		}
	}

	/** Queued street cars belong to the world that generated them (see GameTick.clearWorldState). */
	public static void clear() { synchronized (STREETS) { STREETS.clear(); } }

	private static void drainStreets(ServerLevel level) {
		int totalWeight = Sheets.VEHICLES.stream().mapToInt(Sheets.Vehicle::spawnWeight).sum();
		if (totalWeight <= 0) return;
		synchronized (STREETS) {
			int size = STREETS.size(), budget = 2;
			for (int i = 0; i < size && budget > 0; i++) {
				Street s = STREETS.poll();
				if (!level.hasChunk(s.pos().getX() >> 4, s.pos().getZ() >> 4)) {
					if (s.age() < 1200) STREETS.add(new Street(s.pos(), s.yaw(), s.age() + 1));
					continue;
				}
				budget--;
				int pick = level.random.nextInt(totalWeight);
				for (Sheets.Vehicle v : Sheets.VEHICLES) {
					if (pick < v.spawnWeight()) {
						if (spawnCar(level, v.id(), s.pos().getX(), s.pos().getZ(), s.yaw()) != null) streetCars++;
						break;
					}
					pick -= v.spawnWeight();
				}
			}
		}
	}
}
