using System.Security.Cryptography;
using CthExtract.Core;
using CthExtract.Gta.Crypto;
using CthExtract.Gta.Tests.Synth;
using RageLib.GTA5.Cryptography;
using Xunit;

namespace CthExtract.Gta.Tests;

public class HashTests
{
    [Theory]
    [InlineData("DiffuseSampler", 4059966321u)]
    [InlineData("BumpSampler", 1186448975u)]
    [InlineData("SpecSampler", 1619499462u)]
    [InlineData("matDiffuseColor", 408880252u)]
    [InlineData("vehicle_paint1", 0xF9FB7331u)]
    [InlineData("vehshare", 3787878547u)]
    public void Joaat_matches_reference(string name, uint expected) => Assert.Equal(expected, Hashes.Joaat(name));

    [Theory]
    [InlineData("wheel_lf", 27922)]
    [InlineData("wheel_rf", 26418)]
    [InlineData("wheel_lr", 27902)]
    [InlineData("wheel_rr", 26398)]
    [InlineData("wheel_lm1", 29921)]
    [InlineData("wheel_rm1", 5857)]
    public void Bone_tags_match_published_values(string name, int tag) => Assert.Equal(tag, Hashes.BoneTag(name));

    [Theory]
    [InlineData("siren_pa20a_wail", 0x0D329446u)]
    [InlineData("siren_firetruck_wail_01", 0x159C9182u)]
    [InlineData("siren_firetruck_quick_01", 0x161768E7u)]
    public void Sheet_siren_ids_are_these_names(string name, uint id) => Assert.Equal(id, Hashes.AwcStreamId(name));
}

public class CipherTests
{
    [Fact]
    public void Ng_decrypt_is_bit_identical_to_vendored_gta_toolkit_reference()
    {
        var r = new Random(5);
        var keys = new SynthKeys(77);
        var data = Rand.Bytes(r, 4096 + 7); // includes an unaligned tail
        var key = keys.Ng.Keys[13];
        var keyBytes = SynthKeys.KeyBytes(key);
        var reference = GTA5Crypto.Decrypt(data, keyBytes, keys.Ng.Tables);
        var ours = (byte[])data.Clone();
        NgCrypto.DecryptInPlace(ours, key, keys.Ng.Tables);
        Assert.Equal(reference, ours);
    }

    [Fact]
    public void Ng_roundtrip_with_invertible_synthetic_tables()
    {
        var keys = new SynthKeys(3);
        var plain = Rand.Bytes(new Random(9), 1000);
        var buf = (byte[])plain.Clone();
        var key = keys.NgKeyFor("x64e.rpf", 123456);
        keys.NgEncryptInPlace(buf, key);
        Assert.NotEqual(plain.AsSpan(0, 992).ToArray(), buf.AsSpan(0, 992).ToArray());
        Assert.Equal(plain.AsSpan(992).ToArray(), buf.AsSpan(992).ToArray()); // tail untouched
        NgCrypto.DecryptInPlace(buf, key, keys.Ng.Tables);
        Assert.Equal(plain, buf);
    }

    [Fact]
    public void Key_index_uses_gta5hash_name_plus_length()
    {
        var lut = Enumerable.Range(0, 256).Select(i => (byte)(i * 7 + 3)).ToArray();
        uint h = GTA5Hash.CalculateHash("vehicles.rpf", lut);
        Assert.Equal((int)((h + 4096u + 61u) % 101u), NgCrypto.KeyIndex("vehicles.rpf", 4096, lut));
        Assert.InRange(NgCrypto.KeyIndex("x64e.rpf", uint.MaxValue, lut), 0, 100);
    }

    [Fact]
    public void Aes_ecb_roundtrip_keeps_tail()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = Rand.Bytes(new Random(1), 37);
        var buf = (byte[])plain.Clone();
        AesCrypto.EncryptInPlace(buf, key);
        Assert.Equal(plain.AsSpan(32).ToArray(), buf.AsSpan(32).ToArray());
        AesCrypto.DecryptInPlace(buf, key);
        Assert.Equal(plain, buf);
    }

    [Fact]
    public void Xxtea_matches_reference_vectors_and_roundtrips()
    {
        // vectors from an independent implementation of Wheeler & Needham's corrected block TEA
        var zero = new byte[8];
        Xxtea.EncryptInPlace(zero, new uint[4]);
        Assert.Equal(new uint[] { 0x053704ab, 0x575d8c80 }, Words(zero));
        var v = Bytes(0x11111111, 0x22222222, 0x33333333, 0x44444444, 0x55555555);
        var k = new uint[] { 0x01234567, 0x89abcdef, 0xfedcba98, 0x76543210 };
        Xxtea.EncryptInPlace(v, k);
        Assert.Equal(new uint[] { 0x848d23eb, 0xf2f9f385, 0x4b856c79, 0xe9b5cc84, 0xd409ba69 }, Words(v));
        Xxtea.DecryptInPlace(v, k);
        Assert.Equal(new uint[] { 0x11111111, 0x22222222, 0x33333333, 0x44444444, 0x55555555 }, Words(v));
    }

    private static uint[] Words(byte[] b) => Enumerable.Range(0, b.Length / 4).Select(i => BitConverter.ToUInt32(b, i * 4)).ToArray();
    private static byte[] Bytes(params uint[] w) => w.SelectMany(BitConverter.GetBytes).ToArray();
}

public class KeyTests
{
    [Fact]
    public void Vendored_hash_list_has_expected_shape()
    {
        var h = KeyHashes.Gta5Pc;
        Assert.Equal(20, h.AesKey.Length);
        Assert.Equal(101, h.NgKeys.Length);
        Assert.Equal(272, h.NgTables.Length);
        Assert.All(h.NgKeys.Concat(h.NgTables), x => Assert.Equal(20, x.Length));
        Assert.Equal(0xA0, h.AesKey[0]);
    }

    [Fact]
    public void Synthetic_magic_blob_decodes_and_verifies()
    {
        var keys = new SynthKeys(11);
        using var log = new Log(null);
        var set = MagicData.Decode(keys.MagicBlob(), keys.Aes, keys.Hashes, log, out var detail);
        Assert.NotNull(set);
        Assert.Contains("verified", detail);
        Assert.Equal(keys.Ng.Keys[100], set!.Keys[100]);
        Assert.Equal(keys.Ng.Tables[16][15], set.Tables[16][15]);
        Assert.Equal(keys.Ng.Lut, set.Lut);
        Assert.Equal(keys.Ng.AwcKey, set.AwcKey);
    }

    [Fact]
    public void Magic_blob_with_a_flipped_byte_or_wrong_aes_key_is_rejected()
    {
        var keys = new SynthKeys(12);
        using var log = new Log(null);
        var blob = keys.MagicBlob();
        blob[blob.Length / 2] ^= 0x40;
        Assert.Null(MagicData.Decode(blob, keys.Aes, keys.Hashes, log, out var d1));
        Assert.Contains("did not verify", d1);
        var wrongAes = (byte[])keys.Aes.Clone();
        wrongAes[0] ^= 1;
        Assert.Null(MagicData.Decode(keys.MagicBlob(), wrongAes, keys.Hashes, log, out _));
        // and the real hash list never accepts synthetic material
        Assert.Null(MagicData.Decode(keys.MagicBlob(), keys.Aes, KeyHashes.Gta5Pc, log, out _));
    }

    [Theory]
    [InlineData(8, true)]   // 8-aligned inside .rdata: found by the section pass
    [InlineData(8, false)]  // 8-aligned outside sections: whole-file pass
    [InlineData(4, false)]
    [InlineData(1, false)]
    public void Aes_key_search_finds_planted_key_with_alignment_fallback(int align, bool inSection)
    {
        var exe = FakeExe.Build(42, sectionSize: 1 << 18, tailSize: 1 << 16);
        var key = RandomNumberGenerator.GetBytes(32);
        long off = inSection ? exe.RdataOffset + 0x400 : exe.DataOffset + exe.SectionSize + 0x100 + (align == 8 ? 0 : align == 4 ? 4 : 3);
        key.CopyTo(exe.Bytes, off);
        using var log = new Log(null);
        var hit = AesKeyFinder.Search(exe.Bytes, SHA1.HashData(key), log, TestContext.Current.CancellationToken);
        Assert.NotNull(hit);
        Assert.Equal(off, hit!.Offset);
        Assert.Equal(key, hit.Key);
        Assert.Equal(align == 8 ? 8 : align, hit.Alignment);
        Assert.Equal(inSection ? "section .rdata" : "whole file", hit.Where);
    }

    [Fact]
    public void Aes_key_search_reports_absence()
    {
        var exe = FakeExe.Build(43, sectionSize: 1 << 16, tailSize: 1 << 12);
        using var log = new Log(null);
        Assert.Null(AesKeyFinder.Search(exe.Bytes, SHA1.HashData(RandomNumberGenerator.GetBytes(32)), log, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Aes_key_is_cached_and_reverified()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cth-keys-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var exe = FakeExe.Build(44, sectionSize: 1 << 16, tailSize: 1 << 12);
            var key = RandomNumberGenerator.GetBytes(32);
            key.CopyTo(exe.Bytes, exe.RdataOffset + 64);
            var exePath = Path.Combine(dir, "GTA5.exe");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(exePath, exe.Bytes);
            using var log = new Log(null);
            var keysDir = Path.Combine(dir, "keys");
            Assert.Equal(key, AesKeyFinder.Get(exePath, keysDir, SHA1.HashData(key), log, TestContext.Current.CancellationToken, out var d1));
            Assert.Contains("found", d1);
            Assert.True(File.Exists(Path.Combine(keysDir, AesKeyFinder.CacheFileName)));
            File.Delete(exePath); // the cache alone is enough now
            Assert.Equal(key, AesKeyFinder.Get(exePath, keysDir, SHA1.HashData(key), log, TestContext.Current.CancellationToken, out var d2));
            Assert.Contains("cache", d2);
            // a cache that does not match the expected hash is ignored
            Assert.Null(AesKeyFinder.Get(exePath, keysDir, SHA1.HashData(new byte[32]), log, TestContext.Current.CancellationToken, out var d3));
            Assert.Contains("cannot read", d3);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
