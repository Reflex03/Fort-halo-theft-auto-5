package dev.crafttheft.client;

import org.lwjgl.glfw.GLFW;

import com.mojang.blaze3d.platform.InputConstants;

import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientLifecycleEvents;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.client.keybinding.v1.KeyBindingHelper;
import net.fabricmc.fabric.api.client.rendering.v1.BuiltinItemRendererRegistry;
import net.fabricmc.fabric.api.client.rendering.v1.EntityRendererRegistry;
import net.fabricmc.fabric.api.client.rendering.v1.HudRenderCallback;
import net.fabricmc.fabric.api.client.rendering.v1.LivingEntityFeatureRendererRegistrationCallback;
import net.fabricmc.fabric.api.client.screen.v1.ScreenEvents;
import net.fabricmc.fabric.api.event.client.player.ClientPreAttackCallback;
import net.fabricmc.fabric.api.resource.ResourceManagerHelper;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.client.renderer.blockentity.BlockEntityRenderers;
import net.minecraft.client.renderer.entity.player.PlayerRenderer;
import net.minecraft.server.packs.PackType;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.Hooks;
import dev.crafttheft.client.cache.CacheGuard;
import dev.crafttheft.client.cache.ExtractProgressScreen;
import dev.crafttheft.client.render.BusRenderer;
import dev.crafttheft.client.render.CarRenderer;
import dev.crafttheft.client.render.GliderLayer;
import dev.crafttheft.client.render.LootChestItemRenderer;
import dev.crafttheft.client.render.LootChestRenderer;
import dev.crafttheft.client.render.MeshCache;
import dev.crafttheft.client.render.ProjectileRenderer;
import dev.crafttheft.client.render.WeaponItemRenderer;
import dev.crafttheft.content.ModBlocks;
import dev.crafttheft.content.ModEntities;
import dev.crafttheft.content.ModItems;
import dev.crafttheft.weapon.HaloWeaponItem;

/** Client entrypoint. Fabric runs it before Minecraft reads options.txt, so FirstRun goes first. */
public class CraftTheftClient implements ClientModInitializer {
	@Override
	public void onInitializeClient() {
		FirstRun.writeOptions();

		ClientLifecycleEvents.CLIENT_STARTED.register(CacheGuard::onClientStarted);
		ScreenEvents.AFTER_INIT.register((mc, screen, w, h) -> {
			if (!(screen instanceof TitleScreen)) return;
			if (CacheGuard.mode == CacheGuard.Mode.BLOCKING && !CacheGuard.skipped) mc.execute(() -> mc.setScreen(new ExtractProgressScreen()));
			else CacheGuard.evaluateCacheReady(mc);
		});

		ResourceManagerHelper.get(PackType.CLIENT_RESOURCES).registerReloadListener(MeshCache.INSTANCE);
		Hooks.mark("mesh_reload", "MeshCache.reload");

		WeaponItemRenderer weapons = new WeaponItemRenderer();
		for (HaloWeaponItem w : ModItems.weapons().values()) BuiltinItemRendererRegistry.INSTANCE.register(w, weapons);
		BuiltinItemRendererRegistry.INSTANCE.register(ModItems.LOOT_CHEST, new LootChestItemRenderer());
		Hooks.mark("item_renderer", "WeaponItemRenderer.render");

		for (var type : ModEntities.cars().values()) EntityRendererRegistry.register(type, CarRenderer::new);
		EntityRendererRegistry.register(ModEntities.BUS, BusRenderer::new);
		EntityRendererRegistry.register(ModEntities.PROJECTILE, ProjectileRenderer::new);
		EntityRendererRegistry.register(ModEntities.ROCKET, ProjectileRenderer::new);
		EntityRendererRegistry.register(ModEntities.GRENADE, ProjectileRenderer::new);
		Hooks.mark("entity_renderers", "CarRenderer/BusRenderer/ProjectileRenderer");

		BlockEntityRenderers.register(ModBlocks.LOOT_CHEST_BE, LootChestRenderer::new);
		Hooks.mark("chest_renderer", "LootChestRenderer.render");

		LivingEntityFeatureRendererRegistrationCallback.EVENT.register((type, renderer, helper, ctx) -> {
			if (renderer instanceof PlayerRenderer pr) helper.register(new GliderLayer(pr));
		});
		Hooks.mark("glider_layer", "GliderLayer (LivingEntityFeatureRendererRegistrationCallback)");

		HudRenderCallback.EVENT.register(Hud::render);
		Hooks.mark("hud", "Hud.render");

		ClientInput.RELOAD = KeyBindingHelper.registerKeyBinding(new KeyMapping("key.crafttheft.reload", InputConstants.Type.KEYSYM, GLFW.GLFW_KEY_R, "category.crafttheft"));
		ClientInput.HORN = KeyBindingHelper.registerKeyBinding(new KeyMapping("key.crafttheft.horn", InputConstants.Type.KEYSYM, GLFW.GLFW_KEY_H, "category.crafttheft"));
		ClientPreAttackCallback.EVENT.register(ClientInput::onPreAttack);
		Hooks.mark("pre_attack", "ClientInput.onPreAttack");
		ClientTickEvents.END_CLIENT_TICK.register(ClientInput::tick);
		Hooks.mark("client_tick", "ClientInput.tick");
		// mixins (camera, fov_zoom, hide_shield_hearts, cache_pack) are applied by crafttheft.client.mixins.json
		Hooks.mark("camera", "CameraMixin");
		Hooks.mark("fov_zoom", "ZoomMixin");
		Hooks.mark("hide_shield_hearts", "GuiShieldMixin");
		CraftTheft.LOG.info("[crafttheft] client initialised");
	}
}
