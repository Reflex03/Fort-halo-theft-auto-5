using System.IO.Compression;
using CthExtract.Gta.Resources;

namespace CthExtract.Gta.Tests.Synth;

/// <summary>
/// Our own RSC7 resource builder: blocks are appended to a system or graphics segment (16-byte aligned) and addressed
/// as 0x50000000 / 0x60000000 + offset; Build() pads both segments to a page size expressible in RSC7 page flags,
/// deflates them and prepends the 16-byte header.
/// </summary>
public sealed class ResourceBuilder
{
    private readonly MemoryStream _sys = new(), _gfx = new();

    public sealed record Ref(bool Graphics, int Offset)
    {
        public ulong Ptr => (Graphics ? 0x60000000UL : 0x50000000UL) + (ulong)Offset;
    }

    /// <summary>Reserve a zeroed block; write into it later with <see cref="Put"/>.</summary>
    public Ref Alloc(int size, bool graphics = false, int align = 16)
    {
        var s = graphics ? _gfx : _sys;
        while (s.Length % align != 0) s.WriteByte(0);
        int off = (int)s.Length;
        s.Write(new byte[Math.Max(size, 1)]);
        return new Ref(graphics, off);
    }

    public Ref Add(byte[] data, bool graphics = false, int align = 16)
    {
        var r = Alloc(data.Length, graphics, align);
        Put(r, data);
        return r;
    }

    public void Put(Ref r, byte[] data, int at = 0)
    {
        var s = r.Graphics ? _gfx : _sys;
        long keep = s.Position;
        s.Position = r.Offset + at;
        s.Write(data);
        s.Position = keep;
        s.Seek(0, SeekOrigin.End);
    }

    public Ref String(string text) => Add(System.Text.Encoding.ASCII.GetBytes(text + "\0"));

    /// <summary>Smallest flags (one page-count field) covering size; version nibble in bits 28..31.</summary>
    public static uint FlagsFor(long size, int versionNibble)
    {
        uint v = (uint)(versionNibble & 0xF) << 28;
        if (size == 0) return v;
        for (int shift = 0; shift < 16; shift++)
        {
            long page = 0x2000L << shift;
            long count = (size + page - 1) / page;
            if (count <= 127) return v | ((uint)count << 17) | (uint)shift;
        }
        throw new ArgumentException("resource too large");
    }

    public byte[] Build(int version)
    {
        uint sysFlags = FlagsFor(_sys.Length, version >> 4), gfxFlags = FlagsFor(_gfx.Length, version & 0xF);
        long ss = Resource.SizeFromFlags(sysFlags), gs = Resource.SizeFromFlags(gfxFlags);
        var all = new byte[ss + gs];
        _sys.ToArray().CopyTo(all, 0);
        _gfx.ToArray().CopyTo(all, ss);
        using var o = new MemoryStream();
        o.Write(new Blk(16).U32(0, Resource.Magic).U32(4, (uint)version).U32(8, sysFlags).U32(12, gfxFlags).B);
        using (var ds = new DeflateStream(o, CompressionLevel.Optimal, true)) ds.Write(all);
        return o.ToArray();
    }
}
