package dev.crafttheft.crime;

import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import net.minecraft.core.BlockPos;
import net.minecraft.core.particles.ParticleTypes;
import net.minecraft.core.registries.Registries;
import net.minecraft.resources.ResourceKey;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.sounds.SoundEvents;
import net.minecraft.sounds.SoundSource;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.damagesource.DamageType;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.level.levelgen.Heightmap;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.content.ModEntities;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.melty.MeltyChecks;
import dev.crafttheft.vehicle.CarEntity;

/**
 * Wanted sheet -> police response: every spawnEveryTicks keep up to maxPoliceCars of the row's policeVehicle within
 * range, spawned spawnDistance away; officers shoot from 3 stars when the row says so. Police leave when the
 * stars are gone. Cars stuck far away are recycled so the chase keeps going on rough terrain.
 */
public final class PoliceDispatch {
	public static boolean ENABLED = true;
	public static final double KEEP_RANGE = 160, CHASE_CLOSE = 12, SHOOT_RANGE = 24;
	public static final ResourceKey<DamageType> POLICE_GUNFIRE = ResourceKey.create(Registries.DAMAGE_TYPE, CraftTheft.id("police_gunfire"));
	private static final Map<UUID, Long> LAST_SPAWN = new HashMap<>();
	public static int dispatched, shots;

	private PoliceDispatch() {}

	public static List<CarEntity> policeNear(ServerPlayer p, double range) {
		return p.serverLevel().getEntitiesOfClass(CarEntity.class, new AABB(p.blockPosition()).inflate(range), CarEntity::isPolice);
	}

	public static void tick(ServerPlayer p) {
		ServerLevel level = p.serverLevel();
		long now = level.getGameTime();
		int stars = WantedSystem.stars(p);
		if (stars <= 0) {
			LAST_SPAWN.remove(p.getUUID()); // the next wanted episode gets its first car quickly again
			// tick-time: the 160-block entity query runs once a second here, not every tick for every calm player
			if (now % 20 == 0)
				for (CarEntity c : policeNear(p, KEEP_RANGE))
					if (c.isDispatched() && c.getPassengers().isEmpty() && c.distanceTo(p) > 24) c.discard();
			return;
		}
		List<CarEntity> police = policeNear(p, KEEP_RANGE);
		Sheets.Wanted row = WantedSystem.row(stars);
		if (row == null) return;
		for (CarEntity c : police) {
			if (!c.isDispatched()) continue;
			if (c.distanceTo(p) < CHASE_CLOSE)
				MeltyChecks.pass(MeltyChecks.POLICE_CHASE, String.format("police car %d closed to %.1f blocks at %d stars", c.getId(), c.distanceTo(p), stars));
			if (row.officersShoot() && now % 20 == (c.getId() % 20)) shoot(level, c, p);
			if (c.stuckTicks() > 100 && c.distanceTo(p) > 30) {
				CraftTheft.LOG.info("[police] car {} stuck for {} ticks {} blocks away: recycled", c.getId(), c.stuckTicks(), (int) c.distanceTo(p));
				c.discard();
			}
		}
		if (!ENABLED) return;
		long last = LAST_SPAWN.computeIfAbsent(p.getUUID(), k -> now - row.spawnEveryTicks() + 40); // first car ~2 s after the crime
		long dispatchedHere = police.stream().filter(CarEntity::isDispatched).count();
		if (dispatchedHere >= row.maxPoliceCars() || now - last < row.spawnEveryTicks()) return;
		if (spawn(level, p, row, stars)) LAST_SPAWN.put(p.getUUID(), now);
	}

	private static boolean spawn(ServerLevel level, ServerPlayer p, Sheets.Wanted row, int stars) {
		for (int t = 0; t < 16; t++) {
			double a = level.random.nextDouble() * Math.PI * 2, r = row.spawnDistance() * (0.85 + level.random.nextDouble() * 0.3);
			int x = p.getBlockX() + (int) Math.round(Math.cos(a) * r), z = p.getBlockZ() + (int) Math.round(Math.sin(a) * r);
			if (!level.hasChunk(x >> 4, z >> 4)) continue;
			int y = level.getHeight(Heightmap.Types.MOTION_BLOCKING_NO_LEAVES, x, z);
			BlockPos below = new BlockPos(x, y - 1, z);
			if (!level.getFluidState(below).isEmpty() || !level.getBlockState(below).isSolid()) continue;
			CarEntity car = ModEntities.car(row.policeVehicle()).create(level);
			if (car == null) return false;
			float yaw = (float) Math.toDegrees(Math.atan2(-(p.getX() - x), p.getZ() - z)); // face the player
			car.moveTo(x + 0.5, y, z + 0.5, yaw, 0);
			if (!level.noCollision(car)) continue;
			car.setDispatched(p.getUUID());
			level.addFreshEntity(car);
			dispatched++;
			CraftTheft.LOG.info("[police] dispatched {} {} at {} for {} ({} stars, {} blocks away)", row.policeVehicle(), car.getId(), car.blockPosition(),
					p.getName().getString(), stars, (int) car.distanceTo(p));
			return true;
		}
		return false;
	}

	/** An officer fires from the car's window: a hitscan round with spread, needs line of sight. */
	private static void shoot(ServerLevel level, CarEntity c, ServerPlayer p) {
		if (c.distanceTo(p) > SHOOT_RANGE || p.isCreative() || p.isSpectator()) return;
		Vec3 from = c.position().add(0, c.getBbHeight() * 0.8, 0), to = p.getEyePosition().subtract(0, 0.4, 0);
		if (level.clip(new ClipContext(from, to, ClipContext.Block.COLLIDER, ClipContext.Fluid.NONE, c)).getType() == HitResult.Type.BLOCK) return;
		shots++;
		level.playSound(null, from.x, from.y, from.z, SoundEvents.FIREWORK_ROCKET_BLAST, SoundSource.HOSTILE, 1.5f, 1.6f);
		Vec3 d = to.subtract(from);
		for (double t = 1.5; t < d.length(); t += 1.5) {
			Vec3 q = from.add(d.normalize().scale(t));
			level.sendParticles(ParticleTypes.CRIT, q.x, q.y, q.z, 1, 0, 0, 0, 0);
		}
		if (level.random.nextFloat() < 0.6f) {
			DamageSource src = new DamageSource(level.registryAccess().registryOrThrow(Registries.DAMAGE_TYPE).getHolderOrThrow(POLICE_GUNFIRE), c, c);
			p.hurt(src, 2.0f);
		}
	}

	public static void forget(ServerPlayer p) { LAST_SPAWN.remove(p.getUUID()); }

	public static void clear() { LAST_SPAWN.clear(); }
}
