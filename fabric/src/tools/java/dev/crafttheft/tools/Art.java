package dev.crafttheft.tools;

import java.awt.image.BufferedImage;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;

import javax.imageio.ImageIO;

/** Tiny pixel-art canvas for the jar's original placeholder textures and item sprites. */
public final class Art {
	final BufferedImage img;
	final int w, h;

	public Art(int w, int h) {
		this.w = w; this.h = h;
		this.img = new BufferedImage(w, h, BufferedImage.TYPE_INT_ARGB);
	}

	public static int rgb(int r, int g, int b) { return 0xFF000000 | (r << 16) | (g << 8) | b; }

	public static int shade(int argb, float k) {
		int r = Math.min(255, (int) (((argb >> 16) & 255) * k));
		int g = Math.min(255, (int) (((argb >> 8) & 255) * k));
		int b = Math.min(255, (int) ((argb & 255) * k));
		return (argb & 0xFF000000) | (r << 16) | (g << 8) | b;
	}

	public Art px(int x, int y, int c) {
		if (x >= 0 && y >= 0 && x < w && y < h) img.setRGB(x, y, c);
		return this;
	}

	public Art fill(int x0, int y0, int x1, int y1, int c) {
		for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) px(x, y, c);
		return this;
	}

	public Art all(int c) { return fill(0, 0, w - 1, h - 1, c); }

	public Art border(int c) {
		for (int x = 0; x < w; x++) { px(x, 0, c); px(x, h - 1, c); }
		for (int y = 0; y < h; y++) { px(0, y, c); px(w - 1, y, c); }
		return this;
	}

	public Art disc(float cx, float cy, float r, int c) {
		for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
			float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
			if (dx * dx + dy * dy <= r * r) px(x, y, c);
		}
		return this;
	}

	/** Diagonal stripes of colour c every `period` pixels (marks placeholder art at a glance). */
	public Art stripes(int period, int c) {
		for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if ((x + y) % period == 0) px(x, y, c);
		return this;
	}

	public Art outline(int c) {
		BufferedImage copy = new BufferedImage(w, h, BufferedImage.TYPE_INT_ARGB);
		copy.setData(img.getData());
		for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
			if ((copy.getRGB(x, y) >>> 24) != 0) continue;
			boolean edge = false;
			for (int[] d : new int[][]{{1, 0}, {-1, 0}, {0, 1}, {0, -1}}) {
				int nx = x + d[0], ny = y + d[1];
				if (nx >= 0 && ny >= 0 && nx < w && ny < h && (copy.getRGB(nx, ny) >>> 24) != 0) edge = true;
			}
			if (edge) px(x, y, c);
		}
		return this;
	}

	/** Five-pointed star centred in the canvas (point-in-polygon fill). */
	public Art star(float cx, float cy, float rOuter, float rInner, int c) {
		float[] xs = new float[10], ys = new float[10];
		for (int i = 0; i < 10; i++) {
			double a = -Math.PI / 2 + i * Math.PI / 5;
			float r = i % 2 == 0 ? rOuter : rInner;
			xs[i] = cx + (float) Math.cos(a) * r;
			ys[i] = cy + (float) Math.sin(a) * r;
		}
		for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
			float px = x + 0.5f, py = y + 0.5f;
			boolean in = false;
			for (int i = 0, j = 9; i < 10; j = i++)
				if ((ys[i] > py) != (ys[j] > py) && px < (xs[j] - xs[i]) * (py - ys[i]) / (ys[j] - ys[i]) + xs[i]) in = !in;
			if (in) px(x, y, c);
		}
		return this;
	}

	public void write(Path file) throws IOException {
		Files.createDirectories(file.getParent());
		ImageIO.write(img, "png", file.toFile());
	}
}
