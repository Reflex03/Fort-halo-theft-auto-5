package dev.crafttheft.client;

import java.util.HashMap;
import java.util.Map;

import net.fabricmc.fabric.api.client.networking.v1.ClientPlayNetworking;
import net.minecraft.client.CameraType;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.Minecraft;
import net.minecraft.client.player.AbstractClientPlayer;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.util.Mth;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.phys.Vec3;

import dev.crafttheft.bus.BattleBusEntity;
import dev.crafttheft.bus.Skydive;
import dev.crafttheft.client.cache.CacheGuard;
import dev.crafttheft.client.sound.SirenSound;
import dev.crafttheft.content.ModAttachments;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.net.Payloads;
import dev.crafttheft.vehicle.CarEntity;
import dev.crafttheft.weapon.HaloWeaponItem;

/** hooks rows client_tick and pre_attack: trigger state, reload/horn keys, scope zoom, skydive steering, camera, sirens. */
public final class ClientInput {
	public static KeyMapping RELOAD, HORN;
	/** Test harness overrides (never set in normal play). */
	public static boolean forceFire, forceZoom;
	private static boolean firingSent, zoomed, busJumpSent;
	private static CameraType savedCamera;
	private static final Map<Integer, SirenSound> SIRENS = new HashMap<>();
	private static int ticks;

	private ClientInput() {}

	/** hooks row pre_attack: holding a gun, left click fires (streamed by tick) instead of punching or mining. */
	public static boolean onPreAttack(Minecraft mc, LocalPlayer player, int clicks) {
		return player.getMainHandItem().getItem() instanceof HaloWeaponItem;
	}

	public static boolean zoomed() { return zoomed; }

	/** FOV multiplier for ZoomMixin: the held weapon's zoom while scoped, else 1. */
	public static float zoomFactor(AbstractClientPlayer p) {
		if (!zoomed || p != Minecraft.getInstance().player) return 1f;
		return p.getMainHandItem().getItem() instanceof HaloWeaponItem w ? w.stats().zoom : 1f;
	}

	public static void tick(Minecraft mc) {
		CacheGuard.tick(mc);
		LocalPlayer p = mc.player;
		if (p == null) { zoomed = false; return; }
		ticks++;
		boolean gun = p.getMainHandItem().getItem() instanceof HaloWeaponItem;
		boolean wantFire = (mc.screen == null && mc.options.keyAttack.isDown() && gun) || forceFire;
		if (wantFire != firingSent) {
			ClientPlayNetworking.send(new Payloads.Trigger(wantFire));
			firingSent = wantFire;
		}
		while (RELOAD.consumeClick()) if (gun) ClientPlayNetworking.send(new Payloads.Reload());
		while (HORN.consumeClick()) if (p.getVehicle() instanceof CarEntity c && c.getControllingPassenger() == p) ClientPlayNetworking.send(new Payloads.Horn());
		zoomed = gun && ((mc.screen == null && mc.options.keyUse.isDown()) || forceZoom) && ((HaloWeaponItem) p.getMainHandItem().getItem()).stats().zoom < 1f;

		boolean busJump = p.getVehicle() instanceof BattleBusEntity && mc.screen == null && mc.options.keyJump.isDown();
		if (busJump && !busJumpSent) {
			ClientPlayNetworking.send(new Payloads.BusJump());
			dev.crafttheft.CraftTheft.LOG.info("[bus] jump pressed on the Battle Bus: asking the server to drop");
		}
		busJumpSent = busJump;

		int glide = p.getAttachedOrElse(ModAttachments.GLIDE, Skydive.NONE);
		if (glide != Skydive.NONE && !p.onGround()) steer(p);
		if (glide == Skydive.SKYDIVE && !p.onGround()) predictGlider(mc, p);

		boolean wantThird = p.getVehicle() instanceof BattleBusEntity || p.getVehicle() instanceof CarEntity || glide != Skydive.NONE;
		if (wantThird && savedCamera == null) { savedCamera = mc.options.getCameraType(); mc.options.setCameraType(CameraType.THIRD_PERSON_BACK); }
		else if (!wantThird && savedCamera != null) { mc.options.setCameraType(savedCamera); savedCamera = null; }

		if (ticks % 10 == 0 && mc.level != null) sirens(mc, p);
	}

	/**
	 * The server opens the glider (state + GRAVITY modifier, both synced); when the server lags the player would fall
	 * on at free-fall speed until that arrives. The client applies the same glider gravity as soon as it sees the
	 * auto-open height itself; the server's state confirms it moments later.
	 */
	private static void predictGlider(Minecraft mc, LocalPlayer p) {
		if (mc.level == null || !mc.level.hasChunkAt(p.blockPosition())) return;
		int ground = mc.level.getHeight(net.minecraft.world.level.levelgen.Heightmap.Types.MOTION_BLOCKING, p.getBlockX(), p.getBlockZ());
		if (p.getY() - ground >= Sheets.SKYDIVE_GLIDER_AUTO_OPEN_AGL) return;
		var g = p.getAttribute(net.minecraft.world.entity.ai.attributes.Attributes.GRAVITY);
		if (g == null) return;
		g.addOrUpdateTransientModifier(new net.minecraft.world.entity.ai.attributes.AttributeModifier(Skydive.GRAVITY_ID,
				Skydive.gravityFor(Sheets.SKYDIVE_GLIDER_SPEED) - 0.08, net.minecraft.world.entity.ai.attributes.AttributeModifier.Operation.ADD_VALUE));
		Vec3 v = p.getDeltaMovement();
		if (v.y < -Sheets.SKYDIVE_GLIDER_SPEED) p.setDeltaMovement(v.x, -Sheets.SKYDIVE_GLIDER_SPEED, v.z);
	}

	/** Free fall / glide steering at systems.skydive.steerSpeed (the client owns its player's movement). */
	private static void steer(LocalPlayer p) {
		float fwd = p.input.forwardImpulse, left = p.input.leftImpulse;
		if (Math.abs(fwd) < 0.01f && Math.abs(left) < 0.01f) return;
		float yaw = p.getYRot() * Mth.DEG_TO_RAD;
		Vec3 f = new Vec3(-Mth.sin(yaw), 0, Mth.cos(yaw)), l = new Vec3(Mth.cos(yaw), 0, Mth.sin(yaw));
		Vec3 want = f.scale(fwd).add(l.scale(left)).normalize().scale(Sheets.SKYDIVE_STEER_SPEED / 0.91);
		Vec3 v = p.getDeltaMovement();
		Vec3 h = new Vec3(v.x, 0, v.z);
		Vec3 nh = h.add(want.subtract(h).scale(0.35));
		p.setDeltaMovement(nh.x, v.y, nh.z);
	}

	private static void sirens(Minecraft mc, Player p) {
		SIRENS.entrySet().removeIf(e -> e.getValue().isStopped());
		for (Entity e : mc.level.entitiesForRendering()) {
			if (e instanceof CarEntity c && c.sirenOn() && c.distanceTo(p) < 96 && !SIRENS.containsKey(c.getId())) {
				SirenSound s = new SirenSound(c);
				SIRENS.put(c.getId(), s);
				mc.getSoundManager().play(s);
			}
		}
	}

	public static int activeSirens() { return (int) SIRENS.values().stream().filter(s -> !s.isStopped()).count(); }
}
