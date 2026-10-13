package dev.crafttheft.weapon;

import org.jetbrains.annotations.Nullable;

import net.minecraft.core.particles.ParticleTypes;
import net.minecraft.nbt.CompoundTag;
import net.minecraft.network.syncher.EntityDataAccessor;
import net.minecraft.network.syncher.EntityDataSerializers;
import net.minecraft.network.syncher.SynchedEntityData;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.sounds.SoundEvents;
import net.minecraft.sounds.SoundSource;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.projectile.ThrowableProjectile;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.EntityHitResult;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;

/**
 * Frag grenades bounce and blow up after their fuse; plasma grenades stick to the first thing they touch (an entity
 * carries it along) and blow up a moment later, as in Halo 3.
 */
public class HaloGrenade extends ThrowableProjectile {
	private static final EntityDataAccessor<String> DATA_WEAPON = SynchedEntityData.defineId(HaloGrenade.class, EntityDataSerializers.STRING);
	public static final int FRAG_FUSE = 50, PLASMA_FUSE_AFTER_STICK = 30, PLASMA_MAX = 80;
	public float damage = 10f, radius = 3f;
	public boolean plasma;
	private int age, stuckAt = -1;
	@Nullable private Entity stuckTo;
	@Nullable private Vec3 stuckOffset;

	public HaloGrenade(EntityType<? extends HaloGrenade> type, Level level) { super(type, level); }

	public HaloGrenade(EntityType<? extends HaloGrenade> type, LivingEntity owner, Level level) { super(type, owner, level); }

	public void setup(String weaponId, float damage, float radius, boolean plasma) {
		entityData.set(DATA_WEAPON, weaponId);
		this.damage = damage; this.radius = radius; this.plasma = plasma;
	}

	public String weaponId() { return entityData.get(DATA_WEAPON); }

	@Override protected void defineSynchedData(SynchedEntityData.Builder b) { b.define(DATA_WEAPON, ""); }
	@Override protected double getDefaultGravity() { return 0.05; }

	@Override
	public void tick() {
		if (stuckAt >= 0) {
			// stuck: follow the entity (or stay on the block), no physics
			if (stuckTo != null && stuckTo.isAlive() && stuckOffset != null) setPos(stuckTo.position().add(stuckOffset));
			setDeltaMovement(Vec3.ZERO);
			baseTick();
		} else {
			super.tick();
		}
		if (!(level() instanceof ServerLevel sl)) return;
		age++;
		if (plasma) sl.sendParticles(ParticleTypes.ELECTRIC_SPARK, getX(), getY() + 0.1, getZ(), 1, 0.05, 0.05, 0.05, 0);
		boolean boom = plasma ? (stuckAt >= 0 && age - stuckAt >= PLASMA_FUSE_AFTER_STICK) || age >= PLASMA_MAX : age >= FRAG_FUSE;
		if (boom) {
			Explosions.blast(sl, position().add(0, 0.2, 0), this, getOwner(), damage, radius);
			discard();
		}
	}

	@Override
	protected void onHitEntity(EntityHitResult hit) {
		if (level().isClientSide) return;
		if (plasma && stuckAt < 0) {
			stuckTo = hit.getEntity();
			stuckOffset = position().subtract(stuckTo.position());
			stuckAt = age;
			level().playSound(null, getX(), getY(), getZ(), SoundEvents.SLIME_ATTACK, SoundSource.PLAYERS, 1f, 1.5f);
		} else {
			setDeltaMovement(getDeltaMovement().scale(-0.2));
		}
	}

	@Override
	protected void onHitBlock(BlockHitResult hit) {
		super.onHitBlock(hit);
		if (plasma) {
			if (stuckAt < 0) { stuckAt = age; setPos(hit.getLocation()); }
			return;
		}
		// frag: bounce with damping
		Vec3 v = getDeltaMovement();
		Vec3 n = Vec3.atLowerCornerOf(hit.getDirection().getNormal());
		Vec3 r = v.subtract(n.scale(2 * v.dot(n))).scale(0.4);
		setDeltaMovement(r);
		setPos(hit.getLocation().add(n.scale(0.05)));
		if (v.length() > 0.2) level().playSound(null, getX(), getY(), getZ(), SoundEvents.STONE_HIT, SoundSource.PLAYERS, 0.6f, 1.6f);
	}

	@Override
	protected void onHit(HitResult hit) {
		if (hit.getType() == HitResult.Type.ENTITY) onHitEntity((EntityHitResult) hit);
		else if (hit.getType() == HitResult.Type.BLOCK) onHitBlock((BlockHitResult) hit);
	}

	@Override protected void addAdditionalSaveData(CompoundTag t) { super.addAdditionalSaveData(t); t.putFloat("Damage", damage); t.putFloat("Radius", radius); t.putBoolean("Plasma", plasma); t.putString("Weapon", weaponId()); }
	@Override protected void readAdditionalSaveData(CompoundTag t) { super.readAdditionalSaveData(t); damage = t.getFloat("Damage"); radius = t.getFloat("Radius"); plasma = t.getBoolean("Plasma"); entityData.set(DATA_WEAPON, t.getString("Weapon")); }
}
