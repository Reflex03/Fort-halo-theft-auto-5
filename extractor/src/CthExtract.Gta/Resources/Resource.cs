using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace CthExtract.Gta.Resources;

/// <summary>
/// An inflated RAGE resource (RSC7): system pages followed by graphics pages. Pointers are virtual addresses:
/// 0x5xxxxxxx = system segment offset, 0x6xxxxxxx = graphics segment offset. Every read is bounds-checked and throws
/// InvalidDataException, so a damaged or unexpected file fails one asset instead of the run.
/// Page-flag decoding follows gta-toolkit's ResourceFile_GTA5_pc (MIT).
/// </summary>
public sealed class Resource
{
    public const uint Magic = 0x37435352; // "RSC7"
    public const ulong SystemBase = 0x50000000, GraphicsBase = 0x60000000;

    public byte[] Data { get; }
    public long SystemSize { get; }
    public long GraphicsSize { get; }
    public int Version { get; }
    public uint SysFlags { get; }
    public uint GfxFlags { get; }
    public string What { get; }

    public Resource(byte[] data, long systemSize, long graphicsSize, int version, uint sysFlags, uint gfxFlags, string what)
    {
        Data = data; SystemSize = systemSize; GraphicsSize = graphicsSize; Version = version; SysFlags = sysFlags; GfxFlags = gfxFlags; What = what;
    }

    /// <summary>Bytes of a segment described by RSC7 page flags (base page 0x2000 shifted, then 9 page-count fields).</summary>
    public static long SizeFromFlags(uint flags)
    {
        long baseSize = 0x2000L << (int)(flags & 0xF);
        long div16 = (flags >> 27) & 1, div8 = (flags >> 26) & 1, div4 = (flags >> 25) & 1, div2 = (flags >> 24) & 1;
        long mul1 = (flags >> 17) & 0x7F, mul2 = (flags >> 11) & 0x3F, mul4 = (flags >> 7) & 0xF, mul8 = (flags >> 5) & 0x3, mul16 = (flags >> 4) & 1;
        return baseSize * div16 / 16 + baseSize * div8 / 8 + baseSize * div4 / 4 + baseSize * div2 / 2
             + baseSize * (mul1 + mul2 * 2 + mul4 * 4 + mul8 * 8 + mul16 * 16);
    }

    public static int VersionFromFlags(uint sys, uint gfx) => (int)((((sys >> 28) & 0xF) << 4) | ((gfx >> 28) & 0xF));

    /// <summary>Parse a raw resource as stored in an RPF: a 16-byte header, then raw deflate. The header is normally
    /// RSC7 {magic, version, sysFlags, gfxFlags}; for resources of 16 MiB and more the archive overwrites it with the
    /// packed size, so the RPF entry's flags are used whenever the magic is absent.</summary>
    public static Resource FromRaw(byte[] raw, string what, uint entrySys = 0, uint entryGfx = 0)
    {
        if (raw.Length < 16) throw new InvalidDataException($"{what}: resource too short");
        uint sys = entrySys, gfx = entryGfx;
        const int start = 16;
        if (BinaryPrimitives.ReadUInt32LittleEndian(raw) == Magic)
        {
            sys = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8));
            gfx = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
        }
        else if (entrySys == 0 && entryGfx == 0)
            throw new InvalidDataException($"{what}: not an RSC7 resource and no entry flags");
        long ss = SizeFromFlags(sys), gs = SizeFromFlags(gfx);
        if (ss + gs > 1L << 30) throw new InvalidDataException($"{what}: implausible resource size {ss}+{gs}");
        var data = new byte[ss + gs];
        try
        {
            using var ds = new DeflateStream(new MemoryStream(raw, start, raw.Length - start), CompressionMode.Decompress);
            int done = 0;
            while (done < data.Length)
            {
                int n = ds.Read(data, done, data.Length - done);
                if (n <= 0) break;
                done += n;
            }
            if (done < ss) throw new InvalidDataException($"{what}: resource inflated to {done} bytes, system pages need {ss}");
        }
        catch (InvalidDataException e) when (!e.Message.StartsWith(what))
        {
            throw new InvalidDataException($"{what}: resource inflate failed ({e.Message})");
        }
        return new Resource(data, ss, gs, VersionFromFlags(sys, gfx), sys, gfx, what);
    }

    /// <summary>File offset in <see cref="Data"/> for a pointer, or -1 for null.</summary>
    public long Off(ulong ptr)
    {
        if (ptr == 0) return -1;
        ulong seg = ptr & 0xF0000000UL, o = ptr & 0x0FFFFFFFUL;
        long r = seg switch
        {
            SystemBase when (long)o < SystemSize => (long)o,
            GraphicsBase when (long)o < GraphicsSize => SystemSize + (long)o,
            _ => throw new InvalidDataException($"{What}: bad pointer 0x{ptr:X}"),
        };
        return r;
    }

    private void Check(long o, int n)
    {
        if (o < 0 || o + n > Data.Length) throw new InvalidDataException($"{What}: read 0x{o:X}+{n} outside resource (0x{Data.Length:X})");
    }

    public byte U8(long o) { Check(o, 1); return Data[o]; }
    public ushort U16(long o) { Check(o, 2); return BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan((int)o)); }
    public short S16(long o) { Check(o, 2); return BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan((int)o)); }
    public uint U32(long o) { Check(o, 4); return BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)o)); }
    public ulong U64(long o) { Check(o, 8); return BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)o)); }
    public float F32(long o) { Check(o, 4); return BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan((int)o)); }
    public ReadOnlySpan<byte> Bytes(long o, int n) { Check(o, n); return Data.AsSpan((int)o, n); }

    /// <summary>Follow the pointer stored at offset o.</summary>
    public long Ptr(long o) => Off(U64(o));

    public string? CString(ulong ptr, int max = 512)
    {
        if (ptr == 0) return null;
        long o = Off(ptr);
        int end = (int)o;
        while (end < Data.Length && Data[end] != 0 && end - o < max) end++;
        return Encoding.ASCII.GetString(Data, (int)o, end - (int)o);
    }

    /// <summary>A {u64 ptr; u16 count; u16 capacity} list header at o: (array offset, count).</summary>
    public (long array, int count) List(long o)
    {
        ulong p = U64(o);
        int n = U16(o + 8);
        return p == 0 || n == 0 ? (-1, 0) : (Off(p), n);
    }
}
