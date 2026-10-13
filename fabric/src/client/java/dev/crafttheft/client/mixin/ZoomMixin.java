package dev.crafttheft.client.mixin;

import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

import net.minecraft.client.player.AbstractClientPlayer;

import dev.crafttheft.client.ClientInput;

/** hooks row fov_zoom: scope zoom for weapons whose zoom is below 1 (battle rifle, sniper), while use is held. */
@Mixin(AbstractClientPlayer.class)
public abstract class ZoomMixin {
	@Inject(method = "getFieldOfViewModifier", at = @At("RETURN"), cancellable = true)
	private void crafttheft$scope(CallbackInfoReturnable<Float> cir) {
		float z = ClientInput.zoomFactor((AbstractClientPlayer) (Object) this);
		if (z < 1f) cir.setReturnValue(cir.getReturnValueF() * z);
	}
}
