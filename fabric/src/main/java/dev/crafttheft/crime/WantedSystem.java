package dev.crafttheft.crime;

import java.util.HashMap;
import java.util.Map;
import java.util.UUID;

import org.jetbrains.annotations.Nullable;

import net.minecraft.network.chat.Component;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.content.ModAttachments;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.melty.MeltyChecks;
import dev.crafttheft.vehicle.CarEntity;

/**
 * GTA wanted level: stars in a synced player attachment. A crime sets at least its row's stars and adds addStars
 * when already wanted. The level drops by one after the wanted row's loseAfterTicks out of police sight.
 */
public final class WantedSystem {
	public static final int MAX_STARS = Sheets.WANTED.stream().mapToInt(Sheets.Wanted::stars).max().orElse(5);
	public static final double SIGHT_RANGE = 48;
	private static final Map<UUID, Long> LAST_SEEN = new HashMap<>();

	private WantedSystem() {}

	public static int stars(ServerPlayer p) { return p.getAttachedOrElse(ModAttachments.WANTED, 0); }

	public static void set(ServerPlayer p, int stars) {
		int s = Math.max(0, Math.min(MAX_STARS, stars));
		p.setAttached(ModAttachments.WANTED, s);
		LAST_SEEN.put(p.getUUID(), p.serverLevel().getGameTime());
	}

	/** The wanted sheet row for a level (the highest row at or below it), or null at 0 stars. */
	@Nullable
	public static Sheets.Wanted row(int stars) {
		Sheets.Wanted best = null;
		for (Sheets.Wanted w : Sheets.WANTED) if (w.stars() <= stars && (best == null || w.stars() > best.stars())) best = w;
		return stars <= 0 ? null : best;
	}

	public static void commit(ServerPlayer p, Sheets.Crime c) {
		int before = stars(p);
		int after = before > 0 ? Math.max(c.stars(), before + c.addStars()) : c.stars();
		set(p, after);
		int now = stars(p);
		CraftTheft.LOG.info("[wanted] {} committed '{}' ({}): {} -> {} stars", p.getName().getString(), c.id(), c.name(), before, now);
		p.displayClientMessage(Component.translatable("crafttheft.wanted.crime", Component.translatable("crafttheft.crime." + c.id())), true);
		if (now >= 1) MeltyChecks.pass(MeltyChecks.WANTED_STARS, c.id() + " -> " + now + " stars");
	}

	public static void tick(ServerPlayer p) {
		int s = stars(p);
		if (s <= 0) return;
		ServerLevel level = p.serverLevel();
		long now = level.getGameTime();
		if (now % 10 == 0 && seenByPolice(p)) LAST_SEEN.put(p.getUUID(), now);
		Sheets.Wanted row = row(s);
		long last = LAST_SEEN.computeIfAbsent(p.getUUID(), k -> now);
		if (row != null && now - last > row.loseAfterTicks()) {
			set(p, s - 1);
			CraftTheft.LOG.info("[wanted] {} out of police sight for {} ticks: {} -> {} stars", p.getName().getString(), now - last, s, s - 1);
			p.displayClientMessage(Component.translatable("crafttheft.wanted.lost"), true);
		}
	}

	public static boolean seenByPolice(ServerPlayer p) {
		Vec3 eye = p.getEyePosition();
		// wanted.loseAfterTicks counts time out of sight of the police that chase the player: a police car the player
		// stole (not dispatched any more, possibly the one he drives) or a parked one never keeps the stars up
		for (CarEntity c : p.serverLevel().getEntitiesOfClass(CarEntity.class, p.getBoundingBox().inflate(SIGHT_RANGE),
				c -> c.isPolice() && c.isDispatched() && !c.hasPassenger(p))) {
			Vec3 from = c.position().add(0, c.getBbHeight() * 0.8, 0);
			if (p.serverLevel().clip(new ClipContext(from, eye, ClipContext.Block.VISUAL, ClipContext.Fluid.NONE, c)).getType() != HitResult.Type.BLOCK) return true;
		}
		return false;
	}

	/** Nearest wanted player within range (police AI target). */
	@Nullable
	public static ServerPlayer nearestWanted(ServerLevel level, Vec3 from, double range) {
		ServerPlayer best = null;
		double bestD = range * range;
		for (ServerPlayer p : level.players()) {
			if (stars(p) <= 0 || p.isSpectator() || !p.isAlive()) continue;
			double d = p.distanceToSqr(from);
			if (d < bestD) { bestD = d; best = p; }
		}
		return best;
	}

	public static void forget(ServerPlayer p) { LAST_SEEN.remove(p.getUUID()); }

	public static void clear() { LAST_SEEN.clear(); }
}
