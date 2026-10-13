namespace CthExtract.Fortnite.Unreal;

/// <summary>
/// Connects CUE4Parse's Oodle hook (vendored patch: Compression.UseOodle) to the extractor's own decoder (CthExtract.Oodle, a GPL-3.0 C# port of ooz)
/// (CthExtract.Oodle, a C# port of ooz). No native Oodle library is ever loaded or downloaded: the vendored CUE4Parse
/// has no OodleHelper/ZlibHelper download code at all, and without CthExtract.Oodle Oodle blocks simply fail.
/// </summary>
public static class OodleBridge
{
    private static readonly object Lock = new();
    private static bool _done;

    /// <summary>True when this build contains the managed Oodle decoder.</summary>
    public static bool Available =>
#if CTH_OODLE
        true;
#else
        false;
#endif

    public static string Describe() => Available
        ? "Oodle: CthExtract.Oodle (managed ooz port)"
        : "Oodle: not available in this build (CthExtract.Oodle missing); Oodle-compressed containers cannot be read";

    /// <summary>Register the decoder once per process.</summary>
    public static void Ensure()
    {
        lock (Lock)
        {
            if (_done) return;
            _done = true;
#if CTH_OODLE
            CUE4Parse.Compression.Compression.UseOodle(static (ReadOnlySpan<byte> source, Span<byte> destination, out int written) =>
            {
                int n = CthExtract.Oodle.Ooz.Decompress(source, destination);
                written = n < 0 ? 0 : n;
                return n >= 0;
            });
#endif
        }
    }
}
