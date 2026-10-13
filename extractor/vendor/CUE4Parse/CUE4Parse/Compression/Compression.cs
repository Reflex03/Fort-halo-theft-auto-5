using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Readers;

using K4os.Compression.LZ4;

using OffiUtils;

using ZstdSharp;

namespace CUE4Parse.Compression;

public static class Compression
{
    public const int LOADING_COMPRESSION_CHUNK_SIZE = 131072;

    public static IDecompressor Decompressor => _decompressor;

    // CthExtract: no Oodle decoder ships with this vendored copy (OodleSharp, Oodle.NET and the OodleUE / zlib-ng
    // download helpers were removed). The host registers its own decoder (CthExtract: a GPL-3.0 C# port of ooz) with UseOodle(); until then
    // Oodle blocks fail with a clear error instead of loading or downloading a native library.
    private static IDecompressor _decompressor = DecompressorBuilder.Default
        .Add(CompressionAlgorithm.Oodle, OodleNotConfigured)
        .Add(CompressionAlgorithm.LZ4, static (source, destination, out written)
            => (written = LZ4Codec.Decode(source, destination)) > 0, replace: true)
        .Add(CompressionAlgorithm.Zstd, static (source, destination, out written) =>
        {
            using var decompressor = new Decompressor();
            return decompressor.TryUnwrap(source, destination, out written);
        }, replace: true)
        .Build();

    /// <summary>True once <see cref="UseOodle"/> registered a decoder (CthExtract patch).</summary>
    public static bool IsOodleConfigured { get; private set; }

    private static bool OodleNotConfigured(ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        written = 0;
        throw new FileLoadException("Oodle-compressed data, but no Oodle decoder was registered (Compression.UseOodle)");
    }

    /// <summary>Registers the managed Oodle decoder (CthExtract patch). It must return true and set written to the
    /// number of bytes produced, which must equal destination.Length for a block to be accepted.</summary>
    public static void UseOodle(DecompressDelegate decompressor)
    {
        _decompressor = new DecompressorBuilder()
            .AddRange(_decompressor, true)
            .Add(CompressionAlgorithm.Oodle, decompressor, replace: true)
            .Build();
        IsOodleConfigured = true;
    }

    public static void UseLZO(DecompressDelegate decompressor)
    {
        _decompressor = new DecompressorBuilder()
            .AddRange(_decompressor, true)
            .Add(CompressionAlgorithm.LZO, decompressor, replace: true)
            .Build();
    }

    public static byte[] Decompress(byte[] compressed, int uncompressedSize, CompressionMethod method, FArchive? reader = null)
        => Decompress(compressed, 0, compressed.Length, uncompressedSize, method, reader);

    public static byte[] Decompress(byte[] compressed, int compressedOffset, int compressedCount, int uncompressedSize, CompressionMethod method, FArchive? reader = null)
    {
        var uncompressed = new byte[uncompressedSize];
        Decompress(compressed, compressedOffset, compressedCount, uncompressed, 0, uncompressedSize, method);
        return uncompressed;
    }

    public static void Decompress(
        byte[] compressed, int compressedOffset, int compressedSize,
        byte[] uncompressed, int uncompressedOffset, int uncompressedSize,
        CompressionMethod method, FArchive? reader = null)
    {
        var src = new ReadOnlySpan<byte>(compressed, compressedOffset, compressedSize);
        var dst = new Span<byte>(uncompressed, uncompressedOffset, uncompressedSize);
        Decompress(src, dst, method, reader);
    }

    public static void Decompress(
        ReadOnlySpan<byte> compressed,
        Span<byte> uncompressed,
        CompressionMethod method, FArchive? reader = null)
    {
        CompressionAlgorithm algorithm = method switch
        {
            CompressionMethod.None => 0,
            CompressionMethod.Zlib or CompressionMethod.XB1Zlib or CompressionMethod.XboxOneGDKZlib => CompressionAlgorithm.Zlib,
            CompressionMethod.Gzip => CompressionAlgorithm.Gzip,
            CompressionMethod.Oodle => CompressionAlgorithm.Oodle,
            CompressionMethod.LZ4 => CompressionAlgorithm.LZ4,
            CompressionMethod.LZO => CompressionAlgorithm.LZO,
            CompressionMethod.Brotli => CompressionAlgorithm.Brotli,
            CompressionMethod.Zstd => CompressionAlgorithm.Zstd,
            _ when reader is not null => throw new UnknownCompressionMethodException(reader, $"Compression method \"{method}\" is unknown"),
            _ => throw new UnknownCompressionMethodException($"Compression method \"{method}\" is unknown")
        };

        if (algorithm == 0)
        {
            compressed.CopyTo(uncompressed);
            return;
        }

        if (!_decompressor.TryDecompress(algorithm, compressed, uncompressed, out int bytesWritten) || bytesWritten != uncompressed.Length)
        {
            throw new FileLoadException($"Failed to decompress {method} data (Expected: {uncompressed.Length}, Result: {bytesWritten})");
        }
    }
}
