using System.Text;

namespace CthExtract.Gta.Tests.Synth;

/// <summary>A minimal PE32+ file with .text, .rdata and .data sections filled with random bytes; a key can be planted
/// at a chosen file offset. Only the headers our section parser reads are meaningful.</summary>
public static class FakeExe
{
    public sealed record Layout(byte[] Bytes, long TextOffset, long RdataOffset, long DataOffset, int SectionSize);

    public static Layout Build(int seed, int sectionSize = 1 << 20, int tailSize = 1 << 19)
    {
        var r = new Random(seed);
        const int headers = 0x400;
        var bytes = new byte[headers + 3 * sectionSize + tailSize];
        r.NextBytes(bytes);
        Array.Clear(bytes, 0, headers);
        var b = new Blk(headers);
        b.U8(0, 'M').U8(1, 'Z').U32(0x3C, 0x80);
        b.U32(0x80, 0x00004550).U16(0x84, 0x8664).U16(0x86, 3).U16(0x94, 0xF0); // PE, AMD64, 3 sections, optional header size
        int sec = 0x80 + 24 + 0xF0;
        void Section(int i, string name, long raw, uint ch)
        {
            int o = sec + 40 * i;
            Encoding.ASCII.GetBytes(name).CopyTo(b.B, o);
            b.U32(o + 8, (uint)sectionSize).U32(o + 12, (uint)(0x1000 * (i + 1))).U32(o + 16, (uint)sectionSize).U32(o + 20, (uint)raw).U32(o + 36, ch);
        }
        long text = headers, rdata = headers + sectionSize, data = headers + 2L * sectionSize;
        Section(0, ".text", text, 0x60000020);
        Section(1, ".rdata", rdata, 0x40000040);
        Section(2, ".data", data, 0xC0000040);
        b.B.CopyTo(bytes, 0);
        return new Layout(bytes, text, rdata, data, sectionSize);
    }
}
