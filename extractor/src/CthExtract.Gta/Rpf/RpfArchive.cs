using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using CthExtract.Gta.Crypto;
using Microsoft.Win32.SafeHandles;

namespace CthExtract.Gta.Rpf;

/// <summary>A game file opened read-only with FileShare.ReadWrite|Delete; positional, thread-safe reads.</summary>
public sealed class RpfFile : IDisposable
{
    private readonly SafeFileHandle _h;
    public string Path { get; }
    public long Length { get; }

    public RpfFile(string path)
    {
        Path = path;
        _h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Length = RandomAccess.GetLength(_h);
    }

    public byte[] Read(long offset, long length)
    {
        if (offset < 0 || length < 0 || offset + length > Length)
            throw new InvalidDataException($"read 0x{offset:X}+0x{length:X} outside {System.IO.Path.GetFileName(Path)} (0x{Length:X})");
        var buf = new byte[length];
        int done = 0;
        while (done < buf.Length)
        {
            int n = RandomAccess.Read(_h, buf.AsSpan(done), offset + done);
            if (n <= 0) throw new EndOfStreamException($"short read in {Path}");
            done += n;
        }
        return buf;
    }

    public void Dispose() => _h.Dispose();
}

public enum RpfEntryKind { Directory, Binary, Resource }

public sealed class RpfEntry
{
    public required RpfArchive Archive { get; init; }
    public required string Name { get; init; }
    public RpfEntryKind Kind { get; init; }
    /// <summary>Path inside the archive, lower case, '/'-separated ("levels/gta5/vehicles.rpf").</summary>
    public string Path { get; set; } = "";
    public uint Offset512 { get; init; }
    /// <summary>Binary: stored (compressed) size, 0 = stored uncompressed. Resource: full size including the RSC7 header.</summary>
    public uint StoredSize { get; set; }
    public uint UncompressedSize { get; init; }
    public bool Encrypted { get; init; }
    public uint SysFlags { get; init; }
    public uint GfxFlags { get; init; }
    internal uint EntriesIndex, EntriesCount;

    /// <summary>Absolute offset of the data in the file on disk.</summary>
    public long DataOffset => Archive.Start + (long)Offset512 * 512;
    public long DataLength => Kind == RpfEntryKind.Resource ? StoredSize : StoredSize == 0 ? UncompressedSize : StoredSize;
    public int ResourceVersion => (int)((((SysFlags >> 28) & 0xF) << 4) | ((GfxFlags >> 28) & 0xF));
    public string FullPath => Archive.VirtualPath + "/" + Path;
    public bool IsRpf => Kind == RpfEntryKind.Binary && Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => FullPath;
}

/// <summary>
/// RPF7 archive reader (our own code, written from gta-toolkit's RageArchive7 layout (MIT) and public format notes). The 16-byte
/// header is plain; entries and names are encrypted together with the archive's scheme (OPEN / AES / NG).
/// Nested archives are binary entries whose data is a complete RPF7; their NG key uses their own name and size.
/// </summary>
public sealed class RpfArchive
{
    public const uint Magic = 0x52504637; // "7FPR" little-endian: 'R','P','F','7'
    public const uint EncOpen = 0x4E45504F, EncAes = 0x0FFFFFF9, EncNg = 0x0FEFFFFF;

    public RpfFile File { get; }
    public string Name { get; }
    /// <summary>"x64e.rpf", "x64e.rpf/levels/gta5/vehicles.rpf", "update/update.rpf" (relative to the game folder).</summary>
    public string VirtualPath { get; }
    public long Start { get; }
    public long Size { get; }
    public uint Encryption { get; private set; }
    public List<RpfEntry> Entries { get; } = new();
    private readonly GtaKeys _keys;

    public string EncryptionName => Encryption switch
    {
        0 or EncOpen => "OPEN",
        EncAes => "AES",
        EncNg => "NG",
        _ => $"0x{Encryption:X8}",
    };

    private RpfArchive(RpfFile file, long start, long size, string name, string virtualPath, GtaKeys keys)
    {
        File = file; Start = start; Size = size; Name = name; VirtualPath = virtualPath; _keys = keys;
    }

    /// <summary>Read only the plain 16-byte header (no keys needed): returns the encryption field, or null if not RPF7.</summary>
    public static (uint entries, uint namesLength, uint encryption)? PeekHeader(RpfFile file, long start = 0)
    {
        if (start + 16 > file.Length) return null;
        var h = file.Read(start, 16);
        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != Magic) return null;
        return (BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12)));
    }

    public static RpfArchive Open(RpfFile file, string virtualPath, GtaKeys keys) =>
        Open(file, 0, file.Length, System.IO.Path.GetFileName(virtualPath), virtualPath, keys);

    public static RpfArchive Open(RpfFile file, long start, long size, string name, string virtualPath, GtaKeys keys)
    {
        var a = new RpfArchive(file, start, size, name, virtualPath, keys);
        a.ReadToc();
        return a;
    }

    public RpfArchive OpenNested(RpfEntry e)
    {
        if (!e.IsRpf || e.Archive != this) throw new ArgumentException($"{e} is not a nested archive of {VirtualPath}");
        long off = e.DataOffset - Start, size = e.DataLength;
        if (size < 16 || off < 0 || off + size > Size)
            throw new InvalidDataException($"{e.FullPath}: nested archive 0x{off:X}+0x{size:X} lies outside {VirtualPath} (0x{Size:X})");
        return Open(File, e.DataOffset, size, e.Name, VirtualPath + "/" + e.Path, _keys);
    }

    private void ReadToc()
    {
        var hdr = File.Read(Start, 16);
        if (BinaryPrimitives.ReadUInt32LittleEndian(hdr) != Magic)
            throw new InvalidDataException($"{VirtualPath}: not an RPF7 archive");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
        uint namesLen = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8));
        Encryption = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(12));
        if (count == 0 || count > 4_000_000 || namesLen > 64 * 1024 * 1024 || 16 + 16L * count + namesLen > Size)
            throw new InvalidDataException($"{VirtualPath}: implausible RPF7 header (entries {count}, names {namesLen}, size {Size})");

        var ent = File.Read(Start + 16, 16L * count);
        var names = File.Read(Start + 16 + 16L * count, namesLen);
        switch (Encryption)
        {
            case 0:
            case EncOpen:
                break;
            case EncAes:
            {
                var key = _keys.RequireAes(VirtualPath);
                AesCrypto.DecryptInPlace(ent, key);
                AesCrypto.DecryptInPlace(names, key);
                break;
            }
            default: // NG (0x0FEFFFFF); any other value is treated as NG too, as gta-toolkit does
            {
                var ng = _keys.RequireNg(VirtualPath);
                var key = ng.Keys[NgCrypto.KeyIndex(Name, (uint)Size, ng.Lut)];
                NgCrypto.DecryptInPlace(ent, key, ng.Tables);
                NgCrypto.DecryptInPlace(names, key, ng.Tables);
                break;
            }
        }

        for (int i = 0; i < count; i++)
        {
            var e = ent.AsSpan(i * 16, 16);
            uint w1 = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(4));
            RpfEntry x;
            if (w1 == 0x7FFFFF00)
            {
                x = new RpfEntry
                {
                    Archive = this, Kind = RpfEntryKind.Directory,
                    Name = NameAt(names, BinaryPrimitives.ReadUInt32LittleEndian(e)),
                    EntriesIndex = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(8)),
                    EntriesCount = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(12)),
                };
            }
            else if ((w1 & 0x80000000) == 0)
            {
                ulong q = BinaryPrimitives.ReadUInt64LittleEndian(e);
                uint enc = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(12));
                x = new RpfEntry
                {
                    Archive = this, Kind = RpfEntryKind.Binary,
                    Name = NameAt(names, (uint)(q & 0xFFFF)),
                    StoredSize = (uint)((q >> 16) & 0xFFFFFF),
                    Offset512 = (uint)((q >> 40) & 0xFFFFFF),
                    UncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(8)),
                    Encrypted = enc == 1,
                };
            }
            else
            {
                x = new RpfEntry
                {
                    Archive = this, Kind = RpfEntryKind.Resource,
                    Name = NameAt(names, BinaryPrimitives.ReadUInt16LittleEndian(e)),
                    StoredSize = (uint)(e[2] | (e[3] << 8) | (e[4] << 16)),
                    Offset512 = (uint)(e[5] | (e[6] << 8) | (e[7] << 16)) & 0x7FFFFF,
                    SysFlags = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(8)),
                    GfxFlags = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(12)),
                };
            }
            Entries.Add(x);
        }

        // sanity: entry 0 is the root directory; a wrong key gives garbage here
        if (Entries[0].Kind != RpfEntryKind.Directory || Entries[0].EntriesIndex + Entries[0].EntriesCount > count)
            throw new InvalidDataException($"{VirtualPath}: table of contents does not decode ({EncryptionName}); wrong key or damaged archive");
        AssignPaths();

        // resources of 16 MiB and more store 0xFFFFFF; the real size is packed into their RSC7 header
        foreach (var x in Entries)
        {
            if (x.Kind != RpfEntryKind.Resource || x.StoredSize != 0xFFFFFF) continue;
            var b = File.Read(x.DataOffset, 16);
            x.StoredSize = (uint)(b[7] | (b[14] << 8) | (b[5] << 16) | (b[2] << 24));
        }
    }

    private void AssignPaths()
    {
        var visited = new bool[Entries.Count];
        var stack = new Stack<(int index, string path)>();
        stack.Push((0, ""));
        visited[0] = true;
        while (stack.Count > 0)
        {
            var (di, path) = stack.Pop();
            var d = Entries[di];
            d.Path = path;
            for (long i = d.EntriesIndex; i < d.EntriesIndex + d.EntriesCount && i < Entries.Count; i++)
            {
                if (visited[i]) continue; // malformed tree: never loop
                visited[i] = true;
                var c = Entries[(int)i];
                c.Path = (path.Length == 0 ? "" : path + "/") + c.Name.ToLowerInvariant();
                if (c.Kind == RpfEntryKind.Directory) stack.Push(((int)i, c.Path));
            }
        }
    }

    private static string NameAt(byte[] names, uint off)
    {
        if (off >= names.Length) return $"#bad_name_{off}";
        int end = Array.IndexOf(names, (byte)0, (int)off);
        if (end < 0) end = names.Length;
        return Encoding.ASCII.GetString(names, (int)off, end - (int)off);
    }

    public IEnumerable<RpfEntry> Files => Entries.Where(e => e.Kind != RpfEntryKind.Directory && e.Path.Length > 0);

    /// <summary>Largest entry this reader loads into memory (raw or inflated). Car fragments, texture dictionaries and
    /// vehicles.awc are far below it; a size field above it is damaged data, not a reason to allocate gigabytes.</summary>
    public const long MaxEntryBytes = 768L * 1024 * 1024;

    /// <summary>The entry's stored bytes must lie inside this archive (a nested archive is a window of its parent file).</summary>
    private void CheckRange(RpfEntry e)
    {
        long off = e.DataOffset - Start, len = e.DataLength;
        if (len < 0 || len > MaxEntryBytes || off < 0 || off + len > Size)
            throw new InvalidDataException($"{e.FullPath}: entry data 0x{off:X}+0x{len:X} lies outside its archive (0x{Size:X}) or is implausibly large");
    }

    /// <summary>Bytes of a binary entry: decrypted (AES/NG by this archive's scheme) and inflated when compressed.</summary>
    public byte[] ReadBinary(RpfEntry e)
    {
        if (e.Kind != RpfEntryKind.Binary) throw new ArgumentException($"{e} is not a binary entry");
        CheckRange(e);
        var data = File.Read(e.DataOffset, e.DataLength);
        if (e.Encrypted)
        {
            if (Encryption == EncAes) AesCrypto.DecryptInPlace(data, _keys.RequireAes(e.FullPath));
            else if (Encryption != 0 && Encryption != EncOpen)
            {
                var ng = _keys.RequireNg(e.FullPath);
                NgCrypto.DecryptInPlace(data, ng.Keys[NgCrypto.KeyIndex(e.Name, e.UncompressedSize, ng.Lut)], ng.Tables);
            }
        }
        if (e.StoredSize == 0) return data;
        return Inflate(data, e.UncompressedSize, e.FullPath);
    }

    /// <summary>The raw resource (RSC7 header + deflated pages) of a resource entry. Resources are not encrypted.</summary>
    public byte[] ReadResourceRaw(RpfEntry e)
    {
        if (e.Kind != RpfEntryKind.Resource) throw new ArgumentException($"{e} is not a resource entry");
        CheckRange(e);
        return File.Read(e.DataOffset, e.DataLength);
    }

    /// <summary>Raw deflate with the output bounded: never more than the entry's recorded size (plus one byte to notice a
    /// lie) and never more than <see cref="MaxEntryBytes"/>, so a damaged size field or a deflate bomb cannot exhaust memory.
    /// The initial buffer is capped too: the recorded size is a hint, not a promise.</summary>
    public static byte[] Inflate(byte[] raw, long expected, string what)
    {
        long limit = expected > 0 ? Math.Min(expected, MaxEntryBytes) : MaxEntryBytes;
        try
        {
            using var ds = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var ms = new MemoryStream((int)Math.Min(limit, 64L * 1024 * 1024));
            var buf = new byte[81920];
            int n;
            while ((n = ds.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > limit)
                    throw new InvalidDataException($"{what}: inflates beyond {(expected > 0 ? $"its recorded size {expected}" : $"{MaxEntryBytes} bytes")}; damaged data");
            }
            return ms.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{what}: deflate failed ({ex.Message}); wrong key or damaged data");
        }
    }
}
