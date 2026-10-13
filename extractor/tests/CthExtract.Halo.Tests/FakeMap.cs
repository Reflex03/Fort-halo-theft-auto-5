using System.Buffers.Binary;
using Reclaimer.Geometry;

namespace CthExtract.Halo.Tests;

/// <summary>
/// An in-memory "cache file" for tests: tag metadata is written at the Assembly Halo3MCC offsets into one byte array,
/// block pointers are stored compressed (address &gt;&gt; 2, like MCC's PointerExpander) and tag references carry a
/// salted datum, so the module's raw readers run the same code paths as on a real map.
/// </summary>
public sealed class FakeMap : IHaloMap
{
    private byte[] mem = new byte[1 << 16];
    private long next = 0x1000;
    private readonly List<HaloTag> tags = new();
    private readonly Dictionary<int, long> addr = new();
    private readonly Dictionary<int, string> strings = new();
    private readonly Dictionary<string, int> stringIds = new();
    private readonly Dictionary<string, HaloTag> globals = new();
    private readonly Dictionary<int, Scene> scenes = new();

    public string Path { get; }
    public string Description => $"fake map {System.IO.Path.GetFileName(Path)} ({tags.Count} tags)";
    public IReadOnlyList<HaloTag> Tags => tags;
    public int Reads { get; private set; }

    public FakeMap(string path = "riverworld.map") => Path = path;

    // ---------------------------------------------------------------- building

    public long Alloc(int size)
    {
        long a = next;
        next = (next + size + 15) & ~15L;
        while (next > mem.Length) Array.Resize(ref mem, mem.Length * 2);
        return a;
    }

    public HaloTag AddTag(string cls, string name, int size, bool global = false)
    {
        var t = new HaloTag(tags.Count, cls, name);
        tags.Add(t);
        addr[t.Index] = Alloc(size);
        if (global) globals[cls] = t;
        return t;
    }

    public long At(HaloTag t) => addr[t.Index];

    /// <summary>Gen3 datum: salt in the high 16 bits, tag index in the low 16 bits.</summary>
    public static int Datum(HaloTag t) => unchecked((int)((((0xE174u + (uint)t.Index) & 0xFFFF) << 16) | (uint)t.Index));

    public void I32(long a, int v) => BinaryPrimitives.WriteInt32LittleEndian(mem.AsSpan((int)a), v);
    public void U32(long a, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan((int)a), v);
    public void I16(long a, short v) => BinaryPrimitives.WriteInt16LittleEndian(mem.AsSpan((int)a), v);
    public void U8(long a, byte v) => mem[a] = v;
    public void F32(long a, float v) => BinaryPrimitives.WriteSingleLittleEndian(mem.AsSpan((int)a), v);
    public void Range(long a, float min, float max) { F32(a, min); F32(a + 4, max); }

    public void TagRef(long a, HaloTag? t)
    {
        I32(a, t is null ? -1 : FourCc(t.Class));
        I32(a + HaloLayout.TagRefDatum, t is null ? -1 : Datum(t));
    }

    /// <summary>Write a block header pointing at <paramref name="count"/> new elements; returns element 0's address.</summary>
    public long Block(long a, int count, int elementSize)
    {
        long e = Alloc(count * elementSize);
        I32(a, count);
        I32(a + 4, (int)(e >> 2));
        return e;
    }

    public int StringIdOf(string s)
    {
        if (!stringIds.TryGetValue(s, out var id))
        {
            id = 0x1000 + stringIds.Count;
            stringIds[s] = id;
            strings[id] = s;
        }
        return id;
    }

    public void StringId(long a, string s) => I32(a, StringIdOf(s));

    public void SetScene(HaloTag mode, Scene scene) => scenes[mode.Index] = scene;

    private static int FourCc(string c) => BinaryPrimitives.ReadInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(c.PadRight(4)));

    // ---------------------------------------------------------------- IHaloMap

    public HaloTag? Global(string tagClass) => globals.GetValueOrDefault(tagClass);
    public HaloTag? Tag(int index) => index >= 0 && index < tags.Count ? tags[index] : null;
    public long TagAddress(HaloTag tag) => addr[tag.Index];

    public byte[] Read(long address, int count)
    {
        Reads++;
        if (address <= 0 || address + count > mem.Length) throw new InvalidDataException($"read outside fake map at 0x{address:x}");
        return mem.AsSpan((int)address, count).ToArray();
    }

    public long Pointer(int compressed) => (long)compressed << 2;
    public string? StringId(int id) => strings.GetValueOrDefault(id);
    public Scene? Geometry(HaloTag mode) => scenes.GetValueOrDefault(mode.Index);
    public void Dispose() { }
}
