package dev.crafttheft.mixin;

import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;

import net.minecraft.world.entity.LivingEntity;

/** LivingEntity.jumping is protected; on the server it mirrors a rider's jump key (ServerboundPlayerInputPacket). */
@Mixin(LivingEntity.class)
public interface LivingEntityAccessor {
	@Accessor("jumping")
	boolean crafttheft$isJumping();
}
