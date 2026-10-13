package dev.crafttheft.weapon;

import java.util.HashMap;
import java.util.Map;
import java.util.UUID;

import net.minecraft.core.particles.ParticleTypes;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.sounds.SoundEvents;
import net.minecraft.sounds.SoundSource;
import net.minecraft.util.Mth;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.projectile.ProjectileUtil;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.EntityHitResult;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.content.ModComponents;
import dev.crafttheft.content.ModEntities;
import dev.crafttheft.content.ModSounds;
import dev.crafttheft.crime.CrimeSystem;
import dev.crafttheft.melty.MeltyChecks;

/**
 * Server-side firing (hooks row weapon_fire: C2S payload crafttheft:trigger -> onTrigger). Each weapon follows its
 * effective stats: fireIntervalTicks between trigger pulls, burst rounds per pull (1 tick apart), pellets per
 * round, spread, range; hitscan rounds are ray casts, projectile/rocket/grenade rounds are entities.
 */
public final class WeaponSystem {
	private static final class State {
		boolean down, pull;
		int cooldown, burstLeft, burstGap;
	}

	private static final Map<UUID, State> STATES = new HashMap<>();
	/** Harness/diagnostics counters; ROUND_TIMES holds the server game time of the last rounds fired. */
	public static int roundsFired, pelletsHit;
	public static final java.util.ArrayDeque<Long> ROUND_TIMES = new java.util.ArrayDeque<>();

	private WeaponSystem() {}

	private static State state(ServerPlayer p) { return STATES.computeIfAbsent(p.getUUID(), k -> new State()); }

	/** Trigger pressed/released on the client while holding a Halo weapon. */
	public static void onTrigger(ServerPlayer p, boolean down) {
		State s = state(p);
		if (down && !s.down) s.pull = true;
		s.down = down;
	}

	public static void onReload(ServerPlayer p) {
		ItemStack st = p.getMainHandItem();
		if (st.getItem() instanceof HaloWeaponItem w && !w.isGrenade()) startReload(p, st, w.stats());
	}

	static void startReload(ServerPlayer p, ItemStack st, WeaponStats s) {
		if (HaloWeaponItem.reloading(st) > 0 || HaloWeaponItem.ammo(st) >= s.clip || HaloWeaponItem.reserve(st) <= 0) return;
		if (s.reloadTicks <= 0) { finishReload(st, s); return; }
		st.set(ModComponents.RELOAD, s.reloadTicks);
		p.serverLevel().playSound(null, p.getX(), p.getY(), p.getZ(), SoundEvents.ARMOR_EQUIP_IRON.value(), SoundSource.PLAYERS, 0.6f, 1.4f);
	}

	private static void finishReload(ItemStack st, WeaponStats s) {
		int ammo = HaloWeaponItem.ammo(st), reserve = HaloWeaponItem.reserve(st);
		int take = Math.min(s.clip - ammo, reserve);
		st.set(ModComponents.AMMO, ammo + take);
		st.set(ModComponents.RESERVE, reserve - take);
		st.set(ModComponents.RELOAD, 0);
	}

	public static void tick(ServerPlayer p) {
		ItemStack st = p.getMainHandItem();
		State s = state(p);
		if (!(st.getItem() instanceof HaloWeaponItem w) || p.isSpectator() || !p.isAlive()) {
			s.down = false; s.pull = false; s.burstLeft = 0;
			return;
		}
		WeaponStats stats = w.stats();
		if (s.cooldown > 0) s.cooldown--;
		if (!w.isGrenade()) {
			int reload = HaloWeaponItem.reloading(st);
			if (reload > 0) {
				if (reload - 1 <= 0) finishReload(st, stats);
				else st.set(ModComponents.RELOAD, reload - 1);
				s.burstLeft = 0;
				return;
			}
		}
		if (s.burstLeft > 0) {
			if (--s.burstGap <= 0) {
				if (!fireRound(p, st, w, stats)) s.burstLeft = 0;
				else { s.burstLeft--; s.burstGap = 1; }
			}
			return;
		}
		boolean wants = stats.auto ? s.down || s.pull : s.pull;
		if (!wants || s.cooldown > 0) return;
		s.pull = false;
		if (!w.isGrenade() && HaloWeaponItem.ammo(st) <= 0) {
			if (HaloWeaponItem.reserve(st) > 0) startReload(p, st, stats);
			else p.serverLevel().playSound(null, p.getX(), p.getY(), p.getZ(), SoundEvents.DISPENSER_FAIL, SoundSource.PLAYERS, 0.5f, 1.8f);
			return;
		}
		s.cooldown = stats.fireIntervalTicks;
		if (fireRound(p, st, w, stats)) {
			s.burstLeft = stats.burst - 1;
			s.burstGap = 1;
		}
	}

	/** Fires one round (all its pellets). Returns false when out of ammo. */
	private static boolean fireRound(ServerPlayer p, ItemStack st, HaloWeaponItem w, WeaponStats s) {
		ServerLevel level = p.serverLevel();
		if (w.isGrenade()) {
			if (st.isEmpty()) return false;
			if (!p.getAbilities().instabuild) st.shrink(1);
		} else {
			int ammo = HaloWeaponItem.ammo(st);
			if (ammo <= 0) return false;
			st.set(ModComponents.AMMO, ammo - 1);
		}
		roundsFired++;
		synchronized (ROUND_TIMES) {
			ROUND_TIMES.addLast(level.getGameTime());
			while (ROUND_TIMES.size() > 64) ROUND_TIMES.removeFirst();
		}
		level.playSound(null, p.getX(), p.getEyeY(), p.getZ(), ModSounds.of(w.row.fireSound()), SoundSource.PLAYERS, 1.0f, 0.95f + level.random.nextFloat() * 0.1f);
		Vec3 eye = p.getEyePosition();
		for (int i = 0; i < s.pellets; i++) {
			Vec3 dir = spread(p, s.spreadDeg);
			switch (s.kind) {
				case "projectile" -> {
					HaloProjectile proj = new HaloProjectile(ModEntities.PROJECTILE, p, level);
					proj.setup(w.weaponId(), s.mcDamage(), s.homing, (int) Math.ceil(s.range / Math.max(0.1f, s.projectileSpeed)));
					proj.setPos(eye.x + dir.x * 0.6, eye.y - 0.1 + dir.y * 0.6, eye.z + dir.z * 0.6);
					proj.shoot(dir.x, dir.y, dir.z, s.projectileSpeed, 0f);
					level.addFreshEntity(proj);
				}
				case "rocket" -> {
					HaloRocket r = new HaloRocket(ModEntities.ROCKET, p, level);
					r.setup(w.weaponId(), s.mcDamage(), s.blastRadius, (int) Math.ceil(s.range / Math.max(0.1f, s.projectileSpeed)));
					r.setPos(eye.x + dir.x * 0.8, eye.y - 0.1 + dir.y * 0.8, eye.z + dir.z * 0.8);
					r.shoot(dir.x, dir.y, dir.z, s.projectileSpeed, 0f);
					level.addFreshEntity(r);
				}
				case "grenade" -> {
					HaloGrenade g = new HaloGrenade(ModEntities.GRENADE, p, level);
					g.setup(w.weaponId(), s.mcDamage(), s.blastRadius, w.weaponId().contains("plasma"));
					g.setPos(eye.x + dir.x * 0.5, eye.y - 0.1 + dir.y * 0.5, eye.z + dir.z * 0.5);
					g.shoot(dir.x, dir.y + 0.1, dir.z, s.projectileSpeed, 0f);
					level.addFreshEntity(g);
				}
				default -> hitscan(p, level, eye, dir, s, i == 0 || i % 4 == 0);
			}
		}
		CrimeSystem.onGunfire(p);
		MeltyChecks.pass(MeltyChecks.WEAPON_FIRED, w.weaponId() + " (" + s.kind + ")");
		return true;
	}

	private static Vec3 spread(ServerPlayer p, float deg) {
		var r = p.getRandom();
		float yaw = p.getYRot() + (float) r.triangle(0, deg), pitch = p.getXRot() + (float) r.triangle(0, deg);
		return Vec3.directionFromRotation(pitch, yaw);
	}

	private static void hitscan(ServerPlayer p, ServerLevel level, Vec3 eye, Vec3 dir, WeaponStats s, boolean tracer) {
		Vec3 end = loadedPart(level, eye, eye.add(dir.scale(s.range)));
		BlockHitResult bh = level.clip(new ClipContext(eye, end, ClipContext.Block.COLLIDER, ClipContext.Fluid.NONE, p));
		if (bh.getType() != HitResult.Type.MISS) end = bh.getLocation();
		AABB sweep = p.getBoundingBox().expandTowards(end.subtract(eye)).inflate(1.0);
		EntityHitResult eh = ProjectileUtil.getEntityHitResult(level, p, eye, end, sweep,
				e -> !e.isSpectator() && e.isPickable() && e != p.getVehicle() && !p.hasPassenger(e), 0.15f);
		if (eh != null) {
			Entity target = eh.getEntity();
			end = eh.getLocation();
			target.invulnerableTime = 0; // otherwise automatic fire and pellets are eaten by the hurt cooldown
			if (target.hurt(p.damageSources().playerAttack(p), s.mcDamage())) {
				pelletsHit++;
				CrimeSystem.onRangedHit(p, target); // shooting a villager/golem is assault, like the melee hook
			}
			if (CraftTheft.LOG.isDebugEnabled()) CraftTheft.LOG.debug("[weapon] {} hit {} for {}", s.id, target.getName().getString(), s.mcDamage());
		} else if (bh.getType() == HitResult.Type.BLOCK) {
			level.sendParticles(ParticleTypes.SMOKE, end.x, end.y, end.z, 2, 0.02, 0.02, 0.02, 0.01);
		}
		if (tracer) {
			double len = end.distanceTo(eye);
			for (double t = 1.5; t < len; t += 1.5) {
				Vec3 q = eye.add(dir.scale(t));
				level.sendParticles(ParticleTypes.CRIT, q.x, q.y, q.z, 1, 0, 0, 0, 0);
			}
		}
	}

	public static float clampDeg(float d) { return Mth.clamp(d, -90f, 90f); }

	/**
	 * The part of a ray that stays in loaded chunks. Level.clip on a ServerLevel fetches every chunk it crosses with
	 * load=true, so a long hitscan ray (sniper: range 192 blocks, beyond a 10-chunk view distance) aimed at the horizon
	 * would load or even generate chunks synchronously on the server thread. The ray is sampled every 8 blocks and cut
	 * at the last loaded sample; the loaded area around a player is convex, so the cut ray never leaves it.
	 */
	public static Vec3 loadedPart(ServerLevel level, Vec3 from, Vec3 to) {
		Vec3 d = to.subtract(from);
		double len = d.length();
		if (len < 1e-6) return to;
		Vec3 u = d.scale(1.0 / len);
		final double step = 8.0;
		for (double t = step; ; t += step) {
			double tt = Math.min(t, len);
			if (!level.hasChunkAt(net.minecraft.core.BlockPos.containing(from.add(u.scale(tt))))) return from.add(u.scale(Math.max(0.0, tt - step)));
			if (tt >= len) return to;
		}
	}

	public static void forget(ServerPlayer p) { STATES.remove(p.getUUID()); }

	public static void clear() { STATES.clear(); }
}
