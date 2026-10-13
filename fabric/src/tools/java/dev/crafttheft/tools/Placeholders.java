package dev.crafttheft.tools;

import static dev.crafttheft.tools.Art.rgb;

import java.io.IOException;
import java.nio.file.Path;

import dev.crafttheft.gen.Sheets;

/**
 * Original placeholder meshes and art shipped in the jar at the sources sheet's out paths. Shown whenever the
 * player's cache has nothing better (missing game, failed extraction). Simple shapes drawn here, following the
 * .cthm conventions in docs/CONTRACTS.md (metres = blocks, +Y up, front +Z; vehicles/bus centred in X/Z with the
 * lowest point at y=0; weapons with the grip at the origin and the barrel towards +Z; glider origin at the back).
 */
final class Placeholders {
	private Placeholders() {}

	static void write(Path assets) throws IOException {
		textures(assets);
		modIcon().write(assets.resolve("icon.png"));
		for (Sheets.Source s : Sheets.SOURCES) {
			if (s.kind().equals("mesh")) {
				if (s.placeholder().startsWith("vanilla")) continue; // drawn with the vanilla model in code (chest)
				MeshWriter m = mesh(s);
				m.write(assets.resolve(s.out()));
				System.out.println("[gen] placeholder mesh " + s.out() + ": " + m.triangles() + " tris");
			} else if (s.kind().equals("icon")) {
				icon(s).write(assets.resolve(s.out()));
			}
		}
	}

	private static MeshWriter mesh(Sheets.Source s) {
		for (Sheets.Weapon w : Sheets.WEAPONS) if (w.model().equals(s.id())) return gun(w);
		for (Sheets.Vehicle v : Sheets.VEHICLES) if (v.model().equals(s.id())) return car(v);
		if (s.out().endsWith("battle_bus.cthm")) return bus();
		if (s.out().endsWith("glider.cthm")) return glider();
		// unknown mesh row: a 1-block box so the game still shows something
		return new MeshWriter().box("placeholder/generic", -1, -0.5f, 0, -0.5f, 0.5f, 1, 0.5f);
	}

	// ------------------------------------------------------------------ weapons (grip at origin, barrel +Z)
	static MeshWriter gun(Sheets.Weapon w) {
		MeshWriter m = new MeshWriter();
		String metal = "placeholder/gun_metal", accent = "placeholder/gun_accent";
		switch (w.kind()) {
			case "grenade" -> {
				String mat = w.id().contains("plasma") ? "placeholder/plasma" : metal;
				m.sphere(mat, -1, 0, 0.0f, 0, 0.075f, 0.09f, 0.075f, 8, 12);
				m.box(accent, -1, -0.02f, 0.08f, -0.02f, 0.02f, 0.11f, 0.02f);
				return m;
			}
			case "rocket" -> {
				m.box(metal, -1, -0.09f, 0.02f, -0.55f, 0.09f, 0.20f, 0.75f);   // twin tube body
				m.box(accent, -1, -0.10f, 0.20f, -0.20f, 0.10f, 0.24f, 0.30f); // sight rail
				m.box(metal, -1, -0.025f, -0.14f, -0.03f, 0.025f, 0.02f, 0.03f); // grip
				return m;
			}
			default -> { }
		}
		float len = switch (w.id()) {
			case "magnum" -> 0.30f;
			case "needler" -> 0.42f;
			case "plasma_rifle" -> 0.55f;
			case "assault_rifle" -> 0.75f;
			case "battle_rifle" -> 0.85f;
			case "shotgun" -> 0.95f;
			case "sniper_rifle" -> 1.30f;
			default -> 0.7f;
		};
		boolean pistol = len < 0.5f;
		String body = w.id().contains("plasma") ? "placeholder/plasma" : w.id().equals("needler") ? "placeholder/needler" : metal;
		float back = pistol ? -0.06f : -len * 0.35f, front = back + len * (pistol ? 0.75f : 0.62f);
		m.box(body, -1, -0.035f, 0.02f, back, 0.035f, 0.12f, front);                       // receiver
		m.box(metal, -1, -0.015f, 0.06f, front, 0.015f, 0.09f, back + len);               // barrel
		m.box(metal, -1, -0.025f, -0.12f, -0.03f, 0.025f, 0.02f, 0.03f);                  // grip (at the origin)
		if (!pistol) {
			m.box(accent, -1, -0.022f, -0.10f, 0.06f, 0.022f, 0.02f, 0.13f);               // magazine
			m.box(metal, -1, -0.03f, -0.02f, back - len * 0.18f, 0.03f, 0.09f, back);        // stock
		}
		if (w.zoom() < 1f) m.box(accent, -1, -0.02f, 0.12f, -0.05f, 0.02f, 0.16f, 0.15f);  // scope
		if (w.id().equals("needler"))
			for (int i = 0; i < 4; i++) m.box("placeholder/plasma", -1, -0.008f, 0.12f, 0.02f + i * 0.06f, 0.008f, 0.19f, 0.035f + i * 0.06f);
		return m;
	}

	// ------------------------------------------------------------------ cars (centred, lowest point y=0, front +Z)
	static MeshWriter car(Sheets.Vehicle v) {
		MeshWriter m = new MeshWriter();
		float w = v.width() * 0.92f, h = v.height(), len = v.width() * 2.25f, r = 0.32f;
		String body = "placeholder/car_" + v.id();
		// left = +X when facing +Z (CONTRACTS.md / GTA module mapping)
		int lf = m.node("wheel_lf", w / 2 - 0.12f, r, len / 2 - 0.62f);
		int rf = m.node("wheel_rf", -(w / 2 - 0.12f), r, len / 2 - 0.62f);
		int lb = m.node("wheel_lb", w / 2 - 0.12f, r, -(len / 2 - 0.62f));
		int rb = m.node("wheel_rb", -(w / 2 - 0.12f), r, -(len / 2 - 0.62f));
		m.box(body, -1, -w / 2, 0.22f, -len / 2, w / 2, h * 0.55f, len / 2);                         // lower body
		m.box(body, -1, -w / 2 + 0.12f, h * 0.55f, -len / 2 + 0.75f, w / 2 - 0.12f, h, len / 2 - 1.05f); // cabin
		m.box("placeholder/glass", -1, -w / 2 + 0.16f, h * 0.6f, len / 2 - 1.06f, w / 2 - 0.16f, h * 0.95f, len / 2 - 1.04f); // windscreen
		m.box("placeholder/lights", -1, -w / 2 + 0.1f, h * 0.38f, len / 2, -w / 2 + 0.45f, h * 0.48f, len / 2 + 0.02f);
		m.box("placeholder/lights", -1, w / 2 - 0.45f, h * 0.38f, len / 2, w / 2 - 0.1f, h * 0.48f, len / 2 + 0.02f);
		if (v.role().equals("police")) {
			m.box("placeholder/siren_red", -1, 0.02f, h, -0.2f, w / 2 - 0.25f, h + 0.12f, 0.1f);
			m.box("placeholder/siren_blue", -1, -w / 2 + 0.25f, h, -0.2f, -0.02f, h + 0.12f, 0.1f);
		}
		for (int[] n : new int[][]{{lf, 1}, {rf, -1}, {lb, 1}, {rb, -1}}) {
			var node = n[0] == lf ? new float[]{w / 2 - 0.12f, len / 2 - 0.62f} : n[0] == rf ? new float[]{-(w / 2 - 0.12f), len / 2 - 0.62f}
					: n[0] == lb ? new float[]{w / 2 - 0.12f, -(len / 2 - 0.62f)} : new float[]{-(w / 2 - 0.12f), -(len / 2 - 0.62f)};
			m.wheel("placeholder/tyre", n[0], node[0], r, node[1], r, 0.12f, 12);
		}
		return m;
	}

	// ------------------------------------------------------------------ battle bus with its balloon
	static MeshWriter bus() {
		MeshWriter m = new MeshWriter();
		float w = 2.6f, h = 3.0f, len = 8.5f;
		m.box("placeholder/bus_body", -1, -w / 2, 0.45f, -len / 2, w / 2, h, len / 2);
		m.box("placeholder/bus_window", -1, -w / 2 - 0.01f, 1.7f, -len / 2 + 0.6f, w / 2 + 0.01f, 2.5f, len / 2 - 0.6f);
		m.box("placeholder/bus_window", -1, -w / 2 + 0.2f, 1.7f, len / 2, w / 2 - 0.2f, 2.6f, len / 2 + 0.01f);
		for (float z : new float[]{len / 2 - 1.3f, -len / 2 + 1.3f})
			for (float x : new float[]{w / 2 - 0.15f, -w / 2 + 0.15f})
				m.wheel("placeholder/tyre", -1, x, 0.45f, z, 0.45f, 0.15f, 12);
		m.sphere("placeholder/balloon", -1, 0, h + 4.8f, 0, 3.4f, 3.2f, 3.4f, 10, 16);
		for (float x : new float[]{-1.0f, 1.0f})
			for (float z : new float[]{-2.5f, 2.5f})
				m.box("placeholder/gun_metal", -1, x - 0.04f, h, z - 0.04f, x + 0.04f, h + 2.0f, z + 0.04f);
		return m;
	}

	// ------------------------------------------------------------------ glider (origin = attach point on the back, span along X)
	static MeshWriter glider() {
		MeshWriter m = new MeshWriter();
		m.box("placeholder/glider", -1, -1.7f, 1.35f, -0.55f, 1.7f, 1.45f, 0.55f);
		m.box("placeholder/glider", -1, -1.9f, 1.25f, -0.45f, -1.7f, 1.4f, 0.45f);
		m.box("placeholder/glider", -1, 1.7f, 1.25f, -0.45f, 1.9f, 1.4f, 0.45f);
		m.box("placeholder/gun_metal", -1, -0.55f, 0.0f, -0.03f, -0.49f, 1.35f, 0.03f);
		m.box("placeholder/gun_metal", -1, 0.49f, 0.0f, -0.03f, 0.55f, 1.35f, 0.03f);
		m.box("placeholder/gun_metal", -1, -0.55f, 0.0f, -0.03f, 0.55f, 0.06f, 0.03f);
		return m;
	}

	// ------------------------------------------------------------------ textures
	private static void tex(Path assets, String name, int base, int stripe) throws IOException {
		new Art(32, 32).all(base).stripes(8, stripe).border(Art.shade(base, 0.6f)).write(assets.resolve("textures/placeholder/" + name + ".png"));
	}

	private static void textures(Path assets) throws IOException {
		tex(assets, "generic", rgb(200, 0, 200), rgb(40, 40, 40));
		tex(assets, "gun_metal", rgb(70, 74, 80), rgb(95, 100, 108));
		tex(assets, "gun_accent", rgb(220, 140, 30), rgb(250, 190, 80));
		tex(assets, "plasma", rgb(70, 60, 200), rgb(120, 200, 255));
		tex(assets, "needler", rgb(150, 40, 150), rgb(255, 120, 220));
		tex(assets, "glass", rgb(40, 60, 80), rgb(70, 100, 130));
		tex(assets, "lights", rgb(255, 245, 180), rgb(255, 255, 230));
		tex(assets, "siren_red", rgb(230, 20, 20), rgb(255, 120, 120));
		tex(assets, "siren_blue", rgb(20, 60, 240), rgb(120, 160, 255));
		tex(assets, "tyre", rgb(25, 25, 25), rgb(60, 60, 60));
		tex(assets, "bus_body", rgb(40, 110, 230), rgb(70, 140, 250));
		tex(assets, "bus_window", rgb(30, 40, 60), rgb(60, 80, 110));
		tex(assets, "glider", rgb(250, 200, 30), rgb(230, 40, 40));
		new Art(32, 32).all(rgb(230, 230, 235)).fill(0, 0, 31, 7, rgb(230, 60, 160)).fill(0, 16, 31, 23, rgb(230, 60, 160)).border(rgb(120, 120, 130))
				.write(assets.resolve("textures/placeholder/balloon.png"));
		int[][] carColours = {{230, 120, 20}, {40, 90, 200}, {60, 170, 80}, {30, 30, 30}};
		int i = 0;
		for (Sheets.Vehicle v : Sheets.VEHICLES) {
			int[] c = v.role().equals("police") ? new int[]{235, 235, 235} : carColours[i++ % carColours.length];
			Art a = new Art(32, 32).all(rgb(c[0], c[1], c[2])).stripes(8, Art.shade(rgb(c[0], c[1], c[2]), 1.25f)).border(rgb(20, 20, 20));
			if (v.role().equals("police")) a.fill(0, 20, 31, 31, rgb(20, 20, 30));
			a.write(assets.resolve("textures/placeholder/car_" + v.id() + ".png"));
		}
	}

	/** The mod's own icon (original art): a GTA-style star over a sky-blue disc. */
	private static Art modIcon() {
		Art a = new Art(64, 64);
		a.disc(32, 32, 31, rgb(40, 110, 230)).disc(32, 32, 27, rgb(90, 170, 250));
		a.star(32, 34, 24, 10, rgb(255, 210, 40)).outline(rgb(25, 25, 35));
		return a;
	}

	// ------------------------------------------------------------------ icons (16x16 sprites)
	private static Art icon(Sheets.Source s) {
		Art a = new Art(16, 16);
		String o = s.out();
		int outline = rgb(25, 25, 35);
		if (o.contains("wanted_star")) {
			return a.star(8, 8.4f, 7.6f, 3.2f, rgb(255, 255, 255)).outline(outline);
		}
		int blue = rgb(60, 150, 255), light = rgb(170, 220, 255), red = rgb(220, 40, 40), white = rgb(240, 240, 240);
		if (o.contains("bandages")) {
			a.fill(3, 4, 12, 11, white).fill(3, 7, 12, 8, rgb(210, 200, 180)).fill(6, 5, 9, 10, red).fill(4, 6, 11, 9, red).fill(6, 6, 9, 9, red);
			a.fill(12, 5, 13, 10, rgb(220, 210, 190));
		} else if (o.contains("med_kit")) {
			a.fill(2, 4, 13, 13, white).fill(6, 2, 9, 4, rgb(150, 150, 150)).fill(7, 6, 8, 11, red).fill(5, 8, 10, 9, red);
		} else if (o.contains("small_shield")) {
			a.fill(7, 3, 8, 5, rgb(200, 200, 210)).disc(8, 10, 4.2f, blue).disc(7, 9, 1.5f, light);
		} else if (o.contains("shield_potion")) {
			a.fill(6, 1, 9, 4, rgb(200, 200, 210)).fill(4, 5, 11, 14, blue).fill(5, 6, 6, 12, light);
		} else if (o.contains("chug_jug")) {
			a.fill(5, 1, 10, 3, rgb(200, 180, 120)).disc(8, 9.5f, 5.8f, rgb(40, 120, 230)).disc(6.5f, 8, 2, light).fill(12, 6, 14, 11, rgb(40, 120, 230));
		} else {
			a.fill(3, 3, 12, 12, rgb(200, 0, 200));
		}
		return a.outline(outline);
	}
}
