package dev.crafttheft.client.cache;

import static org.junit.jupiter.api.Assertions.*;

import java.util.Properties;

import org.junit.jupiter.api.Test;

/** config/crafttheft-guard.properties is a plain file players can edit: a bad value must not crash CLIENT_STARTED. */
class CacheGuardRetryTest {
	@Test
	void retryCountsAreParsedDefensively() {
		Properties p = new Properties();
		p.setProperty("halo", "2");
		p.setProperty("gta", "two");
		p.setProperty("fortnite", " 1 ");
		p.setProperty("x", "-4");
		assertEquals(2, CacheGuard.retryCount(p, "halo"));
		assertEquals(0, CacheGuard.retryCount(p, "gta"));
		assertEquals(1, CacheGuard.retryCount(p, "fortnite"));
		assertEquals(0, CacheGuard.retryCount(p, "x"));
		assertEquals(0, CacheGuard.retryCount(p, "missing"));
	}
}
