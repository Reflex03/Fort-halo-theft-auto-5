using System.Buffers.Binary;
using System.Numerics;

namespace CthExtract.Gta.Tests.Synth;

/// <summary>A little-endian byte block builder for synthetic test data.</summary>
public sealed class Blk(int size)
{
    public byte[] B { get; } = new byte[size];
    public Blk U8(int o, int v) { B[o] = (byte)v; return this; }
    public Blk U16(int o, int v) { BinaryPrimitives.WriteUInt16LittleEndian(B.AsSpan(o), (ushort)v); return this; }
    public Blk S16(int o, int v) { BinaryPrimitives.WriteInt16LittleEndian(B.AsSpan(o), (short)v); return this; }
    public Blk U32(int o, uint v) { BinaryPrimitives.WriteUInt32LittleEndian(B.AsSpan(o), v); return this; }
    public Blk U64(int o, ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(B.AsSpan(o), v); return this; }
    public Blk F32(int o, float v) { BinaryPrimitives.WriteSingleLittleEndian(B.AsSpan(o), v); return this; }
    public Blk Vec3(int o, Vector3 v) => F32(o, v.X).F32(o + 4, v.Y).F32(o + 8, v.Z);
}

public static class Rand
{
    public static byte[] Bytes(Random r, int n) { var b = new byte[n]; r.NextBytes(b); return b; }
    public static uint U32(Random r) => (uint)r.Next() ^ ((uint)r.Next(4) << 30) ^ ((uint)r.Next() << 7);
}
