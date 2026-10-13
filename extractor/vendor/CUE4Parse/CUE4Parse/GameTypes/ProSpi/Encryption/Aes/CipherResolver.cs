using System.Buffers.Binary;

namespace CUE4Parse.GameTypes.ProSpi.Encryption.Aes;

public static partial class ProSpiEncryption
{
    private const int Mt19937_64StateSize = 312;
    private const int Mt19937_64MiddleWord = 156;

    private const ulong Mt19937_64MatrixA = 0x8000000000000001;
    private const ulong Mt19937_64UpperMask = 0x8000000000000002;
    private const ulong Mt19937_64LowerMask = 0x00000003;

    private enum KeyLayout
    {
        Full,
        First16Repeated,
        Second16Repeated,
        Swap16
    }

    private enum NonceLayout
    {
        Nonce64,
        Nonce96,
        Nonce64WithFirstWord,
        SalsaSeed012345,
        SalsaSeed234501,
        SalsaSeed345012
    }

    private enum CipherKind
    {
        ChaCha,
        ProSpiCustomPermuted,
        ProSpiCustomPermutedAlt,
        ProSpiSalsaRorPremixed,
        ProSpiCustomPermutedPremixed,
        ProSpiSalsaRor
    }

    private readonly record struct CipherSpecs
    (
        KeyLayout KeyLayout,
        NonceLayout NonceLayout,
        int NonceOffset,
        int Rounds,
        CipherKind CipherKind = CipherKind.ChaCha,
        int PremixRounds = 0
    );

    // It goes like so, trailer -> derive descriptorLookupKey (using MT19937-64) -> use it to map to descriptor -> use descriptor to choose cipher algorithm
    private static bool TryResolveCipherSpec(ReadOnlySpan<byte> trailer, out CipherSpecs spec)
    {
        spec = default;
        if (!TryGetDescriptorKey(trailer, out var descriptor))
            return false;

        return TryGetKnownCipherSpec(descriptor, out spec);
    }

    private static bool TryGetKnownCipherSpec(ulong descriptor, out CipherSpecs spec)
    {
        spec = descriptor switch
        {
            0x0000000100000004 => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce96, 12, 18),
            0x8000000000000005 => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce64, 12, 12),
            0x8000000000000006 => new CipherSpecs(KeyLayout.First16Repeated, NonceLayout.Nonce96, 12, 20),
            0x8000000000000007 => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce64, 8, 10),
            0x0000000100000008 => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce64, 8, 8),
            0x0000000100000009 => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce64, 16, 10),
            0x800000000000000A => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce96, 8, 10),
            0x000000010000000B => new CipherSpecs(KeyLayout.Swap16, NonceLayout.Nonce96, 0, 10),
            0x800000000000000C => new CipherSpecs(KeyLayout.First16Repeated, NonceLayout.Nonce64, 0, 10),
            0x800000000000000D => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce96, 0, 10),
            0x000000010000000E => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce64WithFirstWord, 16, 14),
            0x000000010000000F => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce64, 0, 10, CipherKind.ProSpiCustomPermuted),
            0x0000000100000010 => new CipherSpecs(KeyLayout.Swap16, NonceLayout.Nonce64, 0, 6, CipherKind.ProSpiCustomPermuted),
            0x0000000100000011 => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce64, 8, 6, CipherKind.ProSpiCustomPermuted),
            0x8000000000000012 => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce64, 0, 8, CipherKind.ProSpiCustomPermuted),
            0x8000000000000013 => new CipherSpecs(KeyLayout.Swap16, NonceLayout.Nonce64, 0, 6, CipherKind.ProSpiCustomPermuted),
            0x8000000000000014 => new CipherSpecs(KeyLayout.First16Repeated, NonceLayout.Nonce64, 0, 7, CipherKind.ProSpiCustomPermuted),
            0x8000000000000015 => new CipherSpecs(KeyLayout.Second16Repeated, NonceLayout.Nonce64, 8, 6, CipherKind.ProSpiCustomPermuted),
            0x0000000100000016 => new CipherSpecs(KeyLayout.First16Repeated, NonceLayout.Nonce64, 8, 4, CipherKind.ProSpiCustomPermuted),
            0x0000000100000017 => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce64, 12, 10, CipherKind.ProSpiCustomPermuted),
            0x0000000100000018 => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce64, 8, 5, CipherKind.ProSpiCustomPermuted),
            0x8000000000000019 => new CipherSpecs(KeyLayout.Swap16, NonceLayout.Nonce64, 8, 6, CipherKind.ProSpiCustomPermuted),
            0x000000010000001A => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce64, 16, 7, CipherKind.ProSpiCustomPermuted),
            0x800000000000001B => new CipherSpecs(KeyLayout.Swap16, NonceLayout.Nonce64, 16, 5, CipherKind.ProSpiCustomPermuted),
            0x000000010000001C => new CipherSpecs(KeyLayout.Full, NonceLayout.Nonce64, 12, 6, CipherKind.ProSpiCustomPermutedAlt),
            0x800000000000001D => new CipherSpecs(KeyLayout.Full, NonceLayout.SalsaSeed345012, 0, 8, CipherKind.ProSpiCustomPermutedPremixed, 8),
            0x800000000000001E => new CipherSpecs(KeyLayout.Swap16, NonceLayout.Nonce64, 8, 8, CipherKind.ProSpiSalsaRor),
            0x000000010000001F => new CipherSpecs(KeyLayout.First16Repeated, NonceLayout.Nonce64, 12, 4, CipherKind.ProSpiSalsaRor),
            0x0000000100000020 => new CipherSpecs(KeyLayout.Full, NonceLayout.SalsaSeed234501, 0, 4, CipherKind.ProSpiSalsaRorPremixed, 4),
            0x8000000000000021 => new CipherSpecs(KeyLayout.Second16Repeated, NonceLayout.SalsaSeed012345, 0, 5, CipherKind.ProSpiSalsaRorPremixed, 5),
            0x8000000000000022 => new CipherSpecs(KeyLayout.Full, NonceLayout.SalsaSeed345012, 0, 6, CipherKind.ProSpiSalsaRorPremixed, 6),
            _ => default
        };

        return spec.Rounds != 0;
    }

    private static bool TryGetDescriptorKey(ReadOnlySpan<byte> trailer, out ulong descriptor)
    {
        descriptor = 0;

        var descriptorLookupKey = BuildDescriptorLookupKey(trailer);
        if (TryGetDescriptorFromGeneratedTable(descriptorLookupKey, out descriptor))
            return true;

        if (_missingDescriptorLookupKeys.TryAdd(descriptorLookupKey, 0))
        {
            Log.Information("ProSpi descriptor key lookup missing: descriptorLookupKey=0x{DescriptorLookupKey:X16}, trailer={Trailer}", descriptorLookupKey, Convert.ToHexString(trailer));
        }

        return false;
    }


    private static ulong BuildDescriptorLookupKey(ReadOnlySpan<byte> trailer)
    {
        Span<uint> seedInput =
        [
            BinaryPrimitives.ReadUInt32LittleEndian(trailer),
            BinaryPrimitives.ReadUInt32LittleEndian(trailer[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(trailer[16..]),
        ];

        Span<ulong> state = stackalloc ulong[Mt19937_64StateSize];
        SeedMt19937_64FromSeedSequence(state, seedInput, out var index);
        var secondSeed = NextMt19937_64(state, ref index) & 0xFFF;

        SeedMt19937_64(state, secondSeed, out index);
        return NextMt19937_64(state, ref index);
    }

    private static void SeedMt19937_64(Span<ulong> state, ulong seed, out int index)
    {
        state[0] = seed;
        for (var i = 1; i < state.Length; i++)
            state[i] = 0x0000000100000023 * (state[i - 1] ^ (state[i - 1] >> 62)) + (uint) i;

        index = Mt19937_64StateSize;
    }

    private static void SeedMt19937_64FromSeedSequence(Span<ulong> state, ReadOnlySpan<uint> seedInput, out int index)
    {
        Span<uint> generated = stackalloc uint[Mt19937_64StateSize * 2];
        GenerateSeedSequence(seedInput, generated);

        for (var i = 0; i < state.Length; i++)
            state[i] = generated[i * 2] | ((ulong) generated[i * 2 + 1] << 32);

        index = Mt19937_64StateSize;
    }

    private static void GenerateSeedSequence(ReadOnlySpan<uint> seedInput, Span<uint> output)
    {
        if (output.Length == 0)
            return;

        output.Fill(0x80000024);
        var n = output.Length;
        var s = seedInput.Length;
        var t = n >= 623 ? 11 :
            n >= 68 ? 7 :
            n >= 39 ? 5 :
            n >= 7 ? 3 :
            (n - 1) / 2;
        var p = (n - t) / 2;
        var q = p + t;
        var m = Math.Max(s + 1, n);

        for (var k = 0; k < m; k++)
        {
            var r1 = 1664525U * SeedSeqTransform(output[k % n] ^ output[(k + p) % n] ^ output[(k + n - 1) % n]);
            output[(k + p) % n] += r1;

            var kMod = (uint) (k % n);
            var add = k == 0 ? (uint) s :
                k <= s ? seedInput[k - 1] + kMod :
                kMod;
            var r2 = r1 + add;
            output[(k + q) % n] += r2;
            output[k % n] = r2;
        }

        for (var k = m; k < m + n; k++)
        {
            var r3 = 1566083941U * SeedSeqTransform(output[k % n] + output[(k + p) % n] + output[(k + n - 1) % n]);
            output[(k + p) % n] ^= r3;

            var r4 = r3 - (uint) (k % n);
            output[(k + q) % n] ^= r4;
            output[k % n] = r4;
        }
    }

    private static uint SeedSeqTransform(uint value) => value ^ (value >> 27);

    private static ulong NextMt19937_64(Span<ulong> state, ref int index)
    {
        if (index >= Mt19937_64StateSize)
            TwistMt19937_64(state, ref index);

        var y = state[index++];
        y ^= (y >> 29) & 0x0000000100000025;
        y ^= (y << 17) & 0x0000000100000026;
        y ^= (y << 37) & 0x8000000000000027;
        y ^= y >> 43;
        return y;
    }

    private static void TwistMt19937_64(Span<ulong> state, ref int index)
    {
        for (var i = 0; i < Mt19937_64StateSize; i++)
        {
            var x = (state[i] & Mt19937_64UpperMask) | (state[(i + 1) % Mt19937_64StateSize] & Mt19937_64LowerMask);
            var xA = x >> 1;
            if ((x & 1) != 0)
                xA ^= Mt19937_64MatrixA;
            state[i] = state[(i + Mt19937_64MiddleWord) % Mt19937_64StateSize] ^ xA;
        }

        index = 0;
    }
}
