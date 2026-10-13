using System.Diagnostics;
using CthExtract.Oodle.Internal;

namespace CthExtract.Oodle.Tests;

/// <summary>A compressed stream and the size it decompresses to.</summary>
internal sealed record Seed(string Name, byte[] Stream, int RawSize);

/// <summary>
/// Mutation fuzzer for the decoder core. Each input is placed directly against a guard page (alternately at the start
/// and at the end of a guarded region, so both under- and overruns fault), the output likewise, and the decoder's own
/// memory too. The core is called without the public API's try/catch, so a managed exception also fails the run.
/// The documented padding (64 bytes around the input, 64 after the output) is the only slack it gets.
/// </summary>
internal sealed unsafe class OozFuzzer : IDisposable
{
    readonly GuardedMemory? _in, _out, _mem;
    readonly byte[]? _inArr, _outArr;
    readonly OozDecoder _dec;
    readonly int _maxInput, _maxOutput;

    public long Iterations, Decoded, Rejected;
    public double MaxSeconds;
    public string? SlowestInput;
    public readonly Dictionary<OozErrorCode, long> Errors = new();

    public bool Guarded => _mem != null;

    public OozFuzzer(int maxInput, int maxOutput, bool useGuardPages = true)
    {
        _maxInput = maxInput;
        _maxOutput = maxOutput;
        if (useGuardPages && GuardedMemory.IsSupported)
        {
            _in = new GuardedMemory(maxInput + 2 * OozDecoder.InputPad);
            _out = new GuardedMemory(maxOutput + OozDecoder.OutputPad);
            _mem = new GuardedMemory(OozDecoder.MemorySize);
            // decoder memory ends exactly at the trailing guard page
            _dec = new OozDecoder(_mem.End - OozDecoder.MemorySize);
        }
        else
        {
            _inArr = GC.AllocateArray<byte>(maxInput + 2 * OozDecoder.InputPad, pinned: true);
            _outArr = GC.AllocateArray<byte>(maxOutput + OozDecoder.OutputPad, pinned: true);
            _dec = new OozDecoder();
        }
    }

    public void Dispose()
    {
        _in?.Dispose();
        _out?.Dispose();
        _mem?.Dispose();
    }

    /// <summary>Decodes input into dstLen bytes through the core, with the input/output flush against a guard page.
    /// Returns the core's result (dstLen or -1).</summary>
    public int Decode(ReadOnlySpan<byte> input, int dstLen, bool atStart, out OozError error, Span<byte> outputCopy = default)
    {
        if (input.Length > _maxInput) input = input[.._maxInput];
        if (dstLen > _maxOutput) dstLen = _maxOutput;
        if (dstLen < 1) dstLen = 1;

        int inNeed = input.Length + 2 * OozDecoder.InputPad;
        int outNeed = dstLen + OozDecoder.OutputPad;
        byte* inBase, outBase;
        if (_in != null)
        {
            inBase = atStart ? _in.Start : _in.End - inNeed;
            outBase = atStart ? _out!.Start : _out!.End - outNeed;
        }
        else
        {
            fixed (byte* pi = _inArr) inBase = pi;   // pinned arrays: the pointers stay valid
            fixed (byte* po = _outArr) outBase = po;
        }
        new Span<byte>(inBase, OozDecoder.InputPad).Clear();
        input.CopyTo(new Span<byte>(inBase + OozDecoder.InputPad, input.Length));
        new Span<byte>(inBase + OozDecoder.InputPad + input.Length, OozDecoder.InputPad).Clear();
        new Span<byte>(outBase, outNeed).Fill(0xCD);

        long t0 = Stopwatch.GetTimestamp();
        int n = _dec.Decompress(inBase + OozDecoder.InputPad, input.Length, outBase, dstLen, out error);
        double secs = Stopwatch.GetElapsedTime(t0).TotalSeconds;

        Iterations++;
        if (n == dstLen) Decoded++; else Rejected++;
        Errors[error.Code] = Errors.GetValueOrDefault(error.Code) + 1;
        if (secs > MaxSeconds)
        {
            MaxSeconds = secs;
            SlowestInput = $"len={input.Length} dst={dstLen}";
        }
        if (!outputCopy.IsEmpty && n == dstLen)
            new ReadOnlySpan<byte>(outBase, Math.Min(dstLen, outputCopy.Length)).CopyTo(outputCopy);
        return n;
    }

    static readonly byte[] InterestingBytes = { 0x00, 0x01, 0x02, 0x03, 0x07, 0x08, 0x0F, 0x10, 0x1F, 0x20, 0x3F, 0x40, 0x7F, 0x80, 0x81, 0xC0, 0xFE, 0xFF };
    static readonly ushort[] InterestingWords = { 0x0000, 0x0001, 0x00FF, 0x0100, 0x3FFF, 0x4000, 0x7FFF, 0x8000, 0xFFFE, 0xFFFF };

    /// <summary>1-4 random mutations: bit flips, interesting bytes/words, truncation, deletion, insertion, splicing
    /// from another seed. Mostly leaves the 2-byte block header alone so mutations reach the deeper layers.</summary>
    public static byte[] Mutate(Random rng, byte[] seed, IReadOnlyList<Seed> all)
    {
        var m = new List<byte>(seed);
        int ops = 1 + rng.Next(4);
        int lo = rng.Next(8) == 0 ? 0 : Math.Min(2, m.Count);
        for (int op = 0; op < ops && m.Count > lo; op++)
        {
            int pos = lo + rng.Next(m.Count - lo);
            switch (rng.Next(12))
            {
                case 0:
                case 1:
                case 2:
                    m[pos] ^= (byte)(1 << rng.Next(8));
                    break;
                case 3:
                    m[pos] = (byte)rng.Next(256);
                    break;
                case 4:
                    m[pos] = InterestingBytes[rng.Next(InterestingBytes.Length)];
                    break;
                case 5:
                    if (pos + 1 < m.Count)
                    {
                        ushort w = InterestingWords[rng.Next(InterestingWords.Length)];
                        bool be = rng.Next(2) == 0;
                        m[pos] = (byte)(be ? w >> 8 : w);
                        m[pos + 1] = (byte)(be ? w : w >> 8);
                    }
                    break;
                case 6:
                    m.RemoveRange(pos, m.Count - pos);
                    break;
                case 7:
                    m.RemoveRange(pos, Math.Min(m.Count - pos, 1 + rng.Next(16)));
                    break;
                case 8:
                {
                    int k = 1 + rng.Next(16);
                    var ins = new byte[k];
                    rng.NextBytes(ins);
                    m.InsertRange(pos, ins);
                    break;
                }
                case 9:
                {
                    var other = all[rng.Next(all.Count)].Stream;
                    if (other.Length == 0) break;
                    int from = rng.Next(other.Length);
                    int k = Math.Min(other.Length - from, 1 + rng.Next(64));
                    for (int i = 0; i < k && pos + i < m.Count; i++)
                        m[pos + i] = other[from + i];
                    break;
                }
                case 10:
                    // flip a run of bits in a small window
                    for (int i = 0; i < 4 && pos + i < m.Count; i++)
                        m[pos + i] ^= (byte)rng.Next(256);
                    break;
                default:
                    // duplicate a slice in place (shifts everything after it)
                    int len = Math.Min(m.Count - pos, 1 + rng.Next(32));
                    m.InsertRange(pos, m.GetRange(pos, len));
                    break;
            }
        }
        return m.ToArray();
    }

    /// <summary>Random but structurally plausible streams: a valid block header (Kraken, Mermaid or Leviathan), a
    /// quantum header whose size matches, then a chunk header (LZ or entropy-only) and random body bytes.</summary>
    public static Seed RandomStructured(Random rng)
    {
        byte codec = new byte[] { 6, 10, 12 }[rng.Next(3)];
        int raw = rng.Next(5) switch
        {
            0 => 1 + rng.Next(64),
            1 => 64 + rng.Next(4096),
            2 => 4096 + rng.Next(0x20000),
            3 => 0x20000 + rng.Next(0x20000),
            _ => 0x40000 + rng.Next(0x10000),
        };
        int quantumRaw = Math.Min(raw, 0x40000);
        int body = 1 + rng.Next(Math.Max(1, Math.Min(quantumRaw - 1, 2048)));
        var s = new List<byte> { 0x8C, codec };
        uint qsize = (uint)(body - 1);
        s.Add((byte)(qsize >> 16)); s.Add((byte)(qsize >> 8)); s.Add((byte)qsize);
        var b = new byte[body];
        rng.NextBytes(b);
        if (body >= 3)
        {
            if (rng.Next(2) == 0)
            {
                // LZ chunk header: 0x800000 | mode << 19 | src_used
                int mode = rng.Next(codec == 12 ? 6 : 2);
                uint used = (uint)(body - 3);
                uint hdr = 0x800000u | ((uint)mode << 19) | (used & 0x7FFFF);
                b[0] = (byte)(hdr >> 16); b[1] = (byte)(hdr >> 8); b[2] = (byte)hdr;
            }
            else
            {
                // entropy-only chunk: a Kraken_DecodeBytes block header of a random type with plausible sizes
                b[0] = (byte)((rng.Next(6) << 4) | (rng.Next(2) << 7) | rng.Next(4));
            }
        }
        s.AddRange(b);
        return new Seed("structured", s.ToArray(), raw);
    }
}
