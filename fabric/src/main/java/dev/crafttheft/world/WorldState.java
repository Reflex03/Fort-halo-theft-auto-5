package dev.crafttheft.world;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.UUID;

import net.minecraft.core.HolderLookup;
import net.minecraft.nbt.CompoundTag;
import net.minecraft.nbt.ListTag;
import net.minecraft.nbt.NbtUtils;
import net.minecraft.nbt.Tag;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.saveddata.SavedData;

/** Per-world mod state (data/crafttheft_world.dat): who has dropped from the bus, planned chests and parked cars. */
public final class WorldState extends SavedData {
	/** A chest or car planned at world creation, materialised when its chunk is loaded. */
	public static final class Plan {
		public final int x, z;
		public final String kind; // chest pool or vehicle id
		public final float yaw;
		public boolean done;
		public int y = Integer.MIN_VALUE;
		/** Times this plan was moved because its spot was water or blocked. */
		public int moves;

		public Plan(int x, int z, String kind, float yaw) { this.x = x; this.z = z; this.kind = kind; this.yaw = yaw; }

		public Plan movedTo(int nx, int nz) { Plan p = new Plan(nx, nz, kind, yaw); p.moves = moves + 1; return p; }
	}

	public boolean planned;
	public final List<Plan> chests = new ArrayList<>();
	public final List<Plan> cars = new ArrayList<>();
	private final Set<UUID> dropped = new HashSet<>();

	public static final SavedData.Factory<WorldState> FACTORY = new SavedData.Factory<>(WorldState::new, WorldState::load, null);

	public static WorldState get(ServerLevel level) {
		return level.getServer().overworld().getDataStorage().computeIfAbsent(FACTORY, "crafttheft_world");
	}

	public boolean hasDropped(UUID id) { return dropped.contains(id); }

	public void markDropped(UUID id) { if (dropped.add(id)) setDirty(); }

	public void forgetDropped(UUID id) { if (dropped.remove(id)) setDirty(); }

	static WorldState load(CompoundTag tag, HolderLookup.Provider reg) {
		WorldState s = new WorldState();
		s.planned = tag.getBoolean("Planned");
		readPlans(tag.getList("Chests", Tag.TAG_COMPOUND), s.chests);
		readPlans(tag.getList("Cars", Tag.TAG_COMPOUND), s.cars);
		for (Tag t : tag.getList("Dropped", Tag.TAG_INT_ARRAY)) s.dropped.add(NbtUtils.loadUUID(t));
		return s;
	}

	private static void readPlans(ListTag l, List<Plan> out) {
		for (int i = 0; i < l.size(); i++) {
			CompoundTag c = l.getCompound(i);
			Plan p = new Plan(c.getInt("X"), c.getInt("Z"), c.getString("Kind"), c.getFloat("Yaw"));
			p.done = c.getBoolean("Done");
			p.moves = c.getInt("Moves");
			if (c.contains("Y")) p.y = c.getInt("Y");
			out.add(p);
		}
	}

	private static ListTag writePlans(List<Plan> plans) {
		ListTag l = new ListTag();
		for (Plan p : plans) {
			CompoundTag c = new CompoundTag();
			c.putInt("X", p.x); c.putInt("Z", p.z); c.putString("Kind", p.kind); c.putFloat("Yaw", p.yaw); c.putBoolean("Done", p.done); c.putInt("Moves", p.moves);
			if (p.y != Integer.MIN_VALUE) c.putInt("Y", p.y);
			l.add(c);
		}
		return l;
	}

	@Override
	public CompoundTag save(CompoundTag tag, HolderLookup.Provider reg) {
		tag.putBoolean("Planned", planned);
		tag.put("Chests", writePlans(chests));
		tag.put("Cars", writePlans(cars));
		ListTag d = new ListTag();
		for (UUID u : dropped) d.add(NbtUtils.createUUID(u));
		tag.put("Dropped", d);
		return tag;
	}
}
