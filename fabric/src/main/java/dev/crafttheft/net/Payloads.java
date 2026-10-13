package dev.crafttheft.net;

import net.fabricmc.fabric.api.networking.v1.PayloadTypeRegistry;
import net.fabricmc.fabric.api.networking.v1.ServerPlayNetworking;
import net.minecraft.network.RegistryFriendlyByteBuf;
import net.minecraft.network.codec.ByteBufCodecs;
import net.minecraft.network.codec.StreamCodec;
import net.minecraft.network.protocol.common.custom.CustomPacketPayload;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.Hooks;
import dev.crafttheft.bus.BattleBusEntity;
import dev.crafttheft.vehicle.CarEntity;
import dev.crafttheft.weapon.WeaponSystem;

/** Client -> server payloads. Handlers run on the server thread (Fabric, 1.20.5+). */
public final class Payloads {
	/** Attack key pressed/released while holding a Halo weapon (hooks row weapon_fire). */
	public record Trigger(boolean down) implements CustomPacketPayload {
		public static final Type<Trigger> TYPE = new Type<>(CraftTheft.id("trigger"));
		public static final StreamCodec<RegistryFriendlyByteBuf, Trigger> CODEC = StreamCodec.composite(ByteBufCodecs.BOOL, Trigger::down, Trigger::new);
		@Override public Type<? extends CustomPacketPayload> type() { return TYPE; }
	}

	/** Reload key (R). */
	public record Reload() implements CustomPacketPayload {
		public static final Type<Reload> TYPE = new Type<>(CraftTheft.id("reload"));
		public static final StreamCodec<RegistryFriendlyByteBuf, Reload> CODEC = StreamCodec.unit(new Reload());
		@Override public Type<? extends CustomPacketPayload> type() { return TYPE; }
	}

	/** Horn key while driving. */
	public record Horn() implements CustomPacketPayload {
		public static final Type<Horn> TYPE = new Type<>(CraftTheft.id("horn"));
		public static final StreamCodec<RegistryFriendlyByteBuf, Horn> CODEC = StreamCodec.unit(new Horn());
		@Override public Type<? extends CustomPacketPayload> type() { return TYPE; }
	}

	/** Jump key pressed while riding the Battle Bus (an event, so it survives server lag; the vanilla input packet's
	 * jumping flag is only a state and can be overwritten before the server ticks). */
	public record BusJump() implements CustomPacketPayload {
		public static final Type<BusJump> TYPE = new Type<>(CraftTheft.id("bus_jump"));
		public static final StreamCodec<RegistryFriendlyByteBuf, BusJump> CODEC = StreamCodec.unit(new BusJump());
		@Override public Type<? extends CustomPacketPayload> type() { return TYPE; }
	}

	private Payloads() {}

	public static void register() {
		PayloadTypeRegistry.playC2S().register(Trigger.TYPE, Trigger.CODEC);
		PayloadTypeRegistry.playC2S().register(Reload.TYPE, Reload.CODEC);
		PayloadTypeRegistry.playC2S().register(Horn.TYPE, Horn.CODEC);
		PayloadTypeRegistry.playC2S().register(BusJump.TYPE, BusJump.CODEC);
		ServerPlayNetworking.registerGlobalReceiver(Trigger.TYPE, (p, ctx) -> WeaponSystem.onTrigger(ctx.player(), p.down()));
		Hooks.mark("weapon_fire", "WeaponSystem.onTrigger -> CrimeSystem.onGunfire");
		ServerPlayNetworking.registerGlobalReceiver(Reload.TYPE, (p, ctx) -> WeaponSystem.onReload(ctx.player()));
		ServerPlayNetworking.registerGlobalReceiver(BusJump.TYPE, (p, ctx) -> {
			if (ctx.player().getVehicle() instanceof BattleBusEntity bus) bus.requestDrop(ctx.player());
		});
		ServerPlayNetworking.registerGlobalReceiver(Horn.TYPE, (p, ctx) -> {
			if (ctx.player().getVehicle() instanceof CarEntity car && car.getControllingPassenger() == ctx.player()) car.honk();
		});
	}
}
