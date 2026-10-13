package dev.crafttheft.weapon;

import org.jetbrains.annotations.Nullable;

import net.minecraft.core.particles.ParticleTypes;
import net.minecraft.core.registries.Registries;
import net.minecraft.resources.ResourceKey;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.sounds.SoundEvents;
import net.minecraft.sounds.SoundSource;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.damagesource.DamageType;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.crime.CrimeSystem;
import dev.crafttheft.vehicle.CarEntity;

/** Halo blast damage: linear falloff to the blast radius, needs line of sight, never breaks blocks. */
public final class Explosions {
	public static final ResourceKey<DamageType> HALO_EXPLOSION = ResourceKey.create(Registries.DAMAGE_TYPE, CraftTheft.id("halo_explosion"));
	public static int blasts;

	private Explosions() {}

	public static void blast(ServerLevel level, Vec3 at, @Nullable Entity direct, @Nullable Entity owner, float damage, float radius) {
		blasts++;
		DamageSource src = new DamageSource(level.registryAccess().registryOrThrow(Registries.DAMAGE_TYPE).getHolderOrThrow(HALO_EXPLOSION), direct, owner);
		// living things and GTA cars (CarEntity is not a LivingEntity: without it rockets and grenades could not damage a
		// car, so vehicles.health and the assault_police crime were unreachable with explosives)
		for (Entity e : level.getEntities((Entity) null, new net.minecraft.world.phys.AABB(at, at).inflate(radius),
				x -> x instanceof LivingEntity || x instanceof CarEntity)) {
			Vec3 c = e.getBoundingBox().getCenter();
			double d = c.distanceTo(at);
			if (d > radius || !e.isAlive()) continue;
			if (level.clip(new ClipContext(at, c, ClipContext.Block.COLLIDER, ClipContext.Fluid.NONE, e)).getType() == HitResult.Type.BLOCK
					&& level.clip(new ClipContext(at, e.getEyePosition(), ClipContext.Block.COLLIDER, ClipContext.Fluid.NONE, e)).getType() == HitResult.Type.BLOCK) continue;
			float k = (float) (1.0 - d / radius);
			e.invulnerableTime = 0;
			if (e.hurt(src, damage * Math.max(0.15f, k))) CrimeSystem.onRangedHit(owner, e);
			if (e instanceof LivingEntity) {
				Vec3 push = c.subtract(at).normalize().scale(0.6 * k).add(0, 0.3 * k, 0);
				e.push(push.x, push.y, push.z);
				e.hurtMarked = true;
			}
		}
		level.sendParticles(radius >= 3 ? ParticleTypes.EXPLOSION_EMITTER : ParticleTypes.EXPLOSION, at.x, at.y, at.z, 1, 0, 0, 0, 0);
		level.playSound(null, at.x, at.y, at.z, SoundEvents.GENERIC_EXPLODE.value(), SoundSource.PLAYERS, 2.0f, 1.0f);
	}
}
