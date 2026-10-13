package dev.crafttheft.vehicle;

import java.util.List;
import java.util.UUID;

import org.jetbrains.annotations.Nullable;

import net.minecraft.core.registries.Registries;
import net.minecraft.nbt.CompoundTag;
import net.minecraft.network.syncher.EntityDataAccessor;
import net.minecraft.network.syncher.EntityDataSerializers;
import net.minecraft.network.syncher.SynchedEntityData;
import net.minecraft.resources.ResourceKey;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.sounds.SoundSource;
import net.minecraft.util.Mth;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.damagesource.DamageType;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EntityDimensions;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.MoverType;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.Vec3;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.content.ModEntities;
import dev.crafttheft.content.ModSounds;
import dev.crafttheft.content.SourceRows;
import dev.crafttheft.crime.CrimeSystem;
import dev.crafttheft.crime.WantedSystem;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.melty.MeltyChecks;

/**
 * A GTA V car (one EntityType per vehicles row). Control model is the vanilla boat's: the driver's client simulates
 * the car (isControlledByLocalInstance) and vanilla sends ServerboundMoveVehiclePacket; driver input is Player.zza/xxa.
 * Police cars without a driver run the same physics on the server with chase AI. Handling comes from the sheet.
 */
public class CarEntity extends Entity {
	private static final EntityDataAccessor<Float> DATA_HEALTH = SynchedEntityData.defineId(CarEntity.class, EntityDataSerializers.FLOAT);
	private static final EntityDataAccessor<Boolean> DATA_SIREN = SynchedEntityData.defineId(CarEntity.class, EntityDataSerializers.BOOLEAN);
	public static final ResourceKey<DamageType> RAN_OVER = ResourceKey.create(Registries.DAMAGE_TYPE, CraftTheft.id("ran_over"));
	public static final float MAX_REVERSE = 0.35f, BRAKE = 0.09f;
	public static final int PARKED_PHYSICS_INTERVAL = 10;
	public static int rams;

	public final Sheets.Vehicle spec;
	public float speed;
	private float yawDelta, wheelSpin, wheelSpinO, steerVisual;
	@Nullable private UUID owner;
	@Nullable private UUID dispatchedFor;
	private int lerpSteps;
	private double lerpX, lerpY, lerpZ;
	private float lerpYRot;
	@Nullable private Vec3 lastServerPos;
	public double serverSpeed, drivenDistance;
	private int stuckTicks, reverseTicks, noProgressTicks;
	private double bestDist = Double.MAX_VALUE;

	public CarEntity(EntityType<? extends CarEntity> type, Level level) {
		super(type, level);
		Sheets.Vehicle v = ModEntities.spec(type);
		this.spec = v != null ? v : Sheets.VEHICLES.get(0);
		this.blocksBuilding = true;
		entityData.set(DATA_HEALTH, spec.health());
	}

	public boolean isPolice() { return spec.role().equals("police"); }
	public String meshKey() { return SourceRows.meshKey(spec.model()); }
	public float health() { return entityData.get(DATA_HEALTH); }
	public boolean sirenOn() { return entityData.get(DATA_SIREN); }
	public boolean isDispatched() { return dispatchedFor != null; }
	public void setDispatched(UUID target) { dispatchedFor = target; }
	public int stuckTicks() { return noProgressTicks; }
	@Nullable public UUID owner() { return owner; }
	public void setOwner(@Nullable UUID u) { owner = u; }

	@Override
	protected void defineSynchedData(SynchedEntityData.Builder b) {
		b.define(DATA_HEALTH, 60f);
		b.define(DATA_SIREN, false);
	}

	// ---- vanilla vehicle plumbing ----
	@Override public float maxUpStep() { return 1.05f; } // same on both sides, or the server reports "moved wrongly"
	@Override public boolean canBeCollidedWith() { return true; }
	@Override public boolean isPickable() { return !isRemoved(); }
	@Override public boolean isPushable() { return false; }
	/** Solid only against solid entities; mobs are rammed (server check) instead of stopping the car dead. */
	@Override public boolean canCollideWith(Entity other) { return other.canBeCollidedWith() && !isPassengerOfSameVehicle(other); }
	@Override protected boolean canAddPassenger(Entity e) { return getPassengers().size() < 2; }

	@Nullable
	@Override
	public LivingEntity getControllingPassenger() {
		return getFirstPassenger() instanceof Player p ? p : null;
	}

	@Override
	protected Vec3 getPassengerAttachmentPoint(Entity passenger, EntityDimensions dims, float scale) {
		int i = getPassengers().indexOf(passenger);
		double side = i == 0 ? spec.width() * 0.22 : -spec.width() * 0.22; // driver on the left (+X when facing +Z)
		return new Vec3(side, -0.2, -0.2).yRot(-getYRot() * Mth.DEG_TO_RAD); // low seat: the driver sits inside the body
	}

	@Override
	protected void positionRider(Entity passenger, MoveFunction f) {
		super.positionRider(passenger, f);
		passenger.setYRot(passenger.getYRot() + yawDelta);
		passenger.setYHeadRot(passenger.getYHeadRot() + yawDelta);
	}

	/** hooks row car_enter: entering a car you don't own is stealing it. */
	@Override
	public InteractionResult interact(Player player, InteractionHand hand) {
		if (player.isSecondaryUseActive()) return InteractionResult.PASS;
		if (!level().isClientSide) {
			if (getPassengers().isEmpty() && player instanceof ServerPlayer sp && (owner == null || !owner.equals(player.getUUID()))) {
				CrimeSystem.onCarEnter(sp, this);
				owner = player.getUUID();
				dispatchedFor = null; // a stolen police car stops chasing
				entityData.set(DATA_SIREN, false);
			}
			return player.startRiding(this) ? InteractionResult.CONSUME : InteractionResult.PASS;
		}
		return InteractionResult.SUCCESS;
	}

	/** hooks row car_damaged: hurting a police car is attacking the police. */
	@Override
	public boolean hurt(DamageSource source, float amount) {
		if (level().isClientSide || isRemoved() || isInvulnerableTo(source)) return false;
		float h = health() - amount;
		entityData.set(DATA_HEALTH, h);
		if (isPolice() && source.getEntity() instanceof ServerPlayer sp && !hasPassenger(sp) && (owner == null || !owner.equals(sp.getUUID())))
			CrimeSystem.onPoliceAttacked(sp, this);
		if (h <= 0) {
			level().explode(this, getX(), getY() + 0.5, getZ(), 2.0f, Level.ExplosionInteraction.NONE);
			ejectPassengers();
			discard();
		}
		return true;
	}

	public void honk() {
		if (level().isClientSide) return;
		level().playSound(null, getX(), getY(), getZ(), ModSounds.of(spec.hornSound()), SoundSource.NEUTRAL, 1.5f, 1f);
	}

	// ---- interpolation (as Boat; Entity.lerpTo is an instant teleport in 1.21.1) ----
	@Override
	public void lerpTo(double x, double y, double z, float yRot, float xRot, int steps) {
		lerpX = x; lerpY = y; lerpZ = z; lerpYRot = yRot; lerpSteps = 3;
	}
	@Override public double lerpTargetX() { return lerpSteps > 0 ? lerpX : getX(); }
	@Override public double lerpTargetY() { return lerpSteps > 0 ? lerpY : getY(); }
	@Override public double lerpTargetZ() { return lerpSteps > 0 ? lerpZ : getZ(); }
	@Override public float lerpTargetYRot() { return lerpSteps > 0 ? lerpYRot : getYRot(); }

	private void tickLerp() {
		if (isControlledByLocalInstance()) {
			lerpSteps = 0;
			syncPacketPositionCodec(getX(), getY(), getZ());
		}
		if (lerpSteps > 0) {
			lerpPositionAndRotationStep(lerpSteps, lerpX, lerpY, lerpZ, lerpYRot, getXRot());
			lerpSteps--;
		}
	}

	@Override
	public void tick() {
		super.tick();
		wheelSpinO = wheelSpin;
		tickLerp();
		yawDelta = 0;
		if (isParkedOnServer() && tickCount % PARKED_PHYSICS_INTERVAL != 0) {
			// tick-time: a parked car (no driver, not chasing, standing still on the ground) only re-checks its
			// footing every PARKED_PHYSICS_INTERVAL ticks instead of running a full move() every tick
			setDeltaMovement(Vec3.ZERO);
		} else if (isControlledByLocalInstance()) {
			float throttle = 0, steer = 0;
			if (getControllingPassenger() instanceof Player p) {
				throttle = p.zza;
				steer = p.xxa;
			} else if (!level().isClientSide && dispatchedFor != null) {
				float[] ai = policeAi();
				throttle = ai[0]; steer = ai[1];
			}
			physics(throttle, steer);
		} else {
			setDeltaMovement(Vec3.ZERO);
			if (level().isClientSide) speed = (float) (getX() - xo == 0 && getZ() - zo == 0 ? speed * 0.8 : Math.hypot(getX() - xo, getZ() - zo));
		}
		wheelSpin += speed / 0.35f;
		if (!level().isClientSide) serverTick((ServerLevel) level());
	}

	/** Server side, nobody driving, not a dispatched police car, not moving, on the ground. */
	private boolean isParkedOnServer() {
		return !level().isClientSide && dispatchedFor == null && getControllingPassenger() == null && speed == 0f && onGround()
				&& serverSpeed < 1e-3;
	}

	private void physics(float throttle, float steer) {
		boolean ground = onGround();
		float max = spec.maxSpeed(), accel = spec.accel();
		if (ground) {
			if (throttle > 0.01f) speed += accel * throttle * (speed < 0 ? 2.5f : 1f);
			else if (throttle < -0.01f) speed += (speed > 0 ? BRAKE : accel * 0.6f) * throttle;
			else speed *= 0.97f;
		} else {
			speed *= 0.995f;
		}
		speed = Mth.clamp(speed, -MAX_REVERSE, max);
		if (Math.abs(speed) < 0.003f && Math.abs(throttle) < 0.01f) speed = 0;
		float turn = Mth.clamp(Math.abs(speed) / 0.15f, 0f, 1f) * (1f - 0.4f * Mth.clamp(Math.abs(speed) / max, 0f, 1f));
		float dYaw = ground ? -steer * spec.turnDeg() * turn * Math.signum(speed) : 0f;
		setYRot(getYRot() + dYaw);
		yawDelta = dYaw;
		steerVisual = steer * 25f;
		float rad = getYRot() * Mth.DEG_TO_RAD;
		Vec3 old = getDeltaMovement();
		double vy = ground ? -0.08 : (old.y - 0.08) * 0.98;
		double vx = ground ? -Mth.sin(rad) * speed : old.x * 0.99;
		double vz = ground ? Mth.cos(rad) * speed : old.z * 0.99;
		setDeltaMovement(vx, vy, vz);
		move(MoverType.SELF, getDeltaMovement());
		if (horizontalCollision) speed *= 0.5f;
	}

	// ---- server: ram damage, siren state, drive distance ----
	private void serverTick(ServerLevel level) {
		Vec3 pos = position();
		double moved = lastServerPos == null ? 0 : pos.subtract(lastServerPos).horizontalDistance();
		// the server sees client moves in bursts when it lags: smooth and clamp before using it for damage
		serverSpeed = Math.min(spec.maxSpeed() * 1.2, serverSpeed * 0.5 + moved * 0.5);
		lastServerPos = pos;
		LivingEntity driver = getControllingPassenger();
		if (driver instanceof ServerPlayer sp) {
			drivenDistance += Math.min(moved, 3.0);
			if (drivenDistance >= 20) MeltyChecks.pass(MeltyChecks.CAR_DRIVEN, String.format("%s driven %.0f blocks by %s", spec.id(), drivenDistance, sp.getName().getString()));
		}
		if (serverSpeed > 0.15) {
			DamageSource src = new DamageSource(level.registryAccess().registryOrThrow(Registries.DAMAGE_TYPE).getHolderOrThrow(RAN_OVER), this, driver);
			List<Entity> hits = level.getEntities(this, getBoundingBox().inflate(0.3, 0.0, 0.3),
					e -> e instanceof LivingEntity && e.isAlive() && !hasPassenger(e) && e.getVehicle() != this);
			for (Entity e : hits) {
				float dmg = (float) (serverSpeed * 14.0);
				Vec3 push = pos.vectorTo(e.position()).multiply(1, 0, 1).normalize().scale(serverSpeed * 1.6).add(0, 0.35, 0);
				if (e.hurt(src, dmg)) {
					e.push(push.x, push.y, push.z);
					e.hurtMarked = true;
					rams++;
					CraftTheft.LOG.info("[car] {} rammed {} at {} b/t for {}", spec.id(), e.getName().getString(), String.format("%.2f", serverSpeed), dmg);
				}
			}
			for (Entity e : level.getEntities(this, getBoundingBox().inflate(0.4), e -> e instanceof CarEntity)) {
				e.hurt(src, (float) (serverSpeed * 6.0));
				for (Entity rider : e.getPassengers()) rider.hurt(src, (float) (serverSpeed * 6.0));
			}
		}
		boolean siren = isPolice() && dispatchedFor != null && getPassengers().isEmpty();
		if (siren != sirenOn()) entityData.set(DATA_SIREN, siren);
	}

	/** Police AI: aim where the target's root vehicle will be, pick a speed from distance and heading error, back up when stuck. */
	private float[] policeAi() {
		ServerLevel level = (ServerLevel) level();
		ServerPlayer target = dispatchedFor == null ? null : level.getServer().getPlayerList().getPlayer(dispatchedFor);
		if (target == null || target.level() != level || WantedSystem.stars(target) <= 0) target = WantedSystem.nearestWanted(level, position(), 160);
		if (target == null) return new float[]{-0.3f * Math.signum(speed), 0f};
		Entity root = target.getRootVehicle();
		Vec3 lead = root instanceof CarEntity c ? root.position().add(root.getLookAngle().multiply(1, 0, 1).scale(c.serverSpeed * 10)) : root.position();
		double dx = lead.x - getX(), dz = lead.z - getZ(), dist = Math.sqrt(dx * dx + dz * dz);
		if (dist < bestDist - 0.5) { bestDist = dist; noProgressTicks = 0; } else noProgressTicks++;
		float desired = (float) (Mth.atan2(dz, dx) * Mth.RAD_TO_DEG) - 90f;
		float diff = Mth.wrapDegrees(desired - getYRot());
		if (reverseTicks > 0) {
			reverseTicks--;
			return new float[]{-1f, Mth.clamp(diff / 25f, -1f, 1f)};
		}
		if (horizontalCollision && Math.abs(speed) < 0.08f) {
			if (++stuckTicks > 15) { reverseTicks = 25; stuckTicks = 0; }
		} else stuckTicks = 0;
		float steer = Mth.clamp(-diff / 20f, -1f, 1f);
		float ad = Math.abs(diff), max = spec.maxSpeed();
		float targetSpeed = (float) Mth.clamp(dist / 10.0, 0.45, max) * (ad > 75 ? 0.3f : ad > 35 ? 0.55f : 1f);
		if (dist < 10 && ad < 30) targetSpeed = max * 0.8f;
		return new float[]{speed < targetSpeed ? 1f : -0.6f, steer};
	}

	/** Re-arm the "no progress" detector (the dispatcher recycles cars that make none). */
	public void resetProgress() { bestDist = Double.MAX_VALUE; noProgressTicks = 0; }

	public float wheelSpin(float pt) { return Mth.lerp(pt, wheelSpinO, wheelSpin); }
	public float steerAngle() { return steerVisual; }

	@Override
	protected void readAdditionalSaveData(CompoundTag tag) {
		if (tag.contains("Health")) entityData.set(DATA_HEALTH, tag.getFloat("Health"));
		if (tag.hasUUID("Owner")) owner = tag.getUUID("Owner");
		// The wanted level is a persistent player attachment, so a dispatched police car keeps chasing after a reload
		// (or leaves through PoliceDispatch once the stars are gone). Not restoring it left the car behind as a
		// parked, never-cleaned-up police car while the dispatcher sent new ones.
		dispatchedFor = tag.hasUUID("DispatchedFor") ? tag.getUUID("DispatchedFor") : null;
	}

	@Override
	protected void addAdditionalSaveData(CompoundTag tag) {
		tag.putFloat("Health", health());
		if (owner != null) tag.putUUID("Owner", owner);
		if (dispatchedFor != null) tag.putUUID("DispatchedFor", dispatchedFor);
	}

}
