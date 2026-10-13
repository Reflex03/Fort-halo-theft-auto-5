namespace CthExtract.Oodle.Tests.Synth;

/// <summary>A deterministic spread of encoder settings x inputs, covering every codec, literal mode, entropy coder,
/// offset coding and framing variant that <see cref="SynthOodle"/> can emit.</summary>
internal static class SynthMatrix
{
    public sealed record Case(string Name, byte[] Data, SynthOptions Options);

    static readonly int[] Sizes =
    {
        1, 7, 8, 9, 16, 17, 33, 100, 1000, 4096, 20000, 65535, 65536, 65537, 70000, 131071, 131072, 131073, 200000,
        262143, 262144, 262145, 300000, 600000,
    };

    static readonly EntropyKind[] LitKinds =
    {
        EntropyKind.Raw, EntropyKind.Huff3, EntropyKind.Huff6, EntropyKind.Huff3Gamma, EntropyKind.Rle,
        EntropyKind.Recursive, EntropyKind.RecursiveMulti, EntropyKind.Huff3New, EntropyKind.Huff6New, EntropyKind.Tans,
        EntropyKind.RecursiveFullMulti,
    };

    static readonly EntropyKind[] SmallKinds =
    {
        EntropyKind.Raw, EntropyKind.Huff3, EntropyKind.Rle, EntropyKind.Huff6, EntropyKind.Huff3New, EntropyKind.Tans,
    };

    /// <summary>count cases; sizes up to maxSize.</summary>
    public static IEnumerable<Case> Cases(int count, int seed, int maxSize = 600000)
    {
        var rng = new Random(seed);
        var sizes = Sizes.Where(s => s <= maxSize).ToArray();
        int[] codecs = { 6, 10, 12 };
        for (int i = 0; i < count; i++)
        {
            int codec = codecs[i % 3];
            string kind = SynthData.Kinds[rng.Next(SynthData.Kinds.Length)];
            int size = sizes[rng.Next(sizes.Length)];
            var data = SynthData.Make(kind, size, seed * 1000 + i);
            var o = new SynthOptions
            {
                Codec = codec,
                LzMode = codec == 12 ? rng.Next(6) : rng.Next(2),
                Literals = LitKinds[rng.Next(LitKinds.Length)],
                Commands = SmallKinds[rng.Next(SmallKinds.Length)],
                Other = SmallKinds[rng.Next(SmallKinds.Length)],
                OffsetScale = codec != 10 ? new[] { 0, 0, 1, 3, 16 }[rng.Next(5)] : 0,
                PreferFar = codec == 10 && rng.Next(3) == 0,
                MultiCmd = codec == 12 && rng.Next(2) == 0,
                FullMultiArray = codec == 12 && rng.Next(2) == 0,
                Off16Entropy = codec == 10 && rng.Next(2) == 0,
                MinMatch = 3 + rng.Next(4),
                SearchDepth = 4 + rng.Next(28),
                ChunkKind = rng.Next(8) switch { 0 => 1, 1 => 2, 2 => 3, _ => 0 },
                Memset = rng.Next(4) != 0,
                Uncompressed = rng.Next(25) == 0,
                Checksum = rng.Next(12) == 0 ? 0 : null,
                Seed = seed * 7919 + i,
            };
            var name = $"{i:D3}-{CodecName(codec)}-m{o.LzMode}-{kind}-{size}-lit{o.Literals}-cmd{o.Commands}-oth{o.Other}" +
                       $"-sc{o.OffsetScale}{(o.MultiCmd ? "-multicmd" : "")}{(o.FullMultiArray ? "-fullma" : "")}{(o.Off16Entropy ? "-o16e" : "")}{(o.PreferFar ? "-far" : "")}-ck{o.ChunkKind}" +
                       $"{(o.Uncompressed ? "-unc" : "")}{(o.Checksum.HasValue ? "-crc" : "")}";
            yield return new Case(name, data, o);
        }
    }

    public static string CodecName(int codec) => codec switch { 6 => "kraken", 10 => "mermaid", 12 => "leviathan", _ => "?" };
}
