using System.Buffers.Binary;
using Reclaimer.Geometry;

namespace CthExtract.Halo;

/// <summary>One tag of an opened cache file.</summary>
public sealed record HaloTag(int Index, string Class, string Name)
{
    public override string ToString() => $"[{Class}] {Name}";

    /// <summary>Last path segment of the tag name ("objects\weapons\rifle\ar\assault_rifle" -> "assault_rifle").</summary>
    public string Leaf => Name.Substring(Name.LastIndexOf('\\') + 1);
}

/// <summary>
/// What the Halo module needs from an opened map. The real implementation wraps Reclaimer (ReclaimerMap); tests use an
/// in-memory implementation with synthetic tag data written at the Assembly offsets.
/// Addresses are whatever <see cref="TagAddress"/> and <see cref="Pointer"/> return; the module never invents them.
/// </summary>
public interface IHaloMap : IDisposable
{
    string Path { get; }
    string Description { get; }
    IReadOnlyList<HaloTag> Tags { get; }
    HaloTag? Global(string tagClass);
    HaloTag? Tag(int index);
    long TagAddress(HaloTag tag);
    byte[] Read(long address, int count);
    long Pointer(int compressed);
    string? StringId(int id);

    /// <summary>The render geometry of a 'mode' tag (with materials and textures), or null if it cannot be read.</summary>
    Scene? Geometry(HaloTag mode);
}

/// <summary>A tag block read from raw data: element count and the address of element 0.</summary>
public readonly record struct TagBlock(int Count, long Address)
{
    public long Element(int index, int elementSize) => Address + (long)index * elementSize;
    public bool IsEmpty => Count <= 0 || Address <= 0;
}

/// <summary>Typed raw-field reads over an <see cref="IHaloMap"/>. Every method validates what it reads.</summary>
public sealed class TagFields
{
    /// <summary>Largest block element count we believe (Halo 3 blocks stay far below this).</summary>
    public const int MaxBlockCount = 1 << 16;

    public IHaloMap Map { get; }
    public TagFields(IHaloMap map) => Map = map;

    public float F32(long address) => BinaryPrimitives.ReadSingleLittleEndian(Map.Read(address, 4));
    public int I32(long address) => BinaryPrimitives.ReadInt32LittleEndian(Map.Read(address, 4));
    public uint U32(long address) => BinaryPrimitives.ReadUInt32LittleEndian(Map.Read(address, 4));
    public short I16(long address) => BinaryPrimitives.ReadInt16LittleEndian(Map.Read(address, 2));
    public sbyte I8(long address) => unchecked((sbyte)Map.Read(address, 1)[0]);
    public byte U8(long address) => Map.Read(address, 1)[0];

    /// <summary>(min, max) of a rangef / ranged field.</summary>
    public (float Min, float Max) Range(long address) => (F32(address), F32(address + 4));

    /// <summary>(min, max) of a range16 field.</summary>
    public (short Min, short Max) Range16(long address) => (I16(address), I16(address + 2));

    /// <summary>A tag block {count, compressed pointer}. Implausible counts read as empty.</summary>
    public TagBlock Block(long address)
    {
        int count = I32(address);
        int pointer = I32(address + 4);
        if (count <= 0 || count > MaxBlockCount || pointer == 0) return new TagBlock(0, 0);
        return new TagBlock(count, Map.Pointer(pointer));
    }

    /// <summary>The tag a tag reference points at, or null for a null / dangling reference.</summary>
    public HaloTag? TagRef(long address, string? expectedClass = null)
    {
        int datum = I32(address + HaloLayout.TagRefDatum);
        if (datum == -1 || datum == 0) return null;
        var tag = Map.Tag(datum & 0xFFFF);
        if (tag is null) return null;
        if (expectedClass != null && !string.Equals(tag.Class, expectedClass, StringComparison.Ordinal)) return null;
        return tag;
    }

    public string? StringId(long address)
    {
        int id = I32(address);
        return id == 0 ? null : Map.StringId(id);
    }

    public long Address(HaloTag tag) => Map.TagAddress(tag);
}
