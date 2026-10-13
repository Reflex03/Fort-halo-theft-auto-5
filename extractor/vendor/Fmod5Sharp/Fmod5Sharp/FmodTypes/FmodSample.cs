using System;
using System.Diagnostics.CodeAnalysis;
using Fmod5Sharp.CodecRebuilders;

namespace Fmod5Sharp.FmodTypes
{
    public class FmodSample
    {
        public FmodSampleMetadata Metadata;
        public string? Name;
        internal FmodSoundBank? MyBank;

        // CthExtract patch: sample bytes are either held (upstream behaviour) or read on demand from the bank's
        // stream (FsbLoader.LoadFsbFromStreamLazy), so a multi-hundred-MB bank costs only its headers in memory.
        private byte[]? sampleBytes;
        private readonly Func<byte[]>? loader;

        /// <summary>The sample's encoded bytes. For a lazily loaded bank this reads them from the bank's stream on
        /// every access (nothing is cached); call once and keep the array if you need it twice.</summary>
        public byte[] SampleBytes
        {
            get => sampleBytes ?? loader?.Invoke() ?? Array.Empty<byte>();
            set => sampleBytes = value;
        }

        /// <summary>Absolute offset of the sample data in the bank stream (CthExtract patch; -1 if unknown).</summary>
        public long DataOffset { get; internal set; } = -1;

        /// <summary>Length in bytes of the sample data in the bank (CthExtract patch).</summary>
        public int DataLength { get; internal set; }

        /// <summary>True when <see cref="SampleBytes"/> is read from the bank stream on demand (CthExtract patch).</summary>
        public bool IsLazy => sampleBytes == null && loader != null;

        public FmodSample(FmodSampleMetadata metadata, byte[] sampleBytes)
        {
            Metadata = metadata;
            this.sampleBytes = sampleBytes;
            DataLength = sampleBytes.Length;
        }

        /// <summary>A sample whose bytes are read by <paramref name="loader"/> when needed (CthExtract patch).</summary>
        public FmodSample(FmodSampleMetadata metadata, Func<byte[]> loader, long dataOffset, int dataLength)
        {
            Metadata = metadata;
            this.loader = loader;
            DataOffset = dataOffset;
            DataLength = dataLength;
        }

#if NET6_0_OR_GREATER
		public bool RebuildAsStandardFileFormat([NotNullWhen(true)] out byte[]? data, [NotNullWhen(true)] out string? fileExtension)
#else
        public bool RebuildAsStandardFileFormat(out byte[]? data, out string? fileExtension)
#endif
        {
            switch (MyBank!.Header.AudioType)
            {
                case FmodAudioType.VORBIS:
                    data = FmodVorbisRebuilder.RebuildOggFile(this);
                    fileExtension = "ogg";
                    return data.Length > 0;
                case FmodAudioType.PCM8:
                case FmodAudioType.PCM16:
                case FmodAudioType.PCM32:
                    data = FmodPcmRebuilder.Rebuild(this, MyBank.Header.AudioType);
                    fileExtension = "wav";
                    return data.Length > 0;
                case FmodAudioType.GCADPCM:
                    data = FmodGcadPcmRebuilder.Rebuild(this);
                    fileExtension = "wav";
                    return data.Length > 0;
                case FmodAudioType.IMAADPCM:
                    data = FmodImaAdPcmRebuilder.Rebuild(this);
                    fileExtension = "wav";
                    return data.Length > 0;
                case FmodAudioType.FADPCM:
                    data = FmodFadPcmRebuilder.Rebuild(this);
                    fileExtension = "wav";
                    return data.Length > 0;
                default:
                    data = null;
                    fileExtension = null;
                    return false;
            }
        }
    }
}
