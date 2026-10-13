package dev.crafttheft.client.mixin;

import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.ModifyArg;

import net.minecraft.client.Camera;
import net.minecraft.client.Minecraft;

import dev.crafttheft.bus.BattleBusEntity;
import dev.crafttheft.bus.Skydive;
import dev.crafttheft.content.ModAttachments;
import dev.crafttheft.vehicle.CarEntity;

/** hooks row camera: pull the third-person camera back on the bus, in cars and while skydiving. */
@Mixin(Camera.class)
public abstract class CameraMixin {
	@ModifyArg(method = "setup", at = @At(value = "INVOKE", target = "Lnet/minecraft/client/Camera;getMaxZoom(F)F"), index = 0)
	private float crafttheft$vehicleZoom(float dist) {
		var p = Minecraft.getInstance().player;
		if (p == null) return dist;
		if (p.getVehicle() instanceof BattleBusEntity) return 16f;
		if (p.getVehicle() instanceof CarEntity) return 7f;
		if (p.getAttachedOrElse(ModAttachments.GLIDE, Skydive.NONE) != Skydive.NONE) return 6f;
		return dist;
	}
}
