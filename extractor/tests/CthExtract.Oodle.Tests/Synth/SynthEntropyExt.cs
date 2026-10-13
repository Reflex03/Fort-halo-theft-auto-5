using System.Numerics;

namespace CthExtract.Oodle.Tests.Synth;

/// <summary>The less common block formats: Golomb-Rice code lengths, tANS, and the full multi-array format.</summary>
internal static partial class SynthEntropy
{
    static int Log2(int v) => 31 - BitOperations.LeadingZeroCount((uint)v);

    static void WriteUnary(MsbBitWriter w, int k)
    {
        for (int i = 0; i < k; i++) w.WriteBit(0);
        w.WriteBit(1);
    }

    /// <summary>BitReader_ReadFluff: P in [0, 2*min(257-n, n)) as a truncated binary code. Nothing for n == 256.</summary>
    static void WriteFluff(MsbBitWriter w, int numSymbols, int P)
    {
        if (numSymbols == 256)
        {
            if (P != 0) throw new InvalidOperationException();
            return;
        }
        int x = Math.Min(257 - numSymbols, numSymbols) * 2;
        if (P < 0 || P >= x) throw new InvalidOperationException("too many ranges");
        int y = Log2(x - 1) + 1;
        int z = (1 << y) - x;
        if (P < z) w.Write((uint)P, y - 1);
        else w.Write((uint)(P + z), y);
    }

    /// <summary>The present symbols (ascending) as runs; returns the fluff count P and the per-range unary values and
    /// their extra bits, in the order Huff_ConvertToRanges reads them.</summary>
    static (int P, List<int> vals, List<(uint v, int n)> extra) Ranges(IReadOnlyList<int> syms)
    {
        var runs = new List<(int start, int count)>();
        foreach (var s in syms)
        {
            if (runs.Count > 0 && runs[^1].start + runs[^1].count == s) runs[^1] = (runs[^1].start, runs[^1].count + 1);
            else runs.Add((s, 1));
        }
        bool initialGap = syms[0] > 0;
        int P = 2 * (runs.Count - 1) + (initialGap ? 1 : 0);
        var vals = new List<int>();
        var extra = new List<(uint, int)>();
        if (initialGap) Gap(syms[0]);
        for (int r = 0; r + 1 < runs.Count; r++)
        {
            int num = runs[r].count;
            int k = Log2(num);
            if (k >= 9) throw new InvalidOperationException();
            vals.Add(k); extra.Add(((uint)(num - (1 << k)), k));
            Gap(runs[r + 1].start - (runs[r].start + runs[r].count));
        }
        return (P, vals, extra);

        void Gap(int g)
        {
            int v = Log2(g + 1) - 1;
            if (v < 0 || v >= 8) throw new InvalidOperationException();
            vals.Add(v); extra.Add(((uint)(g + 1 - (1 << (v + 1))), v + 1));
        }
    }

    /// <summary>Huff_ReadCodeLengthsNew: forced bits, symbol count, ranges, Golomb-Rice coded length deltas.</summary>
    static void WriteNewCodeLengths(MsbBitWriter w, int[] lens, int forced)
    {
        var syms = Enumerable.Range(0, 256).Where(s => lens[s] > 0).ToList();
        int n = syms.Count;
        w.Write((uint)forced, 2);
        w.Write((uint)(n - 1), 8);
        var (P, rangeVals, rangeExtra) = Ranges(syms);
        WriteFluff(w, n, P);
        var hi = new List<int>();
        var lo = new List<int>();
        uint rs = 0x1e;
        foreach (var s in syms)
        {
            int pred = (int)(rs >> 2) + 1;
            int delta = lens[s] - pred;
            int zz = delta >= 0 ? 2 * delta : -2 * delta - 1;
            if (zz > 255) throw new InvalidOperationException();
            hi.Add(zz >> forced);
            lo.Add(zz & ((1 << forced) - 1));
            rs += (uint)delta;
        }
        foreach (var v in hi) WriteUnary(w, v);
        foreach (var v in rangeVals) WriteUnary(w, v);
        foreach (var v in lo) w.Write((uint)v, forced);
        foreach (var (v, nb) in rangeExtra) w.Write(v, nb);
    }

    // ------------------------------------------------------------------------------------------------ tANS

    struct TansEnt { public int Symbol, BitsX, W; }

    /// <summary>Weights summing to L, at least 1 per present symbol, roughly proportional to the frequencies.</summary>
    static int[] TansWeights(long[] freq, int L)
    {
        var syms = Enumerable.Range(0, 256).Where(s => freq[s] > 0).ToList();
        long total = syms.Sum(s => freq[s]);
        var w = new int[256];
        int sum = 0;
        foreach (var s in syms) { w[s] = Math.Max(1, (int)(freq[s] * L / total)); sum += w[s]; }
        // fix the sum by adjusting the biggest weights
        while (sum != L)
        {
            int big = syms.OrderByDescending(s => w[s]).First();
            if (sum > L) { int d = Math.Min(sum - L, w[big] - 1); if (d == 0) throw new InvalidOperationException(); w[big] -= d; sum -= d; }
            else { w[big] += L - sum; sum = L; }
        }
        return w;
    }

    /// <summary>Tans_InitLut, as the decoder builds it (A = weight-1 symbols, B = (sym &lt;&lt; 16) + weight, both
    /// ascending).</summary>
    static TansEnt[] TansLut(int[] weights, int lBits)
    {
        int L = 1 << lBits;
        var A = Enumerable.Range(0, 256).Where(s => weights[s] == 1).ToArray();
        var B = Enumerable.Range(0, 256).Where(s => weights[s] >= 2).Select(s => (s << 16) + weights[s]).ToArray();
        var lut = new TansEnt[L];
        int slotsLeft = L - A.Length;
        int sa = slotsLeft >> 2;
        var ptr = new int[4];
        ptr[0] = 0;
        int sb = sa + ((slotsLeft & 3) > 0 ? 1 : 0); ptr[1] = sb;
        sb += sa + ((slotsLeft & 3) > 1 ? 1 : 0); ptr[2] = sb;
        sb += sa + ((slotsLeft & 3) > 2 ? 1 : 0); ptr[3] = sb;
        for (int i = 0; i < A.Length; i++)
            lut[slotsLeft + i] = new TansEnt { Symbol = A[i], BitsX = lBits, W = 0 };
        int weightsSum = 0;
        foreach (var b in B)
        {
            int weight = b & 0xffff, symbol = b >> 16;
            if (weight > 4)
            {
                int symBits = Log2(weight);
                int Z = lBits - symBits;
                var le = new TansEnt { Symbol = symbol, BitsX = Z, W = (L - 1) & (weight << Z) };
                int add = 1 << Z;
                int X = (1 << (symBits + 1)) - weight;
                for (int j = 0; j < 4; j++)
                {
                    int Y = (weight + ((weightsSum - j - 1) & 3)) >> 2;
                    if (X >= Y)
                    {
                        for (int n = Y; n != 0; n--) { lut[ptr[j]++] = le; le.W += add; }
                        X -= Y;
                    }
                    else
                    {
                        for (int n = X; n != 0; n--) { lut[ptr[j]++] = le; le.W += add; }
                        Z--; add >>= 1; le.BitsX = Z; le.W = 0;
                        for (int n = Y - X; n != 0; n--) { lut[ptr[j]++] = le; le.W += add; }
                        X = weight;
                    }
                }
            }
            else
            {
                uint bits = ((1u << weight) - 1) << (weightsSum & 3);
                bits |= bits >> 4;
                int ww = weight;
                for (int n = weight; n != 0; n--)
                {
                    int idx = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    int wb = Log2(ww);
                    lut[ptr[idx]++] = new TansEnt { Symbol = symbol, BitsX = lBits - wb, W = (L - 1) & (ww++ << (lBits - wb)) };
                }
            }
            weightsSum += weight;
        }
        return lut;
    }

    /// <summary>Krak_DecodeTans: table, then 5 interleaved states over a forward and a backward LSB-first stream;
    /// the last 5 output bytes are the final states.</summary>
    static byte[] TansBody(ReadOnlySpan<byte> data, int seed)
    {
        int n = data.Length;
        if (n < 5) throw new InvalidOperationException();
        int lBits = 8 + (seed & 3);
        int L = 1 << lBits;
        var freq = new long[256];
        for (int i = 0; i < n - 5; i++) freq[data[i]]++;
        if (freq.Count(f => f > 0) < 2)
        {
            // the table needs >= 2 symbols: add a neighbour with weight 1
            int s0 = Array.FindIndex(freq, f => f > 0);
            freq[s0 < 0 ? 0 : (s0 + 1) & 255] = 1;
            if (s0 < 0) freq[1] = 1;
        }
        var weights = TansWeights(freq, L);
        var syms = Enumerable.Range(0, 256).Where(s => weights[s] > 0).ToList();
        var w = new MsbBitWriter();
        w.WriteBit(0); // reserved
        w.Write((uint)(lBits - 8), 2);
        if (syms.Count <= 9 && (seed & 4) == 0)
        {
            // sparse table: up to 8 delta-coded weights (non-decreasing) + the last symbol takes the rest
            var order = syms.OrderBy(s => weights[s]).ThenBy(s => s).ToList();
            var last = order[^1];
            var coded = order.Take(order.Count - 1).ToList();
            if (coded.Count == 0) throw new InvalidOperationException();
            int prev = 0, maxDelta = 0;
            foreach (var s in coded) { maxDelta = Math.Max(maxDelta, weights[s] - prev); prev = weights[s]; }
            int mdb = Math.Max(1, Log2(Math.Max(1, maxDelta)) + 1);
            if (mdb > lBits) throw new InvalidOperationException();
            w.WriteBit(0);
            w.Write((uint)(coded.Count - 1), 3);
            w.Write((uint)mdb, Log2(lBits) + 1);
            prev = 0;
            foreach (var s in coded)
            {
                w.Write((uint)s, 8);
                w.Write((uint)(weights[s] - prev), mdb);
                prev = weights[s];
            }
            w.Write((uint)last, 8);
            if (weights[last] < prev || weights[last] <= 1) throw new InvalidOperationException();
        }
        else
        {
            int Q = seed % 4;
            w.WriteBit(1);
            w.Write((uint)Q, 3);
            w.Write((uint)(syms.Count - 1), 8);
            var (P, rangeVals, rangeExtra) = Ranges(syms);
            WriteFluff(w, syms.Count, P);
            var rice = new List<int>();
            var wextra = new List<(uint, int)>();
            int average = 6;
            foreach (var s in syms)
            {
                int t = weights[s] - 1;
                int a = average >> 2, limit = 2 * a;
                int v0 = t <= limit ? (t - a >= 0 ? 2 * (t - a) : -2 * (t - a) - 1) : t;
                int lim2 = Math.Min(limit, t);
                average += lim2 - a;
                int nextra = Log2(v0 + (1 << Q));
                if (nextra > 15) throw new InvalidOperationException();
                rice.Add(nextra - Q);
                wextra.Add(((uint)(v0 + (1 << Q) - (1 << nextra)), nextra));
            }
            foreach (var v in rice) WriteUnary(w, v);
            foreach (var v in rangeVals) WriteUnary(w, v);
            foreach (var (v, nb) in rangeExtra) w.Write(v, nb);
            foreach (var (v, nb) in wextra) w.Write(v, nb);
        }

        // reverse encode with the decoder's own table
        var lut = TansLut(weights, lBits);
        var enc = new Dictionary<int, int[]>();
        foreach (var s in syms)
        {
            var m = new int[L];
            Array.Fill(m, -1);
            for (int st = 0; st < L; st++)
            {
                if (lut[st].Symbol != s) continue;
                for (int k = 0; k < (1 << lut[st].BitsX); k++)
                {
                    int next = lut[st].W + k;
                    if (next >= L || m[next] != -1) throw new InvalidOperationException("tANS table does not partition the states");
                    m[next] = st;
                }
            }
            enc[s] = m;
        }
        var states = new int[5];
        for (int j = 0; j < 5; j++) states[j] = data[n - 5 + j];
        var fw = new List<(uint, int)>();
        var bw = new List<(uint, int)>();
        for (int k = n - 6; k >= 0; k--)
        {
            int j = k % 5;
            var m = enc[data[k]];
            int st = m[states[j]];
            if (st < 0) throw new InvalidOperationException("unreachable state");
            ((k / 5) % 2 == 0 ? fw : bw).Add(((uint)(states[j] - lut[st].W), lut[st].BitsX));
            states[j] = st;
        }
        fw.Reverse(); bw.Reverse();
        var f = new LsbBitWriter();
        var b = new LsbBitWriter();
        f.WriteLsbFirst((uint)states[0], lBits);
        b.WriteLsbFirst((uint)states[1], lBits);
        f.WriteLsbFirst((uint)states[2], lBits);
        b.WriteLsbFirst((uint)states[3], lBits);
        f.WriteLsbFirst((uint)states[4], lBits);
        foreach (var (v, nb) in fw) f.WriteLsbFirst(v, nb);
        foreach (var (v, nb) in bw) b.WriteLsbFirst(v, nb);
        var o = new List<byte>(w.ToArray());
        o.AddRange(f.ToArray());
        var bb = b.ToArray();
        Array.Reverse(bb);
        o.AddRange(bb);
        if (o.Count < 8) throw new InvalidOperationException();
        return o.ToArray();
    }

    // ------------------------------------------------------------------------------------------------ multi-array

    /// <summary>Kraken_DecodeMultiArray with numEntropy (1..63) entropy arrays: the output arrays are cut into
    /// intervals, each taken from one entropy array; interval sources and lengths (2^k + k extra bits, read from a
    /// forward and a backward stream) follow. combined: one index byte holds both the source and k.</summary>
    public static byte[] MultiArrayFull(IReadOnlyList<byte[]> arrays, EntropyKind kind, int seed, bool combined, int numEntropy)
    {
        var rng = new Random(seed);
        var entropy = Enumerable.Range(0, numEntropy).Select(_ => new List<byte>()).ToArray();
        var idx = new List<byte>();
        var lens = new List<int>();
        foreach (var arr in arrays)
        {
            int pos = 0;
            while (pos < arr.Length)
            {
                int chunk = Math.Min(arr.Length - pos, 1 + rng.Next(rng.Next(4) == 0 ? 2000 : 200));
                if (combined) chunk = Math.Min(chunk, 0xFFFF);
                int s = 1 + rng.Next(numEntropy);
                entropy[s - 1].AddRange(arr.AsSpan(pos, chunk).ToArray());
                idx.Add((byte)s);
                lens.Add(chunk);
                pos += chunk;
            }
            idx.Add(0);
            if (combined) lens.Add(1 + rng.Next(3)); // the terminator also has a (unused) length slot
        }
        int total = entropy.Sum(e => e.Count);
        if (idx.Count > total || idx.Count - arrays.Count < 1)
            return MultiArraySimple(arrays, kind, seed); // the index block may not be larger than the data

        var f = new MsbBitWriter();
        var b = new MsbBitWriter();
        var lenlog2 = new List<byte>();
        for (int i = 0; i < lens.Count; i++)
        {
            int k = Log2(lens[i]);
            lenlog2.Add((byte)k);
            (i % 2 == 0 ? f : b).Write((uint)(lens[i] - (1 << k)), k);
        }
        var fb = f.ToArray();
        var bb = b.ToArray();
        Array.Reverse(bb);
        int complen = fb.Length + bb.Length + 4; // a little slack after the streams is allowed (never read)
        if (complen > 0x3FFF) return MultiArraySimple(arrays, kind, seed);

        var o = new List<byte> { (byte)(0x80 | numEntropy) };
        for (int i = 0; i < numEntropy; i++)
            o.AddRange(Encode(entropy[i].ToArray(), kind, false, seed + i + 1));
        int Q = (combined ? 0x8000 : 0) | complen;
        o.Add((byte)Q); o.Add((byte)(Q >> 8));
        if (combined)
        {
            var comb = new byte[idx.Count];
            for (int i = 0; i < idx.Count; i++) comb[i] = (byte)((lenlog2[i] << 4) | idx[i]);
            o.AddRange(Encode(comb, EntropyKind.Raw, forceLongHeader: seed % 3 == 0, seed + 90));
        }
        else
        {
            o.AddRange(Encode(idx.ToArray(), (EntropyKind)(seed % 3), forceLongHeader: seed % 3 == 1, seed + 91));
            o.AddRange(Encode(lenlog2.ToArray(), (EntropyKind)(seed % 2), false, seed + 92));
        }
        o.AddRange(fb);
        o.AddRange(new byte[complen - fb.Length - bb.Length]);
        o.AddRange(bb);
        return o.ToArray();
    }
}
