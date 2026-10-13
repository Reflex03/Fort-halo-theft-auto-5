using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Fmod5Sharp.FmodTypes;
using Fmod5Sharp.Util;

namespace Fmod5Sharp
{
    public static class FsbLoader
    {
        internal static readonly Dictionary<uint, int> Frequencies = new()
        {
            { 0, 4_000 },
            { 1, 8_000 },
            { 2, 11_000 },
            { 3, 12_000 },
            { 4, 16_000 },
            { 5, 22_050 },
            { 6, 24_000 },
            { 7, 32_000 },
            { 8, 44_100 },
            { 9, 48_000 },
            { 10, 96_000 },
        };

        private static FmodSoundBank? LoadInternal(Stream stream, bool throwIfError, bool lazy = false, bool leaveOpen = false)
        {
            // CthExtract patch: the lazy path keeps the stream open and gives every sample a loader instead of its bytes.
            BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
            var ownsStream = !leaveOpen;
            try
            {
                FmodAudioHeader header = new(reader);

                if (!header.IsValid)
                {
                    if (throwIfError)
                        throw new("File is probably not an FSB file (magic number mismatch)");

                    return null;
                }

                long dataStartOffset = header.SizeOfThisHeader + header.SizeOfNameTable + header.SizeOfSampleHeaders;

                // CthExtract patch: refuse banks whose declared sizes run past the end of the stream instead of
                // reading garbage (a truncated download or a half-written game update).
                if (stream.CanSeek && dataStartOffset + header.SizeOfData > stream.Length)
                {
                    if (throwIfError)
                        throw new InvalidDataException($"FSB5 bank is truncated: header says {dataStartOffset + header.SizeOfData} bytes, stream has {stream.Length}");
                    return null;
                }

                // CthExtract patch: read the whole name table once instead of one seek per sample.
                byte[]? nameTable = null;
                if (header.SizeOfNameTable > 0)
                {
                    stream.Position = header.SizeOfThisHeader + header.SizeOfSampleHeaders;
                    nameTable = new byte[header.SizeOfNameTable];
                    stream.ReadExactly(nameTable, 0, nameTable.Length);
                }

                var source = lazy ? new SampleSource(stream, ownsStream) : null;

                List<FmodSample> samples = new(header.Samples.Count);
                for (var i = 0; i < header.Samples.Count; i++)
                {
                    var sampleMetadata = header.Samples[i];

                    var firstByteOfSample = (long)sampleMetadata.DataOffset;
                    var lastByteOfSample = (long)header.SizeOfData;

                    if (i < header.Samples.Count - 1)
                    {
                        lastByteOfSample = header.Samples[i + 1].DataOffset;
                    }

                    if (lastByteOfSample < firstByteOfSample || lastByteOfSample > header.SizeOfData)
                    {
                        if (throwIfError)
                            throw new InvalidDataException($"FSB5 sample {i} has a bad data range {firstByteOfSample}..{lastByteOfSample} (data size {header.SizeOfData})");
                        return null;
                    }

                    var absolute = dataStartOffset + firstByteOfSample;
                    var length = checked((int)(lastByteOfSample - firstByteOfSample));

                    FmodSample sample;
                    if (source != null)
                    {
                        sample = new FmodSample(sampleMetadata, () => source.Read(absolute, length), absolute, length);
                    }
                    else
                    {
                        var sampleData = new byte[length];
                        stream.Position = absolute;
                        stream.ReadExactly(sampleData, 0, sampleData.Length);
                        sample = new FmodSample(sampleMetadata, sampleData) { DataOffset = absolute };
                    }

                    if (nameTable != null)
                    {
                        var nameOffsetOffset = 4 * i;
                        if (nameOffsetOffset + 4 <= nameTable.Length)
                        {
                            var nameOffset = BitConverter.ToUInt32(nameTable, nameOffsetOffset);
                            if (nameOffset < nameTable.Length)
                            {
                                var end = Array.IndexOf(nameTable, (byte)0, (int)nameOffset);
                                if (end < 0) end = nameTable.Length;
                                sample.Name = Encoding.UTF8.GetString(nameTable, (int)nameOffset, end - (int)nameOffset);
                            }
                        }
                    }

                    samples.Add(sample);
                }

                if (source != null)
                    ownsStream = false; // the bank owns it now

                return new FmodSoundBank(header, samples, source);
            }
            finally
            {
                reader.Dispose();
                if (ownsStream)
                    stream.Dispose();
            }
        }

        public static bool TryLoadFsbFromStream(Stream stream, out FmodSoundBank? bank)
        {
            bank = LoadInternal(stream, false);
            return bank != null;
        }

        public static FmodSoundBank LoadFsbFromStream(Stream stream)
            => LoadInternal(stream, true)!;

        public static bool TryLoadFsbFromByteArray(byte[] bankBytes, out FmodSoundBank? bank)
        {
            using var stream = new MemoryStream(bankBytes);
            bank = LoadInternal(stream, false);
            return bank != null;
        }

        public static FmodSoundBank LoadFsbFromByteArray(byte[] bankBytes)
        {
            using var stream = new MemoryStream(bankBytes);
            return LoadInternal(stream, true)!;
        }

        /// <summary>
        /// CthExtract patch: parse only the headers and the name table; each sample reads its bytes from
        /// <paramref name="stream"/> when <see cref="FmodSample.SampleBytes"/> is used. The stream must be seekable.
        /// Dispose the bank to release the stream (unless <paramref name="leaveOpen"/>).
        /// </summary>
        public static FmodSoundBank LoadFsbFromStreamLazy(Stream stream, bool leaveOpen = false)
        {
            if (!stream.CanSeek)
                throw new ArgumentException("lazy FSB loading needs a seekable stream", nameof(stream));
            return LoadInternal(stream, true, lazy: true, leaveOpen: leaveOpen)!;
        }

        /// <summary>
        /// CthExtract patch: open an FSB5 file read-only (sharing read/write/delete with other processes, so a running
        /// game or an update is never blocked) and load it lazily.
        /// </summary>
        public static FmodSoundBank LoadFsbFromFileLazy(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
            try
            {
                return LoadFsbFromStreamLazy(fs);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        /// <summary>Reads sample byte ranges from a shared stream (CthExtract patch).</summary>
        private sealed class SampleSource : IDisposable
        {
            private readonly Stream stream;
            private readonly bool owns;
            private readonly object gate = new();
            private bool disposed;

            public SampleSource(Stream stream, bool owns)
            {
                this.stream = stream;
                this.owns = owns;
            }

            public byte[] Read(long offset, int length)
            {
                lock (gate)
                {
                    if (disposed)
                        throw new ObjectDisposedException(nameof(FmodSoundBank), "the FSB bank was disposed");
                    var data = new byte[length];
                    stream.Position = offset;
                    stream.ReadExactly(data, 0, length);
                    return data;
                }
            }

            public void Dispose()
            {
                lock (gate)
                {
                    if (disposed) return;
                    disposed = true;
                    if (owns) stream.Dispose();
                }
            }
        }
    }
}
