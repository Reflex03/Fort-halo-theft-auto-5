package dev.crafttheft;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Bookkeeping for sheets/hooks.json: every place the mod hooks into Minecraft calls {@link #mark} with the hooks
 * row id when it registers, so the test harness can compare what is registered with the sheet.
 */
public final class Hooks {
	private static final Map<String, String> REGISTERED = Collections.synchronizedMap(new LinkedHashMap<>());

	private Hooks() {}

	public static void mark(String hookId, String handler) {
		REGISTERED.put(hookId, handler);
	}

	public static Map<String, String> registered() {
		synchronized (REGISTERED) { return new LinkedHashMap<>(REGISTERED); }
	}
}
