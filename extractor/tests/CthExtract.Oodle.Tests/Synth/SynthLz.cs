namespace CthExtract.Oodle.Tests.Synth;

/// <summary>Greedy hash-chain match finder over the whole output (the decoder's window).</summary>
internal sealed class MatchFinder
{
    readonly byte[] _d;
    readonly int[] _head;
    readonly int[] _prev;
    readonly int _depth;
    readonly int _shift;
    int _inserted;

    public MatchFinder(byte[] data, int depth)
    {
        _d = data;
        _prev = new int[data.Length];
        _depth = depth;
        int bits = data.Length > (1 << 20) ? 24 : 16; // big inputs: fewer collisions, so old matches stay reachable
        _shift = 32 - bits;
        _head = new int[1 << bits];
        Array.Fill(_head, -1);
    }

    int Hash(int p) => (int)(((uint)(_d[p] | _d[p + 1] << 8 | _d[p + 2] << 16 | _d[p + 3] << 24) * 2654435761u) >> _shift);

    /// <summary>Indexes every position below p.</summary>
    public void InsertUpTo(int p)
    {
        for (; _inserted < p; _inserted++)
        {
            if (_inserted + 4 > _d.Length) continue;
            int h = Hash(_inserted);
            _prev[_inserted] = _head[h];
            _head[h] = _inserted;
        }
    }

    public int MatchLen(int p, int q, int limit)
    {
        int n = 0;
        while (p + n < limit && _d[p + n] == _d[q + n]) n++;
        return n;
    }

    /// <summary>Longest match for position p ending at or before limit, with minDist &lt;= distance &lt;= maxDist.</summary>
    public (int len, int dist) Find(int p, int limit, int minDist, int maxDist, int windowStart)
    {
        InsertUpTo(p);
        if (p + 4 > _d.Length || p + 4 > limit) return (0, 0);
        int best = 0, bestDist = 0;
        int cand = _head[Hash(p)];
        for (int k = 0; k < _depth && cand >= 0; k++, cand = _prev[cand])
        {
            int dist = p - cand;
            if (cand < windowStart || dist > maxDist) break;
            if (dist < minDist) continue;
            int len = MatchLen(p, cand, limit);
            if (len > best) { best = len; bestDist = dist; }
        }
        return (best, bestDist);
    }
}

/// <summary>What <see cref="SynthOodle"/> emits. All choices are ours; the point is to reach every decoder path.</summary>
internal sealed record SynthOptions
{
    /// <summary>6 Kraken, 10 Mermaid/Selkie, 12 Leviathan.</summary>
    public int Codec { get; init; } = 6;
    /// <summary>Kraken/Mermaid: 0 delta literals, 1 raw literals. Leviathan: chunk type 0..5.</summary>
    public int LzMode { get; init; } = 1;
    public EntropyKind Literals { get; init; } = EntropyKind.Huff3;
    public EntropyKind Commands { get; init; } = EntropyKind.Raw;
    public EntropyKind Other { get; init; } = EntropyKind.Raw;
    /// <summary>Kraken/Leviathan offset coding: 0 traditional, 1 scaled with scale 1, n scaled with scale n.</summary>
    public int OffsetScale { get; init; }
    /// <summary>Leviathan: commands in 8 position-selected streams.</summary>
    public bool MultiCmd { get; init; }
    /// <summary>Mermaid: off16 distances entropy-coded as hi/lo byte arrays.</summary>
    public bool Off16Entropy { get; init; }
    public int MinMatch { get; init; } = 4;
    public int SearchDepth { get; init; } = 16;
    /// <summary>0 = LZ chunks; 1 = entropy-only chunks; 2 = stored chunks; 3 = mix per chunk.</summary>
    public int ChunkKind { get; init; }
    /// <summary>Quanta whose bytes are all equal become memset quanta.</summary>
    public bool Memset { get; init; } = true;
    /// <summary>Emit uncompressed-block headers for every quantum.</summary>
    public bool Uncompressed { get; init; }
    /// <summary>Set the checksum flag (the 24-bit checksum written is this value).</summary>
    public int? Checksum { get; init; }
    /// <summary>Allow an entropy-only chunk to be a raw (type 0) block. ooz never copies such a block to the
    /// output (the C# port does), so streams using it are only valid for the port.</summary>
    public bool AllowRawEntropyChunk { get; init; }
    /// <summary>Overrides <see cref="ChunkKind"/> for the first chunk of the stream.</summary>
    public int? FirstChunkKind { get; init; }
    /// <summary>Leviathan literal/command arrays use the full multi-array format (entropy arrays + intervals)
    /// instead of one block per array.</summary>
    public bool FullMultiArray { get; init; }
    /// <summary>Mermaid: encode every match that starts before the 64 KiB half as a far (off32) match.</summary>
    public bool PreferFar { get; init; }
    /// <summary>Match finder window (largest distance searched).</summary>
    public int MaxDistance { get; init; } = 1 << 22;
    public int Seed { get; init; }
}

/// <summary>
/// Our own Oodle-format encoder (Kraken, Mermaid/Selkie and Leviathan bitstreams), written from ooz's decoder
/// source purely to produce test streams: greedy matching, simple entropy coders. Output is verified against both
/// the C# port and (locally) the native ooz decoder.
/// </summary>
internal static class SynthOodle
{
    public static byte[] Compress(byte[] data, SynthOptions o)
    {
        var rng = new Random(o.Seed);
        var mf = new MatchFinder(data, o.SearchDepth);
        var outp = new List<byte>();
        for (int q = 0; q < data.Length; q += 0x40000)
        {
            int qlen = Math.Min(0x40000, data.Length - q);
            byte codec = (byte)(o.Codec | (o.Checksum.HasValue ? 0x80 : 0));
            if (o.Uncompressed)
            {
                outp.Add(0xCC); outp.Add(codec);
                outp.AddRange(data.AsSpan(q, qlen).ToArray());
                continue;
            }
            outp.Add(0x8C); outp.Add(codec);
            var qd = data.AsSpan(q, qlen);
            if (o.Memset && qd.IndexOfAnyExcept(qd[0]) < 0)
            {
                outp.Add(0x07); outp.Add(0xFF); outp.Add(0xFF); outp.Add(qd[0]);
                continue;
            }
            var body = new List<byte>();
            for (int c = q; c < q + qlen; c += 0x20000)
            {
                int ce = Math.Min(c + 0x20000, q + qlen);
                body.AddRange(Chunk(data, c, ce, o, mf, rng));
            }
            if (body.Count >= qlen)
            {
                if (qlen > 0x3FFFF)
                {
                    // a full 256 KiB quantum cannot be stored (its size would be the 0x3FFFF escape): uncompressed block
                    outp[^2] = 0xCC;
                    outp.AddRange(qd.ToArray());
                    continue;
                }
                // stored quantum: compressed_size == dst_bytes_left
                body.Clear();
                body.AddRange(qd.ToArray());
            }
            uint v = (uint)(body.Count - 1);
            outp.Add((byte)(v >> 16)); outp.Add((byte)(v >> 8)); outp.Add((byte)v);
            if (o.Checksum.HasValue)
            {
                uint cs = (uint)o.Checksum.Value;
                outp.Add((byte)(cs >> 16)); outp.Add((byte)(cs >> 8)); outp.Add((byte)cs);
            }
            outp.AddRange(body);
        }
        return outp.ToArray();
    }

    static byte[] Chunk(byte[] d, int cs, int ce, SynthOptions o, MatchFinder mf, Random rng)
    {
        int kind = cs == 0 && o.FirstChunkKind.HasValue ? o.FirstChunkKind.Value : o.ChunkKind == 3 ? rng.Next(3) : o.ChunkKind;
        int len = ce - cs;
        byte[]? lz = null;
        if (kind == 0)
        {
            lz = o.Codec switch
            {
                6 => KrakenChunk(d, cs, ce, o, mf, rng),
                10 => MermaidChunk(d, cs, ce, o, mf, rng),
                _ => LeviathanChunk(d, cs, ce, o, mf, rng),
            };
            if (lz != null && lz.Length < len)
            {
                uint hdr = 0x800000u | ((uint)o.LzMode << 19) | (uint)lz.Length;
                return Concat(new[] { (byte)(hdr >> 16), (byte)(hdr >> 8), (byte)hdr }, lz);
            }
            mf.InsertUpTo(ce);
        }
        else
        {
            mf.InsertUpTo(ce);
        }
        if (kind == 1 || (kind == 0 && lz == null))
        {
            // entropy-only chunk: one Kraken_DecodeBytes block whose first byte is < 0x80
            var blk = SynthEntropy.Encode(d.AsSpan(cs, len), o.Literals, forceLongHeader: true, o.Seed + cs);
            bool raw = ((blk[0] >> 4) & 7) == 0;
            if (!raw || o.AllowRawEntropyChunk)
                return blk;
        }
        // stored chunk: LZ flag, mode 0, src_used == dst_count
        uint h = 0x800000u | (uint)len;
        return Concat(new[] { (byte)(h >> 16), (byte)(h >> 8), (byte)h }, d.AsSpan(cs, len).ToArray());
    }

    static byte[] Concat(params byte[][] parts)
    {
        var o = new List<byte>();
        foreach (var p in parts) o.AddRange(p);
        return o.ToArray();
    }

    // ------------------------------------------------------------------------------------------------ shared

    /// <summary>Distance d (>= 8) in Kraken's traditional code: packed byte + extra bits (BitReader_ReadDistance).</summary>
    static (byte packed, uint extra, int nbits) TraditionalDistance(int d)
    {
        int q = (d + 248) >> 4, low = (d + 248) & 15;
        int n = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)q);
        if (n >= 4 && n <= 18)
            return ((byte)(((n - 4) << 4) | low), (uint)(q - (1 << n)), n);
        // v >= 0xF0: d = 8322816 + (((1 << n) + e) << 12) + low12, n = v - 0xF0 + 4
        int r = d - 8322816;
        int q2 = r >> 12, low12 = r & 0xFFF;
        int n2 = q2 > 0 ? 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)q2) : -1;
        if (n2 < 4 || n2 > 19) throw new InvalidOperationException($"distance {d} out of range");
        return ((byte)(0xF0 + n2 - 4), ((uint)(q2 - (1 << n2)) << 12) | (uint)low12, n2 + 12);
    }

    /// <summary>Kraken_UnpackOffsets' packed offset stream: traditional (scale 0) or scaled coding.</summary>
    static bool PackOffsets(List<int> distances, int scale, List<byte> packed, List<byte> extraBytes, List<(uint, int)> bits)
    {
        foreach (var dist in distances)
        {
            if (scale == 0)
            {
                var (pk, extra, nb) = TraditionalDistance(dist);
                packed.Add(pk);
                bits.Add((extra, nb));
            }
            else
            {
                int low = scale == 1 ? 0 : dist % scale;
                int offs = (scale == 1 ? dist : dist / scale) + 8;
                if (scale != 1 && low > 255) return false;
                int n = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)offs) - 3;
                if (n < 0 || n > 26) return false;
                packed.Add((byte)((n << 3) | ((offs >> n) - 8)));
                bits.Add(((uint)(offs & ((1 << n) - 1)), n));
                if (scale != 1) extraBytes.Add((byte)low);
            }
        }
        return true;
    }

    /// <summary>Kraken_UnpackOffsets' bit streams: the u32 length count (backward), then the offsets alternating
    /// forward/backward, then the u32 lengths alternating. Returns forward bytes + reversed backward bytes.</summary>
    static byte[] OffsetLengthBits(List<(uint value, int nbits)> offsetBits, List<uint> u32Lens)
    {
        var a = new MsbBitWriter();
        var b = new MsbBitWriter();
        uint cnt = (uint)u32Lens.Count + 1;
        int nb = 32 - System.Numerics.BitOperations.LeadingZeroCount(cnt);
        for (int i = 0; i < nb - 1; i++) b.WriteBit(0);
        b.Write(cnt, nb);
        for (int i = 0; i < offsetBits.Count; i++)
            (i % 2 == 0 ? a : b).Write(offsetBits[i].value, offsetBits[i].nbits);
        for (int i = 0; i < u32Lens.Count; i++)
        {
            uint v = u32Lens[i] + 64;
            int bits = 32 - System.Numerics.BitOperations.LeadingZeroCount(v);
            int n = bits - 7;
            if (n < 0 || n > 12) throw new InvalidOperationException("length out of range");
            var w = i % 2 == 0 ? a : b;
            for (int k = 0; k < n; k++) w.WriteBit(0);
            w.Write(v, bits);
        }
        var bb = b.ToArray();
        Array.Reverse(bb);
        return Concat(a.ToArray(), bb);
    }

    // ------------------------------------------------------------------------------------------------ Kraken

    static byte[]? KrakenChunk(byte[] d, int cs, int ce, SynthOptions o, MatchFinder mf, Random rng)
    {
        int pos = cs;
        var prefix = new List<byte>();
        if (cs == 0)
        {
            if (ce - cs < 8 + 8) return null;
            prefix.AddRange(d.AsSpan(0, 8).ToArray());
            pos = 8;
        }
        var lits = new List<byte>();
        var cmds = new List<byte>();
        var newOffsets = new List<int>();       // distances, in command order
        var lenValues = new List<int>();        // len stream values (>= 3)
        int[] recent = { 8, 8, 8 };             // distances of recent_offs[3..5]
        int lastDist = 8;
        int litStart = pos;
        mf.InsertUpTo(pos);
        while (pos < ce)
        {
            // rep matches first (recent offsets), then the hash chain
            int bestLen = 0, bestDist = 0, bestRep = -1;
            for (int r = 0; r < 3; r++)
            {
                int dist = recent[r];
                if (pos - dist < 0) continue;
                int l = mf.MatchLen(pos, pos - dist, ce);
                if (l > bestLen + 1 && l >= 2) { bestLen = l; bestDist = dist; bestRep = r; }
            }
            var (fl, fd) = mf.Find(pos, ce, 8, o.MaxDistance, 0);
            if (fl >= o.MinMatch && fl > bestLen + 1) { bestLen = fl; bestDist = fd; bestRep = -1; }
            if (bestLen < 2 || (bestRep < 0 && bestLen < o.MinMatch) || bestDist < 8)
            {
                pos++;
                continue;
            }
            int litlen = pos - litStart;
            for (int p = litStart; p < pos; p++)
                lits.Add(o.LzMode == 0 ? (byte)(d[p] - d[p - lastDist]) : d[p]);
            int litField = litlen < 3 ? litlen : 3;
            if (litField == 3) lenValues.Add(litlen);
            int matchField;
            if (bestLen <= 16) matchField = bestLen - 2;
            else { matchField = 15; lenValues.Add(bestLen - 14); }
            int offsIndex;
            if (bestRep >= 0)
            {
                offsIndex = bestRep;
                int v = recent[bestRep];
                for (int k = bestRep; k > 0; k--) recent[k] = recent[k - 1];
                recent[0] = v;
            }
            else
            {
                offsIndex = 3;
                newOffsets.Add(bestDist);
                recent[2] = recent[1]; recent[1] = recent[0]; recent[0] = bestDist;
            }
            cmds.Add((byte)((offsIndex << 6) | (matchField << 2) | litField));
            lastDist = bestDist;
            pos += bestLen;
            litStart = pos;
            mf.InsertUpTo(pos);
        }
        for (int p = litStart; p < ce; p++)
            lits.Add(o.LzMode == 0 ? (byte)(d[p] - d[p - lastDist]) : d[p]);
        if (cmds.Count == 0) return null;

        // packed lengths
        var packedLens = new List<byte>();
        var u32 = new List<uint>();
        foreach (var L in lenValues)
        {
            int v = L - 3;
            if (v < 255) packedLens.Add((byte)v);
            else { packedLens.Add(255); u32.Add((uint)(v - 255)); }
        }
        if (u32.Count > 512 || packedLens.Count > (ce - cs) / 4) return null;

        // offsets
        var packedOffs = new List<byte>();
        var offsExtra = new List<byte>();
        var offBits = new List<(uint, int)>();
        int scale = o.OffsetScale;
        if (!PackOffsets(newOffsets, scale, packedOffs, offsExtra, offBits)) return null;
        var outp = new List<byte>(prefix);
        outp.AddRange(SynthEntropy.Encode(lits.ToArray(), o.Literals, forceLongHeader: true, o.Seed + 1));
        outp.AddRange(SynthEntropy.Encode(cmds.ToArray(), o.Commands, false, o.Seed + 2));
        if (scale == 0)
        {
            outp.AddRange(SynthEntropy.Encode(packedOffs.ToArray(), o.Other, forceLongHeader: true, o.Seed + 3));
        }
        else
        {
            outp.Add((byte)(127 + scale));
            outp.AddRange(SynthEntropy.Encode(packedOffs.ToArray(), o.Other, false, o.Seed + 3));
            if (scale != 1)
                outp.AddRange(SynthEntropy.Encode(offsExtra.ToArray(), o.Other, false, o.Seed + 4));
        }
        outp.AddRange(SynthEntropy.Encode(packedLens.ToArray(), o.Other, false, o.Seed + 5));
        outp.AddRange(OffsetLengthBits(offBits, u32));
        if (outp.Count - prefix.Count < 13 - (cs == 0 ? 8 : 0)) return null;
        return outp.ToArray();
    }

    // ------------------------------------------------------------------------------------------------ Mermaid

    static byte[]? MermaidChunk(byte[] d, int cs, int ce, SynthOptions o, MatchFinder mf, Random rng)
    {
        var prefix = new List<byte>();
        int pos = cs;
        if (cs == 0)
        {
            if (ce - cs < 8 + 16) return null;
            prefix.AddRange(d.AsSpan(0, 8).ToArray());
            pos = 8;
        }
        var lits = new List<byte>();
        var cmds = new List<byte>();
        var off16 = new List<ushort>();
        var off32 = new[] { new List<uint>(), new List<uint>() };
        var lengths = new List<byte>();
        int cmds1 = 0;
        int recent = 8; // distance; recent_offs = -recent
        int minMatch = Math.Max(3, o.MinMatch);
        mf.InsertUpTo(pos);
        for (int half = 0; half < 2; half++)
        {
            int hs = cs + half * 0x10000;
            int he = Math.Min(ce, hs + 0x10000);
            if (hs >= ce) break;
            if (half == 1) cmds1 = cmds.Count;
            int litStart = pos;
            while (pos < he)
            {
                // leave room: no command may reach past the half
                var (fl, fd) = mf.Find(pos, he, 8, o.MaxDistance, 0);
                int repLen = pos - recent >= 0 ? mf.MatchLen(pos, pos - recent, he) : 0;
                bool useRep = repLen >= 3 && repLen + 1 >= fl;
                int len = useRep ? repLen : fl;
                int dist = useRep ? recent : fd;
                bool near = dist <= 0xFFFF && !(o.PreferFar && pos - dist <= hs && len >= 8);
                if (len < minMatch && !(useRep && len >= 3))
                {
                    pos++;
                    continue;
                }
                if (!near)
                {
                    // far matches must reference data before the half start, and need length >= 8
                    if (pos - dist > hs || len < 8) { pos++; continue; }
                }
                // literals
                int litlen = pos - litStart;
                int lp = litStart;
                if (litlen >= 64)
                {
                    while (pos - lp >= 64)
                    {
                        int run = Math.Min(pos - lp, (o.Seed & 1) == 0 ? 64 + 251 : 64 + 3000);
                        EmitLits(lp, run);
                        cmds.Add(0);
                        AddLength(run - 64);
                        lp += run;
                    }
                }
                int rem = pos - lp;
                bool combine = near && len <= 15 && rem <= 7 && (dist == recent || len >= 3);
                if (!combine)
                {
                    while (rem > 0)
                    {
                        int k = Math.Min(rem, 7);
                        EmitLits(lp, k);
                        cmds.Add((byte)(0x80 | k)); // literals only: keep offset, match length 0
                        lp += k; rem -= k;
                    }
                }
                if (near && len <= 15)
                {
                    int k = combine ? rem : 0;
                    if (k > 0) EmitLits(lp, k);
                    if (dist == recent)
                    {
                        cmds.Add((byte)(0x80 | (len << 3) | k));
                    }
                    else
                    {
                        // len >= 3 here (minMatch >= 3), so cmd >= 24 with bit 7 clear
                        cmds.Add((byte)((len << 3) | k));
                        off16.Add((ushort)dist);
                    }
                    recent = dist;
                }
                else if (near && len >= 91)
                {
                    cmds.Add(1);
                    AddLength(len - 91);
                    off16.Add((ushort)dist);
                    recent = dist;
                }
                else if (near)
                {
                    // 16..90: a 15-byte match then repeats of the same offset
                    int first = 15;
                    if (dist == recent) cmds.Add((byte)(0x80 | (first << 3)));
                    else { cmds.Add((byte)(first << 3)); off16.Add((ushort)dist); }
                    recent = dist;
                    int left = len - first;
                    while (left > 0)
                    {
                        int k = Math.Min(15, left);
                        cmds.Add((byte)(0x80 | (k << 3)));
                        left -= k;
                    }
                }
                else
                {
                    uint off = (uint)(hs - (pos - dist));
                    if (len <= 28) cmds.Add((byte)(len - 5));
                    else { cmds.Add(2); AddLength(len - 29); }
                    off32[half].Add(off);
                    recent = dist;
                }
                pos += len;
                litStart = pos;
                mf.InsertUpTo(pos);
            }
            // final literals of the half
            EmitLits(litStart, he - litStart);
            pos = he;
        }
        if (cmds.Count == 0) return null;

        void EmitLits(int p, int n)
        {
            for (int i = 0; i < n; i++)
                lits.Add(o.LzMode == 0 ? (byte)(d[p + i] - d[p + i - recent]) : d[p + i]);
        }
        void AddLength(int v)
        {
            if (v <= 251) lengths.Add((byte)v);
            else
            {
                int w = (v - 252) >> 2;
                if (w > 0xFFFF) throw new InvalidOperationException("length too long");
                lengths.Add((byte)(252 + ((v - 252) & 3)));
                lengths.Add((byte)w); lengths.Add((byte)(w >> 8));
            }
        }

        var outp = new List<byte>(prefix);
        outp.AddRange(SynthEntropy.Encode(lits.ToArray(), o.Literals, false, o.Seed + 1));
        outp.AddRange(SynthEntropy.Encode(cmds.ToArray(), o.Commands, false, o.Seed + 2));
        if (ce - cs > 0x10000)
        {
            outp.Add((byte)cmds1); outp.Add((byte)(cmds1 >> 8));
        }
        if (o.Off16Entropy)
        {
            outp.Add(0xFF); outp.Add(0xFF);
            outp.AddRange(SynthEntropy.Encode(off16.Select(x => (byte)(x >> 8)).ToArray(), o.Other, false, o.Seed + 3));
            outp.AddRange(SynthEntropy.Encode(off16.Select(x => (byte)x).ToArray(), o.Other, false, o.Seed + 4));
        }
        else
        {
            if (off16.Count >= 0xFFFF) return null;
            outp.Add((byte)off16.Count); outp.Add((byte)(off16.Count >> 8));
            foreach (var x in off16) { outp.Add((byte)x); outp.Add((byte)(x >> 8)); }
        }
        int s1 = off32[0].Count, s2 = off32[1].Count;
        if (s1 == 0 && s2 == 0)
        {
            outp.Add(0); outp.Add(0); outp.Add(0);
        }
        else
        {
            uint tmp = ((uint)Math.Min(s1, 4095) << 12) | (uint)Math.Min(s2, 4095);
            outp.Add((byte)tmp); outp.Add((byte)(tmp >> 8)); outp.Add((byte)(tmp >> 16));
            if (s1 >= 4095) { outp.Add((byte)s1); outp.Add((byte)(s1 >> 8)); }
            if (s2 >= 4095) { outp.Add((byte)s2); outp.Add((byte)(s2 >> 8)); }
            for (int h = 0; h < 2; h++)
            {
                long limit = cs + h * 0x10000;
                foreach (var off in off32[h])
                {
                    if (limit < 0xC00000 - 1 || off < 0xC00000)
                    {
                        if (off >= 0xC00000) return null;
                        outp.Add((byte)off); outp.Add((byte)(off >> 8)); outp.Add((byte)(off >> 16));
                    }
                    else
                    {
                        // Mermaid_DecodeFarOffsets, large windows: a 3-byte value >= 0xC00000 takes a 4th byte (<< 22)
                        uint r = off - 0xC00000;
                        uint v = 0xC00000 + (r & 0x3FFFFF), b = r >> 22;
                        if (b > 255) return null;
                        outp.Add((byte)v); outp.Add((byte)(v >> 8)); outp.Add((byte)(v >> 16)); outp.Add((byte)b);
                    }
                }
            }
        }
        outp.AddRange(lengths);
        if (outp.Count - prefix.Count < 10 - (cs == 0 ? 8 : 0)) return null;
        return outp.ToArray();
    }

    // ------------------------------------------------------------------------------------------------ Leviathan

    static byte[]? LeviathanChunk(byte[] d, int cs, int ce, SynthOptions o, MatchFinder mf, Random rng)
    {
        int chunkType = o.LzMode;
        var prefix = new List<byte>();
        int pos = cs;
        if (cs == 0)
        {
            if (ce - cs < 8 + 32) return null;
            prefix.AddRange(d.AsSpan(0, 8).ToArray());
            pos = 8;
        }
        int nlit = chunkType switch { 0 or 1 => 1, 2 => 2, 3 => 4, _ => 16 };
        var litStreams = Enumerable.Range(0, nlit).Select(_ => new List<byte>()).ToArray();
        var cmdStreams = Enumerable.Range(0, 8).Select(_ => new List<byte>()).ToArray();
        var newOffsets = new List<int>();
        var fwdLens = new List<int>();   // literal lengths, forward
        var bwdLens = new List<int>();   // long match lengths, consumed from the end
        int[] recent = { 8, 8, 8, 8, 8, 8, 8 };
        int lastDist = 8;
        int matchZoneEnd = ce - 16;
        int litStart = pos;
        mf.InsertUpTo(pos);
        int cmdCount = 0;

        while (pos < matchZoneEnd)
        {
            int bestLen = 0, bestDist = 0, bestRep = -1;
            for (int r = 0; r < 7; r++)
            {
                int dist = recent[r];
                if (pos - dist < 0) continue;
                int l = mf.MatchLen(pos, pos - dist, matchZoneEnd);
                if (l > bestLen + 1 && l >= 2) { bestLen = l; bestDist = dist; bestRep = r; }
            }
            var (fl, fd) = mf.Find(pos, matchZoneEnd, 8, o.MaxDistance, 0);
            if (fl >= o.MinMatch && fl > bestLen + 1) { bestLen = fl; bestDist = fd; bestRep = -1; }
            int litlen = pos - litStart;
            if (bestLen < 2 || (bestRep < 0 && bestLen < o.MinMatch))
            {
                pos++;
                continue;
            }
            // (LamSub needs >= 1 literal whenever the literal field is non-zero; litlen 0 is field 0.)
            EmitLiterals(litStart, litlen);
            int litField = litlen < 3 ? litlen : 3;
            if (litField == 3) fwdLens.Add(litlen);
            int matchField;
            if (bestLen <= 8) matchField = bestLen - 2;
            else { matchField = 7; bwdLens.Add(bestLen - 6); }
            int offsIndex;
            if (bestRep >= 0)
            {
                offsIndex = bestRep;
                int v = recent[bestRep];
                for (int k = bestRep; k > 0; k--) recent[k] = recent[k - 1];
                recent[0] = v;
            }
            else
            {
                offsIndex = 7;
                newOffsets.Add(bestDist);
                for (int k = 6; k > 0; k--) recent[k] = recent[k - 1];
                recent[0] = bestDist;
            }
            byte cmd = (byte)((offsIndex << 5) | (litField << 3) | matchField);
            // multi-cmd: the stream is chosen by the output position when the command is fetched
            cmdStreams[o.MultiCmd ? (litStart - cs) & 7 : 0].Add(cmd);
            cmdCount++;
            lastDist = bestDist;
            pos += bestLen;
            litStart = pos;
            mf.InsertUpTo(pos);
        }
        EmitLiterals(litStart, ce - litStart);
        if (cmdCount == 0) return null;

        void EmitLiterals(int p, int n)
        {
            for (int i = 0; i < n; i++)
            {
                int at = p + i;
                switch (chunkType)
                {
                    case 0: litStreams[0].Add((byte)(d[at] - d[at - lastDist])); break;
                    case 1: litStreams[0].Add(d[at]); break;
                    case 2: litStreams[i == 0 ? 1 : 0].Add((byte)(d[at] - d[at - lastDist])); break;
                    case 3: litStreams[(at - cs) & 3].Add((byte)(d[at] - d[at - lastDist])); break;
                    case 4: litStreams[d[at - 1] >> 4].Add(d[at]); break;
                    default: litStreams[(at - cs) & 15].Add((byte)(d[at] - d[at - lastDist])); break;
                }
            }
        }

        // len stream: forward literal lengths, then the long match lengths in reverse order of use
        var lenValues = new List<int>(fwdLens);
        for (int i = bwdLens.Count - 1; i >= 0; i--) lenValues.Add(bwdLens[i]);
        var packedLens = new List<byte>();
        var u32 = new List<uint>();
        foreach (var L in lenValues)
        {
            int v = L - 3;
            if (v < 0) return null;
            if (v < 255) packedLens.Add((byte)v);
            else { packedLens.Add(255); u32.Add((uint)(v - 255)); }
        }
        int dstSize = ce - cs;
        if (u32.Count > 512 || packedLens.Count > dstSize / 5 || newOffsets.Count > dstSize / 3) return null;

        var packedOffs = new List<byte>();
        var offsExtra = new List<byte>();
        var offBits = new List<(uint, int)>();
        if (!PackOffsets(newOffsets, o.OffsetScale, packedOffs, offsExtra, offBits)) return null;
        var outp = new List<byte>(prefix);
        if (o.OffsetScale == 0)
        {
            outp.AddRange(SynthEntropy.Encode(packedOffs.ToArray(), o.Other, forceLongHeader: true, o.Seed + 3));
        }
        else
        {
            outp.Add((byte)(127 + o.OffsetScale));
            outp.AddRange(SynthEntropy.Encode(packedOffs.ToArray(), o.Other, false, o.Seed + 3));
            if (o.OffsetScale != 1)
                outp.AddRange(SynthEntropy.Encode(offsExtra.ToArray(), o.Other, false, o.Seed + 4));
        }
        outp.AddRange(SynthEntropy.Encode(packedLens.ToArray(), o.Other, false, o.Seed + 5));
        if (chunkType <= 1)
            outp.AddRange(SynthEntropy.Encode(litStreams[0].ToArray(), o.Literals, false, o.Seed + 1));
        else
            outp.AddRange(MultiArray(litStreams.Select(l => l.ToArray()).ToArray(), o.Literals, o.Seed + 1));
        if (!o.MultiCmd)
        {
            outp.AddRange(SynthEntropy.Encode(cmdStreams[0].ToArray(), o.Commands, forceLongHeader: true, o.Seed + 2));
        }
        else
        {
            outp.Add(0x83);
            outp.AddRange(MultiArray(cmdStreams.Select(l => l.ToArray()).ToArray(), o.Commands, o.Seed + 2));
        }
        outp.AddRange(OffsetLengthBits(offBits, u32));
        return outp.ToArray();

        byte[] MultiArray(byte[][] arrays, EntropyKind kind, int seed) => o.FullMultiArray
            ? SynthEntropy.MultiArrayFull(arrays, kind, seed, combined: (seed & 1) == 0, numEntropy: 1 + seed % 4)
            : SynthEntropy.MultiArraySimple(arrays, kind, seed);
    }
}
