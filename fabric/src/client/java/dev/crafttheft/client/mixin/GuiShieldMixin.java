package dev.crafttheft.client.mixin;

import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

import net.minecraft.client.gui.Gui;
import net.minecraft.world.entity.player.Player;

/**
 * The Halo shield lives in vanilla absorption; the HUD shows it as the shield bar (Hud), so the golden absorption
 * hearts (and the extra heart row they would reserve) are hidden.
 */
@Mixin(Gui.class)
public abstract class GuiShieldMixin {
	@Redirect(method = "renderPlayerHealth", at = @At(value = "INVOKE", target = "Lnet/minecraft/world/entity/player/Player;getAbsorptionAmount()F"))
	private float crafttheft$hideShieldHearts(Player player) {
		return 0f;
	}
}
