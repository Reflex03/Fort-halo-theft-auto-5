package dev.crafttheft.client;

import com.mojang.blaze3d.systems.RenderSystem;

import net.minecraft.client.DeltaTracker;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.GuiGraphics;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.network.chat.Component;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.world.item.ItemStack;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.bus.BattleBusEntity;
import dev.crafttheft.bus.Skydive;
import dev.crafttheft.content.ModAttachments;
import dev.crafttheft.content.SourceRows;
import dev.crafttheft.crime.WantedSystem;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.heal.FortniteHealItem;
import dev.crafttheft.weapon.HaloWeaponItem;

/**
 * hooks row hud: GTA wanted stars (the gta wanted_star texture, top right at systems.hud.starsX/Y), the Halo shield
 * bar (top centre, from synced absorption), ammo (bottom right), heal progress, the bus "press jump" prompt and the
 * skydive/glide state, plus a scope overlay while zoomed.
 */
public final class Hud {
	public static final ResourceLocation STAR = CraftTheft.id("textures/" + SourceRows.texturePath("gta_tex_wanted_star") + ".png");
	public static final int STAR_SIZE = 12;
	public static int frames;

	private Hud() {}

	public static void render(GuiGraphics g, DeltaTracker delta) {
		Minecraft mc = Minecraft.getInstance();
		LocalPlayer p = mc.player;
		if (p == null || mc.options.hideGui) return;
		frames++;
		int w = g.guiWidth(), h = g.guiHeight();
		if (ClientInput.zoomed()) scope(g, w, h);
		shield(g, p, w);
		stars(g, p, w);
		ammo(g, mc, p, w, h);
		heal(g, mc, p, w, h);
		int glide = p.getAttachedOrElse(ModAttachments.GLIDE, Skydive.NONE);
		if (p.getVehicle() instanceof BattleBusEntity) {
			Component msg = Component.translatable("crafttheft.hud.jump_to_drop", mc.options.keyJump.getTranslatedKeyMessage());
			g.drawCenteredString(mc.font, msg, w / 2, h / 2 + 30, 0xFFFFE070);
		} else if (glide != Skydive.NONE) {
			g.drawCenteredString(mc.font, Component.translatable(glide == Skydive.SKYDIVE ? "crafttheft.hud.skydiving" : "crafttheft.hud.gliding"), w / 2, 22, 0xFFFFE070);
		}
	}

	private static void shield(GuiGraphics g, LocalPlayer p, int w) {
		float max = p.getMaxAbsorption();
		if (max <= 0) return;
		float frac = Math.min(1f, p.getAbsorptionAmount() / max);
		int bw = 122, bh = 7, x = (w - bw) / 2, y = 6;
		g.fill(x - 2, y - 2, x + bw + 2, y + bh + 2, 0xA0000000);
		g.fill(x - 1, y - 1, x + bw + 1, y + bh + 1, 0xFF6FC8FF);
		g.fill(x, y, x + bw, y + bh, 0xFF0B2234);
		int fill = Math.round(bw * frac);
		boolean low = frac < 0.25f;
		int col = low ? ((System.currentTimeMillis() / 250) % 2 == 0 ? 0xFFFF3030 : 0xFFB02020) : 0xFF3FB6FF;
		g.fill(x, y, x + fill, y + bh, col);
		g.fill(x, y, x + fill, y + 2, low ? 0xFFFF8080 : 0xFF9EE0FF);
		for (int i = 1; i < 10; i++) g.fill(x + i * bw / 10, y, x + i * bw / 10 + 1, y + bh, 0x50000000);
	}

	private static void stars(GuiGraphics g, LocalPlayer p, int w) {
		int stars = p.getAttachedOrElse(ModAttachments.WANTED, 0);
		if (stars <= 0) return;
		int n = WantedSystem.MAX_STARS, gap = 2;
		int right = w + Sheets.HUD_STARS_X; // right-aligned, px from the right edge
		int y = Sheets.HUD_STARS_Y;
		RenderSystem.enableBlend();
		for (int i = 0; i < n; i++) {
			int x = right - (n - i) * (STAR_SIZE + gap);
			boolean on = i < stars;
			// GTA blinks the stars while the police are searching; we keep them steady and dim the empty ones
			RenderSystem.setShaderColor(1f, 1f, 1f, on ? 1f : 0.3f);
			g.blit(STAR, x, y, STAR_SIZE, STAR_SIZE, 0f, 0f, 1, 1, 1, 1);
		}
		RenderSystem.setShaderColor(1f, 1f, 1f, 1f);
		RenderSystem.disableBlend();
	}

	private static void ammo(GuiGraphics g, Minecraft mc, LocalPlayer p, int w, int h) {
		ItemStack st = p.getMainHandItem();
		if (!(st.getItem() instanceof HaloWeaponItem wpn)) return;
		String s;
		if (wpn.isGrenade()) s = "x" + st.getCount();
		else if (HaloWeaponItem.reloading(st) > 0) s = Component.translatable("crafttheft.hud.reloading").getString();
		else s = HaloWeaponItem.ammo(st) + " / " + HaloWeaponItem.reserve(st);
		String name = st.getHoverName().getString();
		g.drawString(mc.font, s, w - 8 - mc.font.width(s), h - 22, HaloWeaponItem.ammo(st) == 0 && !wpn.isGrenade() ? 0xFFFF5050 : 0xFFFFFFFF, true);
		g.drawString(mc.font, name, w - 8 - mc.font.width(name), h - 34, 0xFFA0D8FF, true);
	}

	private static void heal(GuiGraphics g, Minecraft mc, LocalPlayer p, int w, int h) {
		if (!p.isUsingItem() || !(p.getUseItem().getItem() instanceof FortniteHealItem heal)) return;
		int total = heal.row.useTicks();
		float frac = Math.min(1f, (total - p.getUseItemRemainingTicks()) / (float) Math.max(1, total));
		int bw = 100, x = (w - bw) / 2, y = h / 2 + 18;
		g.fill(x - 1, y - 1, x + bw + 1, y + 6, 0xA0000000);
		g.fill(x, y, x + Math.round(bw * frac), y + 5, heal.row.shield() > 0 ? 0xFF3FB6FF : 0xFF50E060);
		g.drawCenteredString(mc.font, Component.translatable("crafttheft.hud.using", heal.getDescription()), w / 2, y + 9, 0xFFFFFFFF);
	}

	private static void scope(GuiGraphics g, int w, int h) {
		int r = Math.min(w, h) / 2 - 10, cx = w / 2, cy = h / 2;
		g.fill(0, 0, w, cy - r, 0xE0000000);
		g.fill(0, cy + r, w, h, 0xE0000000);
		g.fill(0, cy - r, cx - r, cy + r, 0xE0000000);
		g.fill(cx + r, cy - r, w, cy + r, 0xE0000000);
		g.fill(cx - r, cy, cx + r, cy + 1, 0xA0000000);
		g.fill(cx, cy - r, cx + 1, cy + r, 0xA0000000);
	}
}
