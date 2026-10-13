package dev.crafttheft.bus;

import java.util.HashMap;
import java.util.Map;
import java.util.UUID;

import net.minecraft.resources.ResourceLocation;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.ai.attributes.AttributeInstance;
import net.minecraft.world.entity.ai.attributes.AttributeModifier;
import net.minecraft.world.entity.ai.attributes.Attributes;
import net.minecraft.world.level.levelgen.Heightmap;
import net.minecraft.world.phys.Vec3;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.content.ModAttachments;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.melty.MeltyChecks;
import dev.crafttheft.shield.ShieldSystem;

/**
 * After the bus: SKYDIVE (free fall at systems.skydive.fallSpeed), then the glider opens by itself
 * gliderAutoOpenAgl blocks above the ground (GLIDE at gliderSpeed), then landing. The vertical speed comes from the
 * vanilla GRAVITY attribute (synced, so the player's own client moves accordingly: terminal speed = 49 x gravity);
 * FALL_DAMAGE_MULTIPLIER -1 removes fall damage. Steering (steerSpeed) is applied by the client (ClientInput).
 */
public final class Skydive {
	public static final int NONE = 0, SKYDIVE = 1, GLIDE = 2;
	public static final ResourceLocation GRAVITY_ID = CraftTheft.id("skydive_gravity");
	static final ResourceLocation NOFALL_ID = CraftTheft.id("skydive_nofall");
	private static final Map<UUID, Float> HEALTH_AT_DROP = new HashMap<>();
	public static int landings;

	private Skydive() {}

	/** Vanilla travel(): vy = (vy - g) * 0.98, so terminal speed = 49 g. */
	public static double gravityFor(float blocksPerTick) { return blocksPerTick / 49.0; }

	public static int state(ServerPlayer p) { return p.getAttachedOrElse(ModAttachments.GLIDE, NONE); }

	public static void start(ServerPlayer p, Vec3 inherit) {
		setGravity(p, gravityFor(Sheets.SKYDIVE_FALL_SPEED));
		noFall(p);
		p.setAttached(ModAttachments.GLIDE, SKYDIVE);
		HEALTH_AT_DROP.put(p.getUUID(), p.getHealth());
		p.setDeltaMovement(inherit.x, -0.2, inherit.z);
		p.hurtMarked = true;
		CraftTheft.LOG.info("[skydive] {} skydiving from y={}", p.getName().getString(), (int) p.getY());
	}

	private static void noFall(ServerPlayer p) {
		AttributeInstance fall = p.getAttribute(Attributes.FALL_DAMAGE_MULTIPLIER);
		if (fall != null && !fall.hasModifier(NOFALL_ID))
			fall.addOrUpdateTransientModifier(new AttributeModifier(NOFALL_ID, -1.0, AttributeModifier.Operation.ADD_MULTIPLIED_TOTAL));
	}

	public static void tick(ServerPlayer p) {
		int s = state(p);
		if (s == NONE) return;
		// transient modifiers are not saved: re-apply after a reconnect mid-fall
		setGravity(p, gravityFor(s == SKYDIVE ? Sheets.SKYDIVE_FALL_SPEED : Sheets.SKYDIVE_GLIDER_SPEED));
		noFall(p);
		p.resetFallDistance();
		// ServerPlayer.onGround() can stay stale with tiny gravity: also probe for a block under the feet
		boolean grounded = p.onGround() || !p.level().noCollision(p, p.getBoundingBox().move(0, -0.1, 0));
		if (grounded || p.isInWater() || p.isInLava() || p.isPassenger() || !p.isAlive() || p.getAbilities().flying) {
			end(p, grounded || p.isInWater());
			return;
		}
		if (s == SKYDIVE) {
			int bx = p.getBlockX(), bz = p.getBlockZ();
			boolean loaded = p.serverLevel().hasChunk(bx >> 4, bz >> 4);
			int ground = loaded ? p.serverLevel().getHeight(Heightmap.Types.MOTION_BLOCKING, bx, bz) : p.serverLevel().getSeaLevel();
			double agl = p.getY() - ground;
			if (agl < Sheets.SKYDIVE_GLIDER_AUTO_OPEN_AGL) {
				p.setAttached(ModAttachments.GLIDE, GLIDE);
				setGravity(p, gravityFor(Sheets.SKYDIVE_GLIDER_SPEED));
				Vec3 v = p.getDeltaMovement();
				p.setDeltaMovement(v.x, Math.max(v.y, -Sheets.SKYDIVE_GLIDER_SPEED), v.z);
				p.hurtMarked = true;
				CraftTheft.LOG.info("[skydive] {} glider opened at y={} ({} blocks above ground{})", p.getName().getString(), (int) p.getY(), (int) agl, loaded ? "" : ", chunk not loaded: sea level used");
			}
		}
	}

	public static void end(ServerPlayer p, boolean landed) {
		AttributeInstance g = p.getAttribute(Attributes.GRAVITY);
		if (g != null) g.removeModifier(GRAVITY_ID);
		AttributeInstance fall = p.getAttribute(Attributes.FALL_DAMAGE_MULTIPLIER);
		if (fall != null) fall.removeModifier(NOFALL_ID);
		p.setAttached(ModAttachments.GLIDE, NONE);
		p.resetFallDistance();
		Float before = HEALTH_AT_DROP.remove(p.getUUID());
		CraftTheft.LOG.info("[skydive] {} {} at {} health {} (at drop {})", p.getName().getString(), landed ? "landed" : "stopped skydiving", p.blockPosition(), p.getHealth(), before);
		if (landed && p.isAlive()) {
			landings++;
			ShieldSystem.onLanded(p);
			if (before != null && p.getHealth() < before - 0.01f) MeltyChecks.fail(MeltyChecks.LANDED, "lost " + (before - p.getHealth()) + " health during the drop");
			else MeltyChecks.pass(MeltyChecks.LANDED, "landed at " + p.blockPosition().toShortString() + " without fall damage");
		}
	}

	public static void forget(ServerPlayer p) { HEALTH_AT_DROP.remove(p.getUUID()); }

	public static void clear() { HEALTH_AT_DROP.clear(); }

	private static void setGravity(ServerPlayer p, double target) {
		AttributeInstance g = p.getAttribute(Attributes.GRAVITY);
		if (g == null) return;
		double amount = target - 0.08;
		AttributeModifier m = g.getModifier(GRAVITY_ID);
		if (m == null || Math.abs(m.amount() - amount) > 1e-9)
			g.addOrUpdateTransientModifier(new AttributeModifier(GRAVITY_ID, amount, AttributeModifier.Operation.ADD_VALUE));
	}
}
