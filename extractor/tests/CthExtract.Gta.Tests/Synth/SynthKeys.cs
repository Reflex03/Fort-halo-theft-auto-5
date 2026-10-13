using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using CthExtract.Gta.Crypto;

namespace CthExtract.Gta.Tests.Synth;

/// <summary>
/// Random, test-only key material with the same shapes as the real thing: a 32-byte AES key, 101 NG keys, 17x16 NG
/// decryption tables, a 256-byte LUT and a 4-word AWC key. The NG tables are built to be invertible (each round word
/// = M * (S(b0) | S(b1)&lt;&lt;8 | S(b2)&lt;&lt;16 | S(b3)&lt;&lt;24) with random byte permutations S and a random invertible
/// GF(2) 32x32 matrix M) so the tests can also ENCRYPT archives. Nothing here resembles real game keys.
/// </summary>
public sealed class SynthKeys
{
    public byte[] Aes { get; }
    public NgKeySet Ng { get; }
    public KeyHashes Hashes { get; }

    // inverse data for encryption
    private readonly byte[][][] _sInv = new byte[17][][]; // [round][table][256]
    private readonly uint[][][] _mInv = new uint[17][][]; // [round][word][32 rows]

    private static readonly int[][] ListsA = { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 }, new[] { 8, 9, 10, 11 }, new[] { 12, 13, 14, 15 } };
    private static readonly int[][] ListsB = { new[] { 0, 7, 10, 13 }, new[] { 1, 4, 11, 14 }, new[] { 2, 5, 8, 15 }, new[] { 3, 6, 9, 12 } };
    private static int[][] Lists(int round) => round is 0 or 1 or 16 ? ListsA : ListsB;

    public SynthKeys(int seed)
    {
        var r = new Random(seed);
        Aes = Rand.Bytes(r, 32);
        var keys = new uint[101][];
        for (int i = 0; i < 101; i++) { keys[i] = new uint[68]; for (int k = 0; k < 68; k++) keys[i][k] = Rand.U32(r); }
        var tables = new uint[17][][];
        for (int round = 0; round < 17; round++)
        {
            tables[round] = new uint[16][];
            _sInv[round] = new byte[16][];
            _mInv[round] = new uint[4][];
            var lists = Lists(round);
            for (int w = 0; w < 4; w++)
            {
                var (m, mi) = RandomInvertible(r);
                _mInv[round][w] = mi;
                for (int slot = 0; slot < 4; slot++)
                {
                    int t = lists[w][slot];
                    var s = Enumerable.Range(0, 256).Select(x => (byte)x).OrderBy(_ => r.Next()).ToArray();
                    var sInv = new byte[256];
                    for (int x = 0; x < 256; x++) sInv[s[x]] = (byte)x;
                    _sInv[round][t] = sInv;
                    tables[round][t] = new uint[256];
                    for (int b = 0; b < 256; b++) tables[round][t][b] = Apply(m, (uint)s[b] << (8 * slot));
                }
            }
        }
        var lut = Rand.Bytes(r, 256);
        var awc = new uint[4];
        for (int i = 0; i < 4; i++) awc[i] = Rand.U32(r);
        Ng = new NgKeySet { Keys = keys, Tables = tables, Lut = lut, AwcKey = awc };
        Hashes = new KeyHashes
        {
            AesKey = SHA1.HashData(Aes),
            NgKeys = keys.Select(k => SHA1.HashData(KeyBytes(k))).ToArray(),
            NgTables = Enumerable.Range(0, 272).Select(i => SHA1.HashData(KeyBytes(tables[i / 16][i % 16]))).ToArray(),
            Lut = SHA1.HashData(lut),
        };
    }

    public static byte[] KeyBytes(uint[] u)
    {
        var b = new byte[u.Length * 4];
        for (int i = 0; i < u.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), u[i]);
        return b;
    }

    // ---- GF(2) 32x32 matrices: rows as bitmasks, out bit i = parity(row_i & v)
    private static uint Apply(uint[] m, uint v)
    {
        uint o = 0;
        for (int i = 0; i < 32; i++) if ((System.Numerics.BitOperations.PopCount(m[i] & v) & 1) != 0) o |= 1u << i;
        return o;
    }

    private static (uint[] m, uint[] inv) RandomInvertible(Random r)
    {
        while (true)
        {
            var m = new uint[32];
            for (int i = 0; i < 32; i++) m[i] = Rand.U32(r);
            var inv = Invert(m);
            if (inv is not null) return (m, inv);
        }
    }

    private static uint[]? Invert(uint[] m)
    {
        var a = (uint[])m.Clone();
        var inv = new uint[32];
        for (int i = 0; i < 32; i++) inv[i] = 1u << i;
        for (int col = 0; col < 32; col++)
        {
            int piv = -1;
            for (int row = col; row < 32; row++) if (((a[row] >> col) & 1) != 0) { piv = row; break; }
            if (piv < 0) return null;
            (a[col], a[piv]) = (a[piv], a[col]);
            (inv[col], inv[piv]) = (inv[piv], inv[col]);
            for (int row = 0; row < 32; row++)
                if (row != col && ((a[row] >> col) & 1) != 0) { a[row] ^= a[col]; inv[row] ^= inv[col]; }
        }
        return inv;
    }

    /// <summary>Inverse of NgCrypto.DecryptInPlace for these tables (trailing len%16 bytes untouched).</summary>
    public void NgEncryptInPlace(Span<byte> data, uint[] key)
    {
        Span<byte> blk = stackalloc byte[16];
        Span<byte> tmp = stackalloc byte[16];
        for (int b = 0; b < data.Length / 16; b++)
        {
            var d = data.Slice(b * 16, 16);
            d.CopyTo(blk);
            for (int round = 16; round >= 0; round--)
            {
                var lists = Lists(round);
                for (int w = 0; w < 4; w++)
                {
                    uint x = BinaryPrimitives.ReadUInt32LittleEndian(blk.Slice(w * 4)) ^ key[round * 4 + w];
                    uint v = Apply(_mInv[round][w], x);
                    for (int slot = 0; slot < 4; slot++)
                    {
                        int t = lists[w][slot];
                        tmp[t] = _sInv[round][t][(v >> (8 * slot)) & 0xFF];
                    }
                }
                tmp.CopyTo(blk);
            }
            blk.CopyTo(d);
        }
    }

    public uint[] NgKeyFor(string name, uint length) => Ng.Keys[NgCrypto.KeyIndex(name, length, Ng.Lut)];

    /// <summary>A magic.dat-shaped blob for these keys: payload -> raw deflate -> AES-ECB -> + four Random streams.</summary>
    public byte[] MagicBlob(bool includeAwcKey = true)
    {
        using var payload = new MemoryStream();
        foreach (var k in Ng.Keys) payload.Write(KeyBytes(k));
        for (int i = 0; i < 272; i++) payload.Write(KeyBytes(Ng.Tables[i / 16][i % 16]));
        payload.Write(Ng.Lut);
        if (includeAwcKey) payload.Write(KeyBytes(Ng.AwcKey!));
        using var z = new MemoryStream();
        using (var ds = new DeflateStream(z, CompressionLevel.Optimal, leaveOpen: true)) ds.Write(payload.ToArray());
        var m = z.ToArray();
        AesCrypto.EncryptInPlace(m, Aes);
        var rnd = new Random(unchecked((int)CthExtract.Gta.Hashes.Joaat(Aes.AsSpan())));
        var streams = new byte[4][];
        for (int s = 0; s < 4; s++) { streams[s] = new byte[m.Length]; rnd.NextBytes(streams[s]); }
        for (int i = 0; i < m.Length; i++) m[i] = (byte)(m[i] + streams[0][i] + streams[1][i] + streams[2][i] + streams[3][i]);
        return m;
    }
}
