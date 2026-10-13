package dev.crafttheft.world;

import static org.junit.jupiter.api.Assertions.*;

import java.util.UUID;

import org.junit.jupiter.api.Test;

import net.minecraft.nbt.CompoundTag;

/** Per-world state survives save/load: the bus drop list, the chest/car plans and which of them are already placed. */
class WorldStateSaveTest {
	@Test
	void roundTripKeepsPlansAndDrops() {
		WorldState s = new WorldState();
		s.planned = true;
		WorldState.Plan chest = new WorldState.Plan(10, -20, "chest", 90f);
		chest.done = true;
		chest.y = 64;
		s.chests.add(chest);
		WorldState.Plan moved = new WorldState.Plan(5, 6, "chest", 0f).movedTo(7, 8);
		s.chests.add(moved);
		WorldState.Plan car = new WorldState.Plan(-3, 4, "sultan", 123.5f);
		s.cars.add(car);
		UUID a = UUID.randomUUID(), b = UUID.randomUUID();
		s.markDropped(a);

		CompoundTag tag = s.save(new CompoundTag(), null);
		WorldState r = WorldState.load(tag, null);

		assertTrue(r.planned, "a reloaded world must not plan its chests/cars a second time");
		assertEquals(2, r.chests.size());
		assertTrue(r.chests.get(0).done, "a placed chest stays placed (never placed twice)");
		assertEquals(64, r.chests.get(0).y);
		assertEquals(10, r.chests.get(0).x);
		assertEquals(-20, r.chests.get(0).z);
		assertEquals(90f, r.chests.get(0).yaw);
		assertFalse(r.chests.get(1).done);
		assertEquals(1, r.chests.get(1).moves);
		assertEquals(7, r.chests.get(1).x);
		assertEquals(Integer.MIN_VALUE, r.chests.get(1).y, "an unplaced plan has no y");
		assertEquals(1, r.cars.size());
		assertEquals("sultan", r.cars.get(0).kind);
		assertEquals(123.5f, r.cars.get(0).yaw);
		assertTrue(r.hasDropped(a), "a player who dropped from the bus does not board again after a reload");
		assertFalse(r.hasDropped(b));
	}

	@Test
	void emptyTagIsAFreshWorld() {
		WorldState r = WorldState.load(new CompoundTag(), null);
		assertFalse(r.planned);
		assertTrue(r.chests.isEmpty());
		assertTrue(r.cars.isEmpty());
	}
}
