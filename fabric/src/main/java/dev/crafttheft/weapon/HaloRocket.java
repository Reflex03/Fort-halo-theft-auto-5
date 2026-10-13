package dev.crafttheft.weapon;

import net.minecraft.core.particles.ParticleTypes;
import net.minecraft.nbt.CompoundTag;
import net.minecraft.network.syncher.SynchedEntityData;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.projectile.ThrowableProjectile;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;

/** Rocket launcher round: straight flight, blast on impact or at the end of its range. */
public class HaloRocket extends ThrowableProjectile {
	public float damage = 10f, radius = 4f;
	private int maxAge = 60, age;

	public HaloRocket(EntityType<? extends HaloRocket> type, Level level) { super(type, level); }

	public HaloRocket(EntityType<? extends HaloRocket> type, LivingEntity owner, Level level) { super(type, owner, level); }

	public void setup(String weaponId, float damage, float radius, int maxAge) {
		this.damage = damage; this.radius = radius; this.maxAge = Math.max(5, maxAge);
	}

	@Override protected void defineSynchedData(SynchedEntityData.Builder b) {}
	@Override protected double getDefaultGravity() { return 0.0; }

	@Override
	public void tick() {
		// ThrowableProjectile.tick scales the velocity by 0.99 every tick (0.8 in water). Halo rounds fly at the weapons
		// sheet's projectileSpeed (blocks/tick) for range/projectileSpeed ticks; with the drag a rocket only reached
		// about 77% of its range. Keep the speed in air; water still slows them.
		Vec3 before = getDeltaMovement();
		super.tick();
		if (!isRemoved() && !isInWater()) setDeltaMovement(getDeltaMovement().normalize().scale(before.length()));
		if (level() instanceof ServerLevel sl) {
			sl.sendParticles(ParticleTypes.SMOKE, getX(), getY(), getZ(), 2, 0.05, 0.05, 0.05, 0.0);
			sl.sendParticles(ParticleTypes.FLAME, getX(), getY(), getZ(), 1, 0, 0, 0, 0);
			if (++age > maxAge) explode();
		}
	}

	@Override
	protected void onHit(HitResult hit) {
		super.onHit(hit);
		if (!level().isClientSide) explode();
	}

	private void explode() {
		if (isRemoved()) return;
		Explosions.blast((ServerLevel) level(), position(), this, getOwner(), damage, radius);
		discard();
	}

	@Override protected void addAdditionalSaveData(CompoundTag t) { super.addAdditionalSaveData(t); t.putFloat("Damage", damage); t.putFloat("Radius", radius); }
	@Override protected void readAdditionalSaveData(CompoundTag t) { super.readAdditionalSaveData(t); damage = t.getFloat("Damage"); radius = t.getFloat("Radius"); }
}
