package dev.crafttheft.content;

import com.mojang.serialization.Codec;

import net.fabricmc.fabric.api.attachment.v1.AttachmentRegistry;
import net.fabricmc.fabric.api.attachment.v1.AttachmentSyncPredicate;
import net.fabricmc.fabric.api.attachment.v1.AttachmentType;
import net.minecraft.network.codec.ByteBufCodecs;

import dev.crafttheft.CraftTheft;

/** Player state synced with Fabric's data attachments (sync is backported to Fabric API 0.116.x for 1.21.1). */
public final class ModAttachments {
	/** GTA wanted level 0..5, synced to the player's own client (HUD). */
	public static AttachmentType<Integer> WANTED;
	/** Skydive state (Skydive.NONE/SKYDIVE/GLIDE), synced to everyone (others see the glider). */
	public static AttachmentType<Integer> GLIDE;

	private ModAttachments() {}

	public static void init() {
		WANTED = AttachmentRegistry.create(CraftTheft.id("wanted"),
				b -> b.initializer(() -> 0).persistent(Codec.INT).syncWith(ByteBufCodecs.VAR_INT, AttachmentSyncPredicate.targetOnly()));
		GLIDE = AttachmentRegistry.create(CraftTheft.id("glide"),
				b -> b.initializer(() -> 0).persistent(Codec.INT).syncWith(ByteBufCodecs.VAR_INT, AttachmentSyncPredicate.all()));
	}
}
