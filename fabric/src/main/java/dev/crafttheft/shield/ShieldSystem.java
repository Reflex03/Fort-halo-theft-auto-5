package dev.crafttheft.shield;

import java.util.HashMap;
import java.util.Map;
import java.util.UUID;

import net.fabricmc.fabric.api.entity.event.v1.ServerLivingEntityEvents;
import net.fabricmc.fabric.api.entity.event.v1.ServerPlayerEvents;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.sounds.SoundSource;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.ai.attributes.AttributeInstance;
import net.minecraft.world.entity.ai.attributes.AttributeModifier;
import net.minecraft.world.entity.ai.attributes.Attributes;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.Hooks;
import dev.crafttheft.content.ModSounds;
import dev.crafttheft.crime.WantedSystem;

/**
 * Halo energy shield on top of vanilla absorption: a MAX_ABSORPTION modifier makes absorption the shield pool, so
 * vanilla drains it before health and syncs it to the client (HUD bar). Recharges after rechargeDelayTicks without
 * damage up to rechargeCap; Fortnite shield items fill the rest. Plays the Halo recharge and depleted sounds.
 */
public final class ShieldSystem {
	public static final ResourceLocation MAX_ID = CraftTheft.id("halo_shield_max");
	private static final Map<UUID, Long> LAST_HURT = new HashMap<>();
	private static final Map<UUID, Boolean> RECHARGING = new HashMap<>();
	private static final Map<UUID, Float> LAST_ABS = new HashMap<>();
	public static int rechargeSounds, depletedSounds;
	/** Test switch (harness): natural recharge on/off. Always true in play. */
	public static volatile boolean RECHARGE_ENABLED = true;

	private ShieldSystem() {}

	public static void register() {
		ServerLivingEntityEvents.AFTER_DAMAGE.register(ShieldSystem::onDamaged);
		Hooks.mark("player_damaged", "ShieldSystem.onDamaged");
		ServerPlayerEvents.AFTER_RESPAWN.register((oldP, newP, alive) -> {
			ensure(newP);
			if (!alive) {
				newP.setAbsorptionAmount(ShieldTuning.toMc(ShieldTuning.get().start));
				WantedSystem.set(newP, 0);
			}
		});
		Hooks.mark("player_respawn", "ShieldSystem (AFTER_RESPAWN): shield + wanted reset");
	}

	/** hooks row player_damaged: any damage resets the recharge delay. */
	public static void onDamaged(LivingEntity entity, DamageSource source, float base, float taken, boolean blocked) {
		if (entity instanceof ServerPlayer p) {
			LAST_HURT.put(p.getUUID(), p.serverLevel().getGameTime());
			RECHARGING.put(p.getUUID(), false);
		}
	}

	/** Gives the player the shield pool (MAX_ABSORPTION modifier). Called on join, respawn and every tick (cheap). */
	public static void ensure(ServerPlayer p) {
		AttributeInstance a = p.getAttribute(Attributes.MAX_ABSORPTION);
		if (a == null) return;
		float want = ShieldTuning.get().maxMc();
		AttributeModifier m = a.getModifier(MAX_ID);
		if (m == null || m.amount() != want) {
			a.removeModifier(MAX_ID);
			a.addPermanentModifier(new AttributeModifier(MAX_ID, want, AttributeModifier.Operation.ADD_VALUE));
		}
	}

	public static void tick(ServerPlayer p) {
		ensure(p);
		if (!p.isAlive()) return;
		ShieldTuning t = ShieldTuning.get();
		UUID id = p.getUUID();
		float cur = p.getAbsorptionAmount();
		float last = LAST_ABS.getOrDefault(id, cur);
		if (last > 0.01f && cur <= 0.01f) {
			depletedSounds++;
			p.serverLevel().playSound(null, p.getX(), p.getY(), p.getZ(), ModSounds.of("halo_snd_shield_depleted"), SoundSource.PLAYERS, 1f, 1f);
		}
		long now = p.serverLevel().getGameTime();
		long hurt = LAST_HURT.getOrDefault(id, Long.MIN_VALUE / 2);
		float cap = ShieldTuning.toMc(t.rechargeCap);
		if (RECHARGE_ENABLED && now - hurt >= t.rechargeDelayTicks && cur < cap - 1e-4f) {
			if (!RECHARGING.getOrDefault(id, false)) {
				RECHARGING.put(id, true);
				rechargeSounds++;
				p.serverLevel().playSound(null, p.getX(), p.getY(), p.getZ(), ModSounds.of("halo_snd_shield_recharge"), SoundSource.PLAYERS, 1f, 1f);
			}
			cur = Math.min(cap, cur + ShieldTuning.toMc(t.rechargePerTick));
			p.setAbsorptionAmount(cur);
		}
		LAST_ABS.put(id, p.getAbsorptionAmount());
	}

	/** Adds shield points up to a cap (Fortnite items); never lowers the shield. */
	public static void add(ServerPlayer p, float points, float capPoints) {
		float cur = p.getAbsorptionAmount();
		float cap = Math.min(ShieldTuning.toMc(capPoints), p.getMaxAbsorption());
		if (cur >= cap) return;
		p.setAbsorptionAmount(Math.min(cap, cur + ShieldTuning.toMc(points)));
	}

	/** Landing from the Battle Bus: the shield starts at systems.shield.start. */
	public static void onLanded(ServerPlayer p) {
		ensure(p);
		float start = ShieldTuning.toMc(ShieldTuning.get().start);
		if (p.getAbsorptionAmount() < start) p.setAbsorptionAmount(start);
	}

	public static void clear() {
		LAST_HURT.clear();
		RECHARGING.clear();
		LAST_ABS.clear();
	}

	public static void forget(ServerPlayer p) {
		LAST_HURT.remove(p.getUUID());
		RECHARGING.remove(p.getUUID());
		LAST_ABS.remove(p.getUUID());
	}
}
