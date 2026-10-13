package dev.crafttheft.client.cache;

import com.google.gson.JsonObject;

import net.minecraft.client.gui.GuiGraphics;
import net.minecraft.client.gui.components.Button;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.network.chat.Component;
import net.minecraft.util.FormattedCharSequence;

import dev.crafttheft.CraftTheft;

/**
 * "Reading your games..." while the extractor runs on first start: shows progress.json (game, step, done/total)
 * written by the extractor. The player can choose to play with placeholders; the run then continues in the
 * background and its content appears when it finishes.
 */
public class ExtractProgressScreen extends Screen {
	private JsonObject progress;
	private int ticks;
	public static int shown;

	public ExtractProgressScreen() {
		super(Component.translatable("crafttheft.extract.title"));
	}

	@Override
	protected void init() {
		shown++;
		addRenderableWidget(Button.builder(Component.translatable("crafttheft.extract.skip"), b -> {
			CacheGuard.skipped = true;
			if (CacheGuard.mode == CacheGuard.Mode.BLOCKING) CacheGuard.mode = CacheGuard.Mode.BACKGROUND;
			CraftTheft.LOG.info("[cache-guard] player chose to play with placeholders; extraction continues in the background");
			minecraft.setScreen(new TitleScreen());
		}).bounds(width / 2 - 100, height - 40, 200, 20).build());
	}

	@Override
	public void tick() {
		if (ticks++ % 5 == 0) {
			JsonObject p = CacheGuard.readProgress();
			if (p != null) progress = p;
		}
	}

	@Override public boolean shouldCloseOnEsc() { return false; }

	/** A numeric field of progress.json, else 0 (getAsInt on a string or a fraction would throw inside render). */
	static int intOf(JsonObject o, String key) {
		var e = o.get(key);
		if (e == null || !e.isJsonPrimitive() || !e.getAsJsonPrimitive().isNumber()) return 0;
		double d = e.getAsDouble();
		return Double.isFinite(d) ? (int) Math.max(0, Math.min(Integer.MAX_VALUE, d)) : 0;
	}

	@Override
	public void render(GuiGraphics g, int mouseX, int mouseY, float delta) {
		super.render(g, mouseX, mouseY, delta);
		int cx = width / 2, y = height / 4;
		g.drawCenteredString(font, title, cx, y, 0xFFFFFF);
		y += 16;
		for (FormattedCharSequence line : font.split(Component.translatable("crafttheft.extract.subtitle"), Math.min(360, width - 40))) {
			g.drawCenteredString(font, line, cx, y, 0xA0A0A0);
			y += 10;
		}
		y += 14;
		int done = 0, total = 0;
		String game = null, step = null;
		if (progress != null) {
			done = intOf(progress, "done");
			total = intOf(progress, "total");
			game = progress.has("game") && progress.get("game").isJsonPrimitive() ? progress.get("game").getAsString() : null;
			step = progress.has("step") && progress.get("step").isJsonPrimitive() ? progress.get("step").getAsString() : null;
		}
		if (CacheGuard.blockingFinished) {
			g.drawCenteredString(font, Component.translatable("crafttheft.extract.done"), cx, y, 0x80FF80);
		} else if (progress == null) {
			g.drawCenteredString(font, Component.translatable("crafttheft.extract.waiting"), cx, y, 0xFFFFFF);
		} else {
			if (game != null) g.drawCenteredString(font, Component.translatable("crafttheft.extract.game", game), cx, y, 0xFFE070);
			y += 12;
			if (step != null) g.drawCenteredString(font, font.plainSubstrByWidth(step, width - 40), cx, y, 0xC0C0C0);
		}
		y += 20;
		int bw = Math.min(300, width - 40), bx = cx - bw / 2;
		float frac = total > 0 ? Math.min(1f, done / (float) total) : 0f;
		g.fill(bx - 1, y - 1, bx + bw + 1, y + 9, 0xFF000000);
		g.fill(bx, y, bx + bw, y + 8, 0xFF303030);
		g.fill(bx, y, bx + (int) (bw * frac), y + 8, 0xFF40B8FF);
		y += 14;
		if (total > 0) g.drawCenteredString(font, Component.translatable("crafttheft.extract.count", done, total), cx, y, 0xFFFFFF);
		y += 12;
		long s = (System.currentTimeMillis() - CacheGuard.startedAtMs) / 1000;
		g.drawCenteredString(font, String.format("%d:%02d", s / 60, s % 60), cx, y, 0x808080);
	}
}
