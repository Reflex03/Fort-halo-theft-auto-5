package dev.crafttheft.bus;

import java.util.HashMap;
import java.util.List;
import java.util.Map;

import net.minecraft.nbt.CompoundTag;
import net.minecraft.network.syncher.SynchedEntityData;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.util.Mth;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EntityDimensions;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.Vec3;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.content.SourceRows;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.melty.MeltyChecks;
import dev.crafttheft.mixin.LivingEntityAccessor;

/**
 * The Battle Bus: server-moved, no physics, flies its route at systems.battle_bus.speed. Riders press jump to drop
 * (the vanilla input packet sets jumping on the server); at the end of the route everyone is dropped
 * (autoDropAtEnd). Any way of leaving the bus starts a skydive.
 */
public class BattleBusEntity extends Entity {
	public static final String MESH_SOURCE = "fn_mesh_battle_bus";
	private int life;
	private int maxLife = (int) (Sheets.BATTLE_BUS_ROUTE_LENGTH / Sheets.BATTLE_BUS_SPEED);
	private int lerpSteps;
	private double lerpX, lerpY, lerpZ;
	private float lerpYRot;
	private final Map<Entity, Integer> ridden = new HashMap<>();

	public BattleBusEntity(EntityType<? extends BattleBusEntity> type, Level level) {
		super(type, level);
		this.noPhysics = true;
		this.setNoGravity(true);
	}

	/**
	 * The rider pressed jump (C2S crafttheft:bus_jump): drop right away, on the server thread. Not deferred to the bus
	 * tick: while the server is still generating the chunks under the bus it does not tick, and the jump must work.
	 */
	public void requestDrop(ServerPlayer p) {
		if (!hasPassenger(p)) return;
		CraftTheft.LOG.info("[bus] {} jumped at {} (life {}/{})", p.getName().getString(), p.blockPosition(), life, maxLife);
		p.stopRiding();
	}

	public void setRoute(double lengthBlocks) { this.maxLife = (int) Math.ceil(lengthBlocks / Sheets.BATTLE_BUS_SPEED); }
	public int life() { return life; }
	public int maxLife() { return maxLife; }
	public String meshKey() { return SourceRows.meshKey(MESH_SOURCE); }

	@Override protected void defineSynchedData(SynchedEntityData.Builder b) {}
	@Override protected boolean canAddPassenger(Entity e) { return getPassengers().size() < 16; }
	@Override public boolean isPickable() { return false; }

	@Override
	protected Vec3 getPassengerAttachmentPoint(Entity passenger, EntityDimensions dims, float scale) {
		return new Vec3(0, 1.0, 0); // inside the bus
	}

	/** Leave through the floor at the back, clear of the mesh. */
	@Override
	public Vec3 getDismountLocationForPassenger(LivingEntity passenger) {
		Vec3 fwd = Vec3.directionFromRotation(0, getYRot());
		return position().subtract(fwd.scale(4.0)).add(0, -2.0, 0);
	}

	@Override
	protected void removePassenger(Entity passenger) {
		super.removePassenger(passenger);
		Integer rode = ridden.remove(passenger);
		if (passenger instanceof ServerPlayer sp && sp.hasDisconnected()) {
			// Quitting on the bus is not a drop: vanilla saves the player with the bus as RootVehicle and only then
			// dismounts (PlayerList.remove), and a rejoin re-seats the player on the restored bus. Recording a drop
			// here marked the player as dropped and started a skydive on the leaving player, so a bus that could not
			// be restored left the player at the bus altitude with no glider and no second bus.
			CraftTheft.LOG.info("[bus] {} left the game on the bus at {}: the ride resumes on rejoin", sp.getName().getString(), sp.blockPosition());
			return;
		}
		if (passenger instanceof ServerPlayer sp && rode != null && rode > 0)
			MeltyChecks.pass(MeltyChecks.BUS_RIDE, String.format("%s rode the Battle Bus %.0f blocks at y=%d before dropping", sp.getName().getString(), rode * Sheets.BATTLE_BUS_SPEED, (int) getY()));
		if (passenger instanceof ServerPlayer sp && !level().isClientSide) {
			BattleBus.onDropped(sp, this, flightVelocity());
		}
	}

	public Vec3 flightVelocity() {
		float rad = getYRot() * Mth.DEG_TO_RAD;
		return new Vec3(-Mth.sin(rad) * Sheets.BATTLE_BUS_SPEED, 0, Mth.cos(rad) * Sheets.BATTLE_BUS_SPEED);
	}

	@Override
	public void lerpTo(double x, double y, double z, float yRot, float xRot, int steps) {
		lerpX = x; lerpY = y; lerpZ = z; lerpYRot = yRot; lerpSteps = 3;
	}

	@Override
	public void tick() {
		super.tick();
		Vec3 v = flightVelocity();
		setDeltaMovement(v);
		if (level().isClientSide) {
			if (lerpSteps > 0) { lerpPositionAndRotationStep(lerpSteps, lerpX, lerpY, lerpZ, lerpYRot, getXRot()); lerpSteps--; }
			else setPos(position().add(v)); // dead-reckon between packets
			return;
		}
		setPos(position().add(v));
		life++;
		for (Entity p : List.copyOf(getPassengers())) {
			if (!(p instanceof ServerPlayer sp)) continue;
			int t = ridden.merge(sp, 1, Integer::sum);
			if (t == 40) MeltyChecks.pass(MeltyChecks.BUS_RIDE, String.format("%s rode the Battle Bus %.0f blocks at y=%d", sp.getName().getString(), t * Sheets.BATTLE_BUS_SPEED, (int) getY()));
			boolean end = life >= maxLife && Sheets.BATTLE_BUS_AUTO_DROP_AT_END;
			boolean jump = ((LivingEntityAccessor) sp).crafttheft$isJumping();
			if ((jump && t > 10) || end) {
				CraftTheft.LOG.info("[bus] {} {} at {} (life {}/{})", sp.getName().getString(), end ? "dropped at the end of the route" : "jumped", sp.blockPosition(), life, maxLife);
				sp.stopRiding();
			}
		}
		if (life >= maxLife + 40 || (getPassengers().isEmpty() && life > 200)) discard();
	}

	@Override protected void readAdditionalSaveData(CompoundTag tag) { life = tag.getInt("Life"); if (tag.contains("MaxLife")) maxLife = tag.getInt("MaxLife"); }
	@Override protected void addAdditionalSaveData(CompoundTag tag) { tag.putInt("Life", life); tag.putInt("MaxLife", maxLife); }
}
