namespace CthExtract.Oodle.Tests.Synth;

/// <summary>Which coder <see cref="SynthEntropy"/> uses for one entropy block.</summary>
internal enum EntropyKind
{
    Raw,
    Rle,
    /// <summary>Huffman, one set of 3 interleaved streams (block type 2), sparse code lengths.</summary>
    Huff3,
    /// <summary>Huffman, two sets of 3 streams (block type 4), sparse code lengths.</summary>
    Huff6,
    /// <summary>Huffman (block type 2) with the gamma-coded ("old, non-sparse") code length format.</summary>
    Huff3Gamma,
    /// <summary>Block type 5: the data split into 2-4 parts, each its own block.</summary>
    Recursive,
    /// <summary>Block type 5 through the multi-array path (num_arrays_in_file == 0).</summary>
    RecursiveMulti,
    /// <summary>Huffman (block type 2) with the Golomb-Rice ("new") code length format.</summary>
    Huff3New,
    /// <summary>Huffman (block type 4) with the Golomb-Rice code length format.</summary>
    Huff6New,
    /// <summary>tANS (block type 1); sparse or Golomb-Rice coded table depending on the symbol count.</summary>
    Tans,
    /// <summary>Block type 5 through the full multi-array format (entropy arrays + intervals).</summary>
    RecursiveFullMulti,
}

/// <summary>
/// Our own encoder for ooz's Kraken_DecodeBytes block format, written from the decoder's source to produce test
/// streams. Each coder falls back to a raw block when it does not shrink the data (the format requires that).
/// </summary>
internal static partial class SynthEntropy
{
    /// <summary>Encodes data as one block. forceLongHeader: the first byte must be &lt; 0x80 (entropy-only chunks and
    /// the first stream of an LZ table need that).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> data, EntropyKind kind, bool forceLongHeader = false, int seed = 0)
    {
        if (data.Length == 0)
            return Raw(data, forceLongHeader);
        byte[]? body = null;
        int type = 0;
        try
        {
            switch (kind)
            {
                case EntropyKind.Rle:
                    body = RleBody(data, seed); type = 3; break;
                case EntropyKind.Huff3:
                    body = HuffBody(data, false, gamma: false); type = 2; break;
                case EntropyKind.Huff6:
                    body = HuffBody(data, true, gamma: false); type = 4; break;
                case EntropyKind.Huff3Gamma:
                    body = HuffBody(data, false, gamma: true); type = 2; break;
                case EntropyKind.Recursive:
                    body = RecursiveBody(data, seed); type = 5; break;
                case EntropyKind.RecursiveMulti:
                    body = RecursiveMultiBody(data, seed); type = 5; break;
                case EntropyKind.Huff3New:
                    body = HuffBody(data, false, gamma: false, newFormat: true, seed); type = 2; break;
                case EntropyKind.Huff6New:
                    body = HuffBody(data, true, gamma: false, newFormat: true, seed); type = 4; break;
                case EntropyKind.Tans:
                    body = TansBody(data, seed); type = 1; break;
                case EntropyKind.RecursiveFullMulti:
                    body = MultiArrayFull(new[] { data.ToArray() }, (EntropyKind)(1 + seed % 3), seed, combined: seed % 2 == 0, numEntropy: 2 + seed % 3);
                    if (body[0] == 0x80) body[0] = 0xC0; // fell back to the simple form: low 7 bits must be >= 2
                    type = 5; break;
            }
        }
        catch (InvalidOperationException)
        {
            body = null; // a constraint of the format could not be met for this data
        }
        if (body == null || body.Length >= data.Length || data.Length > 0x40000 || body.Length > 0x3FFFF)
            return Raw(data, forceLongHeader);
        return WithHeader(type, body, data.Length, forceLongHeader);
    }

    public static byte[] Raw(ReadOnlySpan<byte> data, bool forceLongHeader)
    {
        var o = new List<byte>(data.Length + 3);
        if (!forceLongHeader && data.Length <= 0xFFF)
        {
            o.Add((byte)(0x80 | (data.Length >> 8)));
            o.Add((byte)data.Length);
        }
        else
        {
            if (data.Length > 0x3FFFF) throw new ArgumentException("raw block too large");
            o.Add((byte)(data.Length >> 16));
            o.Add((byte)(data.Length >> 8));
            o.Add((byte)data.Length);
        }
        o.AddRange(data.ToArray());
        return o.ToArray();
    }

    static byte[] WithHeader(int type, byte[] body, int dstSize, bool forceLongHeader)
    {
        var o = new List<byte>(body.Length + 5);
        int srcSize = body.Length;
        if (!forceLongHeader && srcSize <= 0x3FF && dstSize - srcSize - 1 <= 0x3FF)
        {
            uint v = 0x800000u | ((uint)type << 20) | ((uint)(dstSize - srcSize - 1) << 10) | (uint)srcSize;
            o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v);
        }
        else
        {
            uint d1 = (uint)(dstSize - 1);
            o.Add((byte)((type << 4) | (int)((d1 >> 14) & 0xF)));
            uint bits = ((d1 & 0x3FFF) << 18) | (uint)srcSize;
            o.Add((byte)(bits >> 24)); o.Add((byte)(bits >> 16)); o.Add((byte)(bits >> 8)); o.Add((byte)bits);
        }
        o.AddRange(body);
        return o.ToArray();
    }

    // ------------------------------------------------------------------------------------------------ RLE

    /// <summary>Krak_DecodeRLE: literal bytes (and new run bytes) from the front, commands from the back.</summary>
    static byte[] RleBody(ReadOnlySpan<byte> data, int seed)
    {
        if (data.IndexOfAnyExcept(data[0]) < 0)
            return new[] { data[0] }; // a 1-byte RLE block is a memset
        var front = new List<byte>();
        var cmds = new List<byte[]>(); // decode order
        var rng = new Random(seed);
        byte rle = 0;
        int n = data.Length, i = 0, litStart = 0;
        while (i < n)
        {
            int r = 1;
            while (i + r < n && data[i + r] == data[i]) r++;
            if (r >= 6)
            {
                EmitRun(data[litStart..i], data[i], r);
                i += r;
                litStart = i;
            }
            else
            {
                i += r;
            }
        }
        // trailing literals
        var lits = data[litStart..n];
        int pos = 0;
        while (lits.Length - pos >= 64)
        {
            int m = Math.Min((lits.Length - pos) / 64, 0x700);
            cmds.Add(Word((uint)(m + 511)));
            front.AddRange(lits.Slice(pos, 64 * m).ToArray());
            pos += 64 * m;
        }
        int rem = lits.Length - pos;
        if (rem == 15 && rng.Next(2) == 0)
            cmds.Add(new byte[] { 0x00 }); // cmd 0: copy 15, rle 0
        else if (rem > 0)
            cmds.Add(Word((uint)(rem + 4096)));
        front.AddRange(lits[pos..].ToArray());

        var full = new List<byte>(front);
        for (int k = cmds.Count - 1; k >= 0; k--)
            full.AddRange(cmds[k]);
        if (seed % 2 == 1 && front.Count > 16)
        {
            // src[0] != 0: the first part of the command buffer is itself an entropy block (here: the literals),
            // the rest follows raw
            var prefix = Encode(full.GetRange(0, front.Count).ToArray(), seed % 4 == 1 ? EntropyKind.Huff3 : EntropyKind.Raw, false, seed + 1);
            if (prefix[0] != 0)
            {
                var b2 = new List<byte>(prefix);
                b2.AddRange(full.GetRange(front.Count, full.Count - front.Count));
                return b2.ToArray();
            }
        }
        var body = new List<byte> { 0x00 };
        body.AddRange(full);
        return body.ToArray();

        void EmitRun(ReadOnlySpan<byte> l, byte b, int run)
        {
            if (b != rle)
            {
                cmds.Add(new byte[] { 0x01 });
                front.Add(b);
                rle = b;
            }
            int p = 0;
            while (l.Length - p >= 64)
            {
                int m = Math.Min((l.Length - p) / 64, 0x700);
                cmds.Add(Word((uint)(m + 511)));
                front.AddRange(l.Slice(p, 64 * m).ToArray());
                p += 64 * m;
            }
            int copy = l.Length - p;
            int k1 = Math.Min(run, 127);
            if (copy <= 15 && k1 >= 3 && k1 <= 15 && rng.Next(2) == 0)
                cmds.Add(new byte[] { (byte)((k1 << 4) | (15 - copy)) });
            else
                cmds.Add(Word((uint)(((k1 << 6) | copy) + 4096)));
            front.AddRange(l[p..].ToArray());
            run -= k1;
            while (run >= 128)
            {
                int m = Math.Min(run / 128, 0x700);
                cmds.Add(Word((uint)(m + 0x8FF)));
                run -= 128 * m;
            }
            while (run > 0)
            {
                int k = Math.Min(run, 127);
                cmds.Add(Word((uint)((k << 6) + 4096)));
                run -= k;
            }
        }

        static byte[] Word(uint w) => new[] { (byte)w, (byte)(w >> 8) }; // little-endian u16, read from the end
    }

    // ------------------------------------------------------------------------------------------------ Huffman

    /// <summary>Length-limited (11 bit) Huffman code lengths via package-merge; always at least 2 symbols.</summary>
    public static int[] CodeLengths(ReadOnlySpan<byte> data, int maxLen = 11)
    {
        var freq = new long[256];
        foreach (var b in data) freq[b]++;
        var syms = Enumerable.Range(0, 256).Where(s => freq[s] > 0).ToList();
        if (syms.Count < 2)
        {
            int extra = syms.Count == 1 && syms[0] == 0 ? 1 : 0;
            if (!syms.Contains(extra)) syms.Add(extra);
            if (syms.Count < 2) syms.Add(extra == 0 ? 1 : 2);
            syms.Sort();
        }
        int n = syms.Count;
        var leaves = syms.Select((s, idx) => (w: Math.Max(1, freq[s]), c: OneHot(idx, n))).OrderBy(x => x.w).ToList();
        var cur = leaves.ToList();
        for (int level = 1; level < maxLen; level++)
        {
            var packages = new List<(long w, int[] c)>();
            for (int k = 0; k + 1 < cur.Count; k += 2)
                packages.Add((cur[k].w + cur[k + 1].w, Add(cur[k].c, cur[k + 1].c)));
            cur = leaves.Concat(packages).OrderBy(x => x.w).ToList(); // OrderBy is stable: leaves first on ties
        }
        var lens = new int[256];
        foreach (var item in cur.Take(2 * n - 2))
            for (int idx = 0; idx < n; idx++)
                lens[syms[idx]] += item.c[idx];
        return lens;

        static int[] OneHot(int i, int n) { var a = new int[n]; a[i] = 1; return a; }
        static int[] Add(int[] a, int[] b) { var r = new int[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = a[i] + b[i]; return r; }
    }

    /// <summary>Canonical codes in the decoder's order: by length, then by symbol (the order the lengths are read).</summary>
    static uint[] CanonicalCodes(int[] lens)
    {
        var codes = new uint[256];
        int slot = 0;
        for (int l = 1; l <= 11; l++)
            for (int s = 0; s < 256; s++)
                if (lens[s] == l)
                {
                    codes[s] = (uint)(slot >> (11 - l));
                    slot += 1 << (11 - l);
                }
        if (slot != 2048) throw new InvalidOperationException("code lengths are not a complete prefix code");
        return codes;
    }

    static byte[] HuffBody(ReadOnlySpan<byte> data, bool sixStreams, bool gamma, bool newFormat = false, int seed = 0)
    {
        var lens = CodeLengths(data);
        var codes = CanonicalCodes(lens);
        var hdr = new MsbBitWriter();
        int numSyms = lens.Count(l => l > 0);
        if (newFormat)
        {
            hdr.WriteBit(1); hdr.WriteBit(0); // Type12: new code length format
            WriteNewCodeLengths(hdr, lens, forced: seed & 3);
        }
        else if (!gamma && numSyms <= 255)
        {
            hdr.WriteBit(0); // Type12: old code length format
            hdr.WriteBit(0); // sparse
            hdr.Write((uint)numSyms, 8);
            int maxLen = lens.Max();
            int codelenBits = maxLen <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(maxLen - 1));
            if (codelenBits > 4) throw new InvalidOperationException();
            hdr.Write((uint)codelenBits, 3);
            for (int s = 0; s < 256; s++)
            {
                if (lens[s] == 0) continue;
                hdr.Write((uint)s, 8);
                hdr.Write((uint)(lens[s] - 1), codelenBits);
            }
        }
        else
        {
            hdr.WriteBit(0); // Type12: old code length format
            WriteGammaCodeLengths(hdr, lens);
        }
        var o = new List<byte>(hdr.ToArray());
        if (!sixStreams)
        {
            var (a, b, c) = Streams(data, lens, codes);
            if (a.Length + b.Length + c.Length < 1) throw new InvalidOperationException();
            if (a.Length > 0xFFFF) throw new InvalidOperationException();
            o.Add((byte)a.Length); o.Add((byte)(a.Length >> 8));
            o.AddRange(a); o.AddRange(b); o.AddRange(c);
        }
        else
        {
            int half = (data.Length + 1) >> 1;
            var (a1, b1, c1) = Streams(data[..half], lens, codes);
            var (a2, b2, c2) = Streams(data[half..], lens, codes);
            if (a1.Length > 0xFFFF || a2.Length > 0xFFFF) throw new InvalidOperationException();
            if (b1.Length + c1.Length < 2 || b2.Length + c2.Length < 2) throw new InvalidOperationException();
            int splitMid = 2 + a1.Length + b1.Length + c1.Length;
            o.Add((byte)splitMid); o.Add((byte)(splitMid >> 8)); o.Add((byte)(splitMid >> 16));
            o.Add((byte)a1.Length); o.Add((byte)(a1.Length >> 8));
            o.AddRange(a1); o.AddRange(b1); o.AddRange(c1);
            o.Add((byte)a2.Length); o.Add((byte)(a2.Length >> 8));
            o.AddRange(a2); o.AddRange(b2); o.AddRange(c2);
        }
        return o.ToArray();
    }

    /// <summary>Symbol i goes to stream A (forward) when i%3==0, C (backward from the end) when 1, B (forward from the
    /// middle) when 2. Returns A, B, and C already byte-reversed.</summary>
    static (byte[] A, byte[] B, byte[] C) Streams(ReadOnlySpan<byte> data, int[] lens, uint[] codes)
    {
        var a = new LsbBitWriter(); var b = new LsbBitWriter(); var c = new LsbBitWriter();
        for (int i = 0; i < data.Length; i++)
        {
            int s = data[i];
            var w = (i % 3) switch { 0 => a, 1 => c, _ => b };
            w.WriteCodeMsbFirst(codes[s], lens[s]);
        }
        var cr = c.ToArray();
        Array.Reverse(cr);
        return (a.ToArray(), b.ToArray(), cr);
    }

    /// <summary>The "old, non-sparse" code length format: runs of absent symbols and of present symbols, each length
    /// coded relative to a running average (Huff_ReadCodeLengthsOld).</summary>
    static void WriteGammaCodeLengths(MsbBitWriter w, int[] lens)
    {
        w.WriteBit(1); // non-sparse
        const int forced = 1;
        w.Write(forced, 2);
        int avgX4 = 32;
        int sym = 0;
        bool first = true;
        while (sym < 256)
        {
            int z = 0;
            while (sym + z < 256 && lens[sym + z] == 0) z++;
            if (first)
            {
                w.WriteBit(z == 0 ? 1 : 0); // 1: skip the initial zero run
                first = false;
            }
            if (z > 0 || sym > 0)
            {
                if (z == 0) throw new InvalidOperationException("present-symbol runs must be separated by absent ones");
                WriteGamma(w, z + 1);
                sym += z;
                if (sym >= 256) break;
            }
            int nrun = 0;
            while (sym + nrun < 256 && lens[sym + nrun] != 0) nrun++;
            WriteGamma(w, nrun + 1);
            for (int k = 0; k < nrun; k++)
            {
                int pred = (avgX4 + 2) >> 2;
                int delta = lens[sym + k] - pred;
                int v = delta >= 0 ? 2 * delta : -2 * delta - 1;
                int lz = v >> forced;
                if (lz > (20 >> forced)) throw new InvalidOperationException("code length delta too large");
                for (int q = 0; q < lz; q++) w.WriteBit(0);
                w.WriteBit(1);
                w.Write((uint)(v & ((1 << forced) - 1)), forced);
                avgX4 = lens[sym + k] + ((3 * avgX4 + 2) >> 2);
            }
            sym += nrun;
        }
    }

    /// <summary>v &gt;= 2: floor(log2 v) - 1 zero bits, then v in floor(log2 v) + 1 bits (decoded as v - 1).</summary>
    static void WriteGamma(MsbBitWriter w, int v)
    {
        int nb = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)v); // bits in v
        int lz = nb - 2;
        if (lz < 0 || lz > 7) throw new InvalidOperationException("gamma value out of range");
        for (int i = 0; i < lz; i++) w.WriteBit(0);
        w.Write((uint)v, nb);
    }

    // ------------------------------------------------------------------------------------------------ recursive

    static byte[] RecursiveBody(ReadOnlySpan<byte> data, int seed)
    {
        var rng = new Random(seed);
        int parts = Math.Min(2 + rng.Next(3), data.Length);
        if (parts < 2) throw new InvalidOperationException();
        var o = new List<byte> { (byte)parts };
        int pos = 0;
        for (int p = 0; p < parts; p++)
        {
            int len = p == parts - 1 ? data.Length - pos : Math.Max(1, (data.Length - pos) / (parts - p) + rng.Next(-3, 4));
            len = Math.Clamp(len, 1, data.Length - pos - (parts - 1 - p));
            var kind = (EntropyKind)rng.Next(4); // Raw, Rle, Huff3, Huff6
            o.AddRange(Encode(data.Slice(pos, len), kind, false, seed + p + 1));
            pos += len;
        }
        if (o.Count < 6) throw new InvalidOperationException();
        return o.ToArray();
    }

    static byte[] RecursiveMultiBody(ReadOnlySpan<byte> data, int seed)
    {
        // 0xC0: bit 7 = multi-array, low 7 bits >= 2 (checked first), low 6 bits = 0 entropy arrays -> one plain block
        var o = new List<byte> { 0xC0 };
        o.AddRange(Encode(data, (EntropyKind)(1 + seed % 3), false, seed + 1));
        if (o.Count < 6) throw new InvalidOperationException();
        return o.ToArray();
    }

    /// <summary>Kraken_DecodeMultiArray in its simple form (num_arrays_in_file == 0): one plain block per array.</summary>
    public static byte[] MultiArraySimple(IReadOnlyList<byte[]> arrays, EntropyKind kind, int seed)
    {
        var o = new List<byte> { 0x80 };
        for (int i = 0; i < arrays.Count; i++)
            o.AddRange(Encode(arrays[i], kind, false, seed + i));
        while (o.Count < 4) o.Add(0); // never reached for >= 2 arrays (each block has a 2-byte header)
        return o.ToArray();
    }
}
