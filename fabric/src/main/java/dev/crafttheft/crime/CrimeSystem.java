package dev.crafttheft.crime;

import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import org.jetbrains.annotations.Nullable;

import net.fabricmc.fabric.api.entity.event.v1.ServerLivingEntityEvents;
import net.fabricmc.fabric.api.event.player.AttackEntityCallback;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.animal.IronGolem;
import net.minecraft.world.entity.npc.AbstractVillager;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.EntityHitResult;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.Hooks;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.vehicle.CarEntity;

/**
 * Crimes sheet -> wanted stars. Each crimes row names the hooks row that detects it; this class holds one rule per
 * crime id (what the victim/situation must be) and the hook handlers fire every row whose hook matches.
 */
public final class CrimeSystem {
	/** What a hook saw. */
	public record Event(String hook, ServerPlayer player, @Nullable Entity victim) {}

	/** Rule per crimes row id. A row without a rule is reported at startup (and by the harness). */
	public static final Map<String, java.util.function.Predicate<Event>> RULES = new LinkedHashMap<>();
	static {
		RULES.put("assault_villager", e -> e.victim() instanceof AbstractVillager);
		RULES.put("kill_villager", e -> e.victim() instanceof AbstractVillager);
		RULES.put("assault_golem", e -> e.victim() instanceof IronGolem);
		RULES.put("steal_car", e -> e.victim() instanceof CarEntity);
		RULES.put("assault_police", e -> e.victim() instanceof CarEntity c && c.isPolice());
		RULES.put("gunfire", e -> e.victim() instanceof AbstractVillager);
	}
	public static final double GUNFIRE_RADIUS = 16;
	private static final Map<String, Long> DEDUPE = new HashMap<>();
	/** Crimes committed (row id -> count), for the harness and logs. */
	public static final Map<String, Integer> COUNTS = new LinkedHashMap<>();

	private CrimeSystem() {}

	public static void register() {
		for (Sheets.Crime c : Sheets.CRIMES)
			if (!RULES.containsKey(c.id())) CraftTheft.LOG.error("[crime] crimes row '{}' has no rule in CrimeSystem; it can never fire", c.id());
		AttackEntityCallback.EVENT.register(CrimeSystem::onAttack);
		Hooks.mark("attack_entity", "CrimeSystem.onAttack");
		ServerLivingEntityEvents.AFTER_DEATH.register(CrimeSystem::onDeath);
		Hooks.mark("entity_death", "CrimeSystem.onDeath");
	}

	/** hooks row attack_entity (melee). PASS keeps vanilla damage. */
	public static InteractionResult onAttack(Player player, Level level, InteractionHand hand, Entity entity, @Nullable EntityHitResult hit) {
		if (!level.isClientSide && player instanceof ServerPlayer sp && !sp.isSpectator()) fire(new Event("attack_entity", sp, entity));
		return InteractionResult.PASS;
	}

	/**
	 * Ranged half of hooks row attack_entity: AttackEntityCallback only sees melee, so a Halo weapon hit (hitscan round,
	 * plasma bolt/needle, rocket/grenade blast) that damaged an entity is reported here as the same "attack_entity"
	 * event. Without it shooting a villager or an iron golem from beyond the gunfire radius was no crime at all.
	 */
	public static void onRangedHit(@Nullable Entity attacker, Entity victim) {
		if (attacker instanceof ServerPlayer sp && !sp.isSpectator() && victim != sp) fire(new Event("attack_entity", sp, victim));
	}

	/** Per-world bookkeeping is dropped when a server stops (singleplayer can open another world in the same JVM). */
	public static void clear() { DEDUPE.clear(); }

	/** hooks row entity_death: the killer is the damage source's causing entity (guns, cars, melee). */
	public static void onDeath(LivingEntity entity, DamageSource source) {
		if (source.getEntity() instanceof ServerPlayer sp) fire(new Event("entity_death", sp, entity));
	}

	/** hooks row car_enter (from CarEntity.interact): entering a car someone else owns. */
	public static void onCarEnter(ServerPlayer p, CarEntity car) {
		fire(new Event("car_enter", p, car));
	}

	/** hooks row car_damaged (from CarEntity.hurt). */
	public static void onPoliceAttacked(ServerPlayer p, CarEntity car) {
		fire(new Event("car_damaged", p, car));
	}

	/** hooks row weapon_fire (from WeaponSystem): the nearest villager within earshot is the "victim". */
	public static void onGunfire(ServerPlayer p) {
		List<AbstractVillager> near = p.serverLevel().getEntitiesOfClass(AbstractVillager.class, p.getBoundingBox().inflate(GUNFIRE_RADIUS), Entity::isAlive);
		if (!near.isEmpty()) fire(new Event("weapon_fire", p, near.get(0)));
	}

	public static void fire(Event e) {
		long now = e.player().serverLevel().getGameTime();
		for (Sheets.Crime c : Sheets.CRIMES) {
			if (!c.hook().equals(e.hook())) continue;
			var rule = RULES.get(c.id());
			if (rule == null || !rule.test(e)) continue;
			// one hit is often reported by several paths in the same instant (melee + damage); count it once
			String key = c.id() + "/" + e.player().getUUID() + "/" + (e.victim() == null ? 0 : e.victim().getId());
			Long last = DEDUPE.get(key);
			if (last != null && now - last < 20) continue;
			DEDUPE.put(key, now);
			if (DEDUPE.size() > 512) DEDUPE.entrySet().removeIf(x -> now - x.getValue() > 200);
			COUNTS.merge(c.id(), 1, Integer::sum);
			WantedSystem.commit(e.player(), c);
		}
	}
}
