package dev.crafttheft.content;

import java.util.LinkedHashMap;
import java.util.Map;

import net.minecraft.core.Registry;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.sounds.SoundEvent;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.gen.Sheets;

/**
 * One SoundEvent per sources sound row: crafttheft:&lt;game short&gt;.&lt;file&gt; (docs/CONTRACTS.md "Sound events").
 * The jar's sounds.json maps each to the row's placeholder vanilla sound; a cache pack's sounds.json replaces it.
 */
public final class ModSounds {
	private static final Map<String, SoundEvent> BY_SOURCE = new LinkedHashMap<>();

	private ModSounds() {}

	public static void init() {
		for (Sheets.Source s : Sheets.SOURCES) {
			if (!s.kind().equals("sound")) continue;
			var id = CraftTheft.id(SourceRows.soundEvent(s.id()));
			BY_SOURCE.put(s.id(), Registry.register(BuiltInRegistries.SOUND_EVENT, id, SoundEvent.createVariableRangeEvent(id)));
		}
	}

	/** The event for a sources row id, e.g. "halo_snd_assault_rifle_fire". */
	public static SoundEvent of(String sourceId) {
		SoundEvent e = BY_SOURCE.get(sourceId);
		if (e == null) throw new IllegalArgumentException("no sound event for sources row " + sourceId);
		return e;
	}

	public static Map<String, SoundEvent> all() { return BY_SOURCE; }
}
