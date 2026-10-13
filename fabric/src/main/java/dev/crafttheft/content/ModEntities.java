package dev.crafttheft.content;

import java.util.LinkedHashMap;
import java.util.Map;

import org.jetbrains.annotations.Nullable;

import net.minecraft.core.Registry;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.MobCategory;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.Hooks;
import dev.crafttheft.bus.BattleBusEntity;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.vehicle.CarEntity;
import dev.crafttheft.weapon.HaloGrenade;
import dev.crafttheft.weapon.HaloProjectile;
import dev.crafttheft.weapon.HaloRocket;

/** Entity types: one CarEntity type per vehicles row (crafttheft:&lt;vehicle id&gt;), the Battle Bus and Halo projectiles. */
public final class ModEntities {
	private static final Map<String, EntityType<CarEntity>> CARS = new LinkedHashMap<>();
	private static final Map<EntityType<?>, Sheets.Vehicle> SPEC = new LinkedHashMap<>();
	public static EntityType<BattleBusEntity> BUS;
	public static EntityType<HaloProjectile> PROJECTILE;
	public static EntityType<HaloRocket> ROCKET;
	public static EntityType<HaloGrenade> GRENADE;

	private ModEntities() {}

	public static void init() {
		for (Sheets.Vehicle v : Sheets.VEHICLES) {
			EntityType<CarEntity> type = Registry.register(BuiltInRegistries.ENTITY_TYPE, CraftTheft.id(v.id()),
					EntityType.Builder.<CarEntity>of(CarEntity::new, MobCategory.MISC).sized(v.width(), v.height())
							.clientTrackingRange(10).updateInterval(2).build(v.id()));
			CARS.put(v.id(), type);
			SPEC.put(type, v);
		}
		Hooks.mark("car_enter", "CarEntity.interact -> CrimeSystem.onCarEnter");
		Hooks.mark("car_damaged", "CarEntity.hurt -> CrimeSystem.onPoliceAttacked");
		BUS = Registry.register(BuiltInRegistries.ENTITY_TYPE, CraftTheft.id("battle_bus"),
				EntityType.Builder.<BattleBusEntity>of(BattleBusEntity::new, MobCategory.MISC).sized(3.0f, 3.0f).clientTrackingRange(16).updateInterval(1).build("battle_bus"));
		PROJECTILE = Registry.register(BuiltInRegistries.ENTITY_TYPE, CraftTheft.id("halo_projectile"),
				EntityType.Builder.<HaloProjectile>of(HaloProjectile::new, MobCategory.MISC).sized(0.25f, 0.25f).clientTrackingRange(8).updateInterval(1).build("halo_projectile"));
		ROCKET = Registry.register(BuiltInRegistries.ENTITY_TYPE, CraftTheft.id("halo_rocket"),
				EntityType.Builder.<HaloRocket>of(HaloRocket::new, MobCategory.MISC).sized(0.3f, 0.3f).clientTrackingRange(8).updateInterval(1).build("halo_rocket"));
		GRENADE = Registry.register(BuiltInRegistries.ENTITY_TYPE, CraftTheft.id("halo_grenade"),
				EntityType.Builder.<HaloGrenade>of(HaloGrenade::new, MobCategory.MISC).sized(0.25f, 0.25f).clientTrackingRange(8).updateInterval(1).build("halo_grenade"));
	}

	public static EntityType<CarEntity> car(String vehicleId) {
		EntityType<CarEntity> t = CARS.get(vehicleId);
		if (t == null) throw new IllegalArgumentException("no vehicles row " + vehicleId);
		return t;
	}

	public static Map<String, EntityType<CarEntity>> cars() { return CARS; }

	@Nullable
	public static Sheets.Vehicle spec(EntityType<?> type) { return SPEC.get(type); }
}
