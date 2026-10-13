using CthExtract.Core;
using Fmod5Sharp.CodecRebuilders;
using Fmod5Sharp.FmodTypes;

namespace CthExtract.Halo;

/// <summary>
/// Vorbis setup headers for FSB5 Vorbis samples. FSB5 stores only the crc32 of a sample's setup header; the header
/// itself must come from a table. The vendored Fmod5Sharp table is generated from reference libvorbis (see
/// vendor/Fmod5Sharp/VENDORED.md) and covers what libvorbis produces for 1–2 channels. FMOD's own encoder also uses
/// setups libvorbis does not produce; those are read, when a bank needs them, from the FMOD code inside the player's own
/// MCC install (the same way the game finds them), never shipped.
/// </summary>
public static class VorbisSetup
{
    private static readonly uint[] CrcTable = MakeTable();
    private static readonly byte[] Magic = { 5, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s' };
    public const int MaxHeaderLength = 16384;
    public const long MaxBinarySize = 768L << 20;

    /// <summary>
    /// Bit offset of the 6-bit "mode count - 1" field of a Vorbis setup header (Fmod5Sharp's SeekBit), found from the end
    /// of the packet: the last bit written is the framing bit (1), before it come the modes, 41 bits each
    /// (blockflag 1, window type 16 = 0, transform type 16 = 0, mapping 8), before them the 6-bit count. The largest mode
    /// count whose fields are all valid wins (a short false match is far likelier than a long one). -1 if none.
    /// </summary>
    public static int FindModeSeekBit(ReadOnlySpan<byte> setup)
    {
        if (setup.Length < 16 || !setup[..7].SequenceEqual(Magic)) return -1;
        int last = setup.Length - 1;
        if (setup[last] == 0) return -1;
        int hb = 7;
        while (hb > 0 && (setup[last] & (1 << hb)) == 0) hb--;
        long framing = (long)last * 8 + hb;
        for (int m = 64; m >= 1; m--)
        {
            long s = framing - 41L * m - 6;
            if (s < 7 * 8) continue;
            if (Bits(setup, s, 6) != m - 1) continue;
            bool ok = true;
            for (int i = 0; i < m && ok; i++)
            {
                long b = s + 6 + 41L * i;
                ok = Bits(setup, b + 1, 16) == 0 && Bits(setup, b + 17, 16) == 0;
            }
            if (ok) return (int)s;
        }
        return -1;
    }

    /// <summary>Vorbis bit order: LSB first within each byte.</summary>
    public static uint Bits(ReadOnlySpan<byte> data, long bit, int count)
    {
        uint v = 0;
        for (int i = 0; i < count; i++)
        {
            long k = bit + i;
            if ((data[(int)(k >> 3)] >> (int)(k & 7) & 1) != 0) v |= 1u << i;
        }
        return v;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>Distinct setup-header crcs the Vorbis samples of a bank need (metadata only, no sample data read).</summary>
    public static HashSet<uint> NeededBy(FmodSoundBank bank)
    {
        var set = new HashSet<uint>();
        if (bank.Header.AudioType != FmodAudioType.VORBIS) return set;
        foreach (var s in bank.Samples)
            if (FmodVorbisRebuilder.GetSetupHeaderCrc(s) is { } c) set.Add(c);
        return set;
    }

    /// <summary>
    /// Find setup headers with the wanted crcs inside binary files: every "\x05vorbis" occurrence is followed with a
    /// running crc32 until a length matches. Returns crc → (header bytes, seek bit) for headers that also parse.
    /// </summary>
    public static Dictionary<uint, (byte[] Header, int SeekBit, string File)> Scan(IEnumerable<string> files, IReadOnlySet<uint> wanted, Action<string>? log = null)
    {
        var found = new Dictionary<uint, (byte[], int, string)>();
        if (wanted.Count == 0) return found;
        const int chunk = 16 << 20;
        foreach (var file in files)
        {
            if (found.Count == wanted.Count) break;
            long size;
            try { size = new FileInfo(file).Length; } catch { continue; }
            if (size > MaxBinarySize || size < Magic.Length) continue;
            int before = found.Count;
            try
            {
                using var fs = GameFile.OpenRead(file);
                var buf = new byte[chunk + MaxHeaderLength];
                long pos = 0;
                int carry = 0;
                while (true)
                {
                    int read = 0, n;
                    while (carry + read < buf.Length && (n = fs.Read(buf, carry + read, buf.Length - carry - read)) > 0) read += n;
                    int len = carry + read;
                    if (len <= Magic.Length) break;
                    bool eof = read == 0 || pos + len >= size;
                    // occurrences starting before 'limit' have all their bytes in this buffer (or the file ends)
                    int limit = eof ? len : len - MaxHeaderLength;
                    var span = buf.AsSpan(0, len);
                    int at = 0;
                    while (at < limit)
                    {
                        int k = span[at..limit].IndexOf(Magic);
                        if (k < 0) break;
                        at += k;
                        var tail = span.Slice(at, Math.Min(MaxHeaderLength, len - at));
                        uint c = 0xFFFFFFFF;
                        for (int i = 0; i < tail.Length; i++)
                        {
                            c = CrcTable[(c ^ tail[i]) & 0xFF] ^ (c >> 8);
                            uint v = ~c;
                            if (i >= 64 && wanted.Contains(v) && !found.ContainsKey(v))
                            {
                                var header = tail[..(i + 1)].ToArray();
                                int seek = FindModeSeekBit(header);
                                if (seek > 0) { found[v] = (header, seek, file); break; }
                            }
                        }
                        at += Magic.Length;
                    }
                    if (eof) break;
                    // keep the last MaxHeaderLength bytes so occurrences near the end are completed next round
                    Array.Copy(buf, len - MaxHeaderLength, buf, 0, MaxHeaderLength);
                    pos += len - MaxHeaderLength;
                    carry = MaxHeaderLength;
                }
            }
            catch (Exception e) { log?.Invoke($"cannot scan {file}: {e.GetType().Name}: {e.Message}"); continue; }
            if (found.Count > before) log?.Invoke($"found {found.Count - before} Vorbis setup header(s) in {file}");
        }
        return found;
    }

    /// <summary>Binaries of an MCC install that may contain FMOD (fmod*.dll first, then the game DLLs and executables).</summary>
    public static IReadOnlyList<string> CandidateBinaries(HaloInstall install)
    {
        var list = new List<string>();
        void AddDir(string? dir, int depth)
        {
            if (dir is null || depth < 0) return;
            try
            {
                list.AddRange(Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var n = Path.GetFileName(d);
                    if (n.Equals("maps", StringComparison.OrdinalIgnoreCase) || n.Equals("fmod", StringComparison.OrdinalIgnoreCase) || n.StartsWith("_", StringComparison.Ordinal)) continue;
                    AddDir(d, depth - 1);
                }
            }
            catch { }
        }
        AddDir(install.Root, 3);
        return list.Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(f => Path.GetFileName(f).Contains("fmod", StringComparison.OrdinalIgnoreCase) ? 0
                               : Path.GetFileName(f).StartsWith("halo3", StringComparison.OrdinalIgnoreCase) ? 1
                               : f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
                   .ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Make sure every setup header a bank needs is available: log coverage, and look for the missing ones in the
    /// install's binaries (once per bank). Returns the crcs still missing.
    /// </summary>
    public static HashSet<uint> EnsureFor(FmodSoundBank bank, string bankName, Func<IReadOnlyList<string>> binaries, Log log)
    {
        var needed = NeededBy(bank);
        var missing = needed.Where(c => !FmodVorbisRebuilder.HasSetupHeader(c)).ToHashSet();
        log.Info($"[halo] {bankName}: {needed.Count} distinct Vorbis setup headers used; {needed.Count - missing.Count} in the libvorbis table ({FmodVorbisRebuilder.SetupHeaderCount} entries), {missing.Count} not");
        if (missing.Count == 0) return missing;
        var files = binaries();
        log.Info($"[halo] looking for {missing.Count} setup header(s) in {files.Count} binaries of the install");
        var found = Scan(files, missing, m => log.Info("[halo] " + m));
        foreach (var (crc, (header, seek, file)) in found)
        {
            FmodVorbisRebuilder.RegisterSetupHeader(crc, header, seek);
            missing.Remove(crc);
        }
        if (missing.Count > 0)
            log.Warn($"[halo] {missing.Count} setup header(s) not found anywhere ({string.Join(", ", missing.Take(8).Select(c => $"0x{c:x8}"))}{(missing.Count > 8 ? ", ..." : "")}); samples using them fall back to placeholders");
        return missing;
    }
}
