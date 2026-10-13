package dev.crafttheft.weapon;

import java.util.Comparator;

import org.jetbrains.annotations.Nullable;

import net.minecraft.core.particles.ParticleTypes;
import net.minecraft.nbt.CompoundTag;
import net.minecraft.network.syncher.EntityDataAccessor;
import net.minecraft.network.syncher.EntityDataSerializers;
import net.minecraft.network.syncher.SynchedEntityData;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.projectile.ThrowableProjectile;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.EntityHitResult;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;

/** Plasma bolts and needles: straight, no gravity; the needler's rounds home on the nearest living target ahead. */
public class HaloProjectile extends ThrowableProjectile {
	private static final EntityDataAccessor<String> DATA_WEAPON = SynchedEntityData.defineId(HaloProjectile.class, EntityDataSerializers.STRING);
	public static final double HOMING_RANGE = 20, HOMING_CONE = 0.55, HOMING_TURN = 0.3;
	public float damage = 1f;
	public boolean homing;
	private int maxAge = 60, age;
	@Nullable private Entity homingTarget;

	public HaloProjectile(EntityType<? extends HaloProjectile> type, Level level) {
		super(type, level);
	}

	public HaloProjectile(EntityType<? extends HaloProjectile> type, LivingEntity owner, Level level) {
		super(type, owner, level);
	}

	public void setup(String weaponId, float damage, boolean homing, int maxAge) {
		entityData.set(DATA_WEAPON, weaponId);
		this.damage = damage;
		this.homing = homing;
		this.maxAge = Math.max(5, maxAge);
	}

	public String weaponId() { return entityData.get(DATA_WEAPON); }

	@Override protected void defineSynchedData(SynchedEntityData.Builder b) { b.define(DATA_WEAPON, ""); }
	@Override protected double getDefaultGravity() { return 0.0; }

	@Override
	public void tick() {
		// ThrowableProjectile.tick scales the velocity by 0.99 every tick (0.8 in water). Halo rounds fly at the weapons
		// sheet's projectileSpeed (blocks/tick) for range/projectileSpeed ticks; with the drag a rocket only reached
		// about 77% of its range. Keep the speed in air; water still slows them.
		Vec3 before = getDeltaMovement();
		super.tick();
		if (!isRemoved() && !isInWater()) setDeltaMovement(getDeltaMovement().normalize().scale(before.length()));
		if (!(level() instanceof ServerLevel sl)) return;
		sl.sendParticles(homing ? ParticleTypes.WITCH : ParticleTypes.ELECTRIC_SPARK, getX(), getY(), getZ(), 1, 0, 0, 0, 0);
		if (++age > maxAge) { discard(); return; }
		if (homing) steer();
	}

	/** Turns the velocity a bounded amount towards the nearest living target in a cone ahead. */
	private void steer() {
		Vec3 v = getDeltaMovement();
		double speed = v.length();
		if (speed < 1e-4) return;
		Vec3 dir = v.scale(1 / speed);
		if (homingTarget == null || !homingTarget.isAlive() || homingTarget.distanceToSqr(this) > HOMING_RANGE * HOMING_RANGE * 1.5) {
			Entity owner = getOwner();
			homingTarget = level().getEntitiesOfClass(LivingEntity.class, getBoundingBox().inflate(HOMING_RANGE),
							e -> e.isAlive() && e != owner && !e.isSpectator() && (owner == null || !owner.isPassengerOfSameVehicle(e))
									&& e.getEyePosition().subtract(position()).normalize().dot(dir) > HOMING_CONE)
					.stream().min(Comparator.comparingDouble(e -> e.distanceToSqr(this))).orElse(null);
		}
		if (homingTarget == null) return;
		Vec3 want = homingTarget.getBoundingBox().getCenter().subtract(position()).normalize();
		Vec3 nd = dir.add(want.subtract(dir).scale(HOMING_TURN)).normalize();
		setDeltaMovement(nd.scale(speed));
		hasImpulse = true;
	}

	@Nullable public Entity homingTarget() { return homingTarget; }

	@Override
	protected void onHitEntity(EntityHitResult hit) {
		super.onHitEntity(hit);
		Entity e = hit.getEntity();
		e.invulnerableTime = 0;
		if (e.hurt(damageSources().thrown(this, getOwner()), damage)) dev.crafttheft.crime.CrimeSystem.onRangedHit(getOwner(), e);
	}

	@Override
	protected void onHit(HitResult hit) {
		super.onHit(hit);
		if (!level().isClientSide) discard();
	}

	@Override
	protected void addAdditionalSaveData(CompoundTag tag) {
		super.addAdditionalSaveData(tag);
		tag.putFloat("Damage", damage);
		tag.putBoolean("Homing", homing);
		tag.putString("Weapon", weaponId());
	}

	@Override
	protected void readAdditionalSaveData(CompoundTag tag) {
		super.readAdditionalSaveData(tag);
		damage = tag.getFloat("Damage");
		homing = tag.getBoolean("Homing");
		entityData.set(DATA_WEAPON, tag.getString("Weapon"));
	}
}
