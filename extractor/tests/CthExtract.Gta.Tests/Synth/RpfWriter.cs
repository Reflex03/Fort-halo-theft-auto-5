using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using CthExtract.Gta.Crypto;
using CthExtract.Gta.Rpf;

namespace CthExtract.Gta.Tests.Synth;

public enum RpfEnc { Open, Aes, Ng }

/// <summary>
/// Our own RPF7 writer for synthetic archives (layout per gta-toolkit's RageArchive7, MIT): 16-byte header, 16-byte
/// entries (directory children contiguous), name table, data at 512-byte boundaries. Entries and names are encrypted
/// with the archive's scheme; binary entries can be deflated and/or encrypted; resources are stored as given.
/// </summary>
public sealed class RpfWriter
{
    public sealed class Node
    {
        public required string Name;
        public bool IsDir;
        public List<Node> Children = new();
        public byte[] Data = Array.Empty<byte>();
        public bool IsResource;
        public bool Compress;
        public bool Encrypt;
        public uint SysFlags, GfxFlags;
        public bool HugeSizeMarker;
        public RpfWriter? Nested;
    }

    public RpfEnc Enc { get; }
    public Node Root { get; } = new() { Name = "", IsDir = true };
    private readonly SynthKeys? _keys;

    public RpfWriter(RpfEnc enc, SynthKeys? keys) { Enc = enc; _keys = keys; }

    private Node Dir(string path)
    {
        var cur = Root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = cur.Children.FirstOrDefault(c => c.IsDir && c.Name == part);
            if (next is null) { next = new Node { Name = part, IsDir = true }; cur.Children.Add(next); }
            cur = next;
        }
        return cur;
    }

    private static (string dir, string name) Split(string path)
    {
        int i = path.LastIndexOf('/');
        return i < 0 ? ("", path) : (path[..i], path[(i + 1)..]);
    }

    public RpfWriter AddBinary(string path, byte[] data, bool compress = false, bool encrypt = false)
    {
        var (d, n) = Split(path);
        Dir(d).Children.Add(new Node { Name = n, Data = data, Compress = compress, Encrypt = encrypt });
        return this;
    }

    public RpfWriter AddResource(string path, byte[] rsc7, bool hugeSizeMarker = false)
    {
        var (d, n) = Split(path);
        uint sys = BinaryPrimitives.ReadUInt32LittleEndian(rsc7.AsSpan(8)), gfx = BinaryPrimitives.ReadUInt32LittleEndian(rsc7.AsSpan(12));
        Dir(d).Children.Add(new Node { Name = n, Data = rsc7, IsResource = true, SysFlags = sys, GfxFlags = gfx, HugeSizeMarker = hugeSizeMarker });
        return this;
    }

    public RpfWriter AddNested(string path, RpfWriter nested)
    {
        var (d, n) = Split(path);
        Dir(d).Children.Add(new Node { Name = n, Nested = nested });
        return this;
    }

    public byte[] Build(string archiveName)
    {
        // entries: BFS so that each directory's children are contiguous
        var order = new List<Node> { Root };
        var dirInfo = new Dictionary<Node, (int index, int count)>();
        var queue = new Queue<Node>();
        queue.Enqueue(Root);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            var kids = d.Children.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            dirInfo[d] = (order.Count, kids.Count);
            order.AddRange(kids);
            foreach (var k in kids.Where(k => k.IsDir)) queue.Enqueue(k);
        }
        // names
        var names = new MemoryStream();
        var nameOff = new Dictionary<string, int>();
        foreach (var n in order)
        {
            if (nameOff.ContainsKey(n.Name)) continue;
            nameOff[n.Name] = (int)names.Length;
            names.Write(Encoding.ASCII.GetBytes(n.Name));
            names.WriteByte(0);
        }
        while (names.Length % 16 != 0) names.WriteByte(0);
        var nameBytes = names.ToArray();

        // payloads
        var payload = new Dictionary<Node, byte[]>();
        foreach (var n in order.Where(n => !n.IsDir))
        {
            if (n.Nested is not null) payload[n] = n.Nested.Build(n.Name);
            else if (n.IsResource)
            {
                var d = (byte[])n.Data.Clone();
                if (n.HugeSizeMarker)
                {
                    uint size = (uint)d.Length;
                    Array.Clear(d, 0, 16);
                    d[7] = (byte)size; d[14] = (byte)(size >> 8); d[5] = (byte)(size >> 16); d[2] = (byte)(size >> 24);
                }
                payload[n] = d;
            }
            else
            {
                var d = n.Data;
                if (n.Compress)
                {
                    using var z = new MemoryStream();
                    using (var ds = new DeflateStream(z, CompressionLevel.Optimal, true)) ds.Write(d);
                    d = z.ToArray();
                }
                else d = (byte[])d.Clone();
                if (n.Encrypt)
                {
                    if (Enc == RpfEnc.Aes) AesCrypto.EncryptInPlace(d, _keys!.Aes);
                    else if (Enc == RpfEnc.Ng) _keys!.NgEncryptInPlace(d, _keys.NgKeyFor(n.Name, (uint)n.Data.Length));
                }
                payload[n] = d;
            }
        }

        long headerLen = 16 + 16L * order.Count + nameBytes.Length;
        long pos = (headerLen + 511) / 512 * 512;
        var offsets = new Dictionary<Node, long>();
        foreach (var n in order.Where(n => !n.IsDir))
        {
            offsets[n] = pos;
            pos += (payload[n].Length + 511) / 512 * 512;
            if (payload[n].Length == 0) pos += 512;
        }
        var file = new byte[pos];

        var ent = new byte[16 * order.Count];
        for (int i = 0; i < order.Count; i++)
        {
            var n = order[i];
            var e = new Blk(16);
            if (n.IsDir)
            {
                var (idx, cnt) = dirInfo[n];
                e.U32(0, (uint)nameOff[n.Name]).U32(4, 0x7FFFFF00).U32(8, (uint)idx).U32(12, (uint)cnt);
            }
            else if (n.IsResource)
            {
                uint size = n.HugeSizeMarker ? 0xFFFFFFu : (uint)payload[n].Length;
                uint off512 = (uint)(offsets[n] / 512);
                e.U16(0, nameOff[n.Name]);
                e.U8(2, (int)(size & 0xFF)).U8(3, (int)((size >> 8) & 0xFF)).U8(4, (int)((size >> 16) & 0xFF));
                e.U8(5, (int)(off512 & 0xFF)).U8(6, (int)((off512 >> 8) & 0xFF)).U8(7, (int)(((off512 >> 16) & 0x7F) | 0x80));
                e.U32(8, n.SysFlags).U32(12, n.GfxFlags);
            }
            else
            {
                bool nested = n.Nested is not null;
                uint stored = nested || !n.Compress ? 0u : (uint)payload[n].Length;
                uint uncompressed = nested ? (uint)payload[n].Length : (uint)n.Data.Length;
                ulong q = (ulong)(uint)nameOff[n.Name] | ((ulong)stored << 16) | ((ulong)(offsets[n] / 512) << 40);
                e.U64(0, q).U32(8, uncompressed).U32(12, n.Encrypt ? 1u : 0u);
            }
            e.B.CopyTo(ent, i * 16);
        }
        switch (Enc)
        {
            case RpfEnc.Aes:
                AesCrypto.EncryptInPlace(ent, _keys!.Aes);
                AesCrypto.EncryptInPlace(nameBytes, _keys.Aes);
                break;
            case RpfEnc.Ng:
                var key = _keys!.NgKeyFor(archiveName, (uint)file.Length);
                _keys.NgEncryptInPlace(ent, key);
                _keys.NgEncryptInPlace(nameBytes, key);
                break;
        }
        uint encWord = Enc switch { RpfEnc.Open => RpfArchive.EncOpen, RpfEnc.Aes => RpfArchive.EncAes, _ => RpfArchive.EncNg };
        new Blk(16).U32(0, RpfArchive.Magic).U32(4, (uint)order.Count).U32(8, (uint)nameBytes.Length).U32(12, encWord).B.CopyTo(file, 0);
        ent.CopyTo(file, 16);
        nameBytes.CopyTo(file, 16 + ent.Length);
        foreach (var (n, off) in offsets) payload[n].CopyTo(file, off);
        return file;
    }
}
