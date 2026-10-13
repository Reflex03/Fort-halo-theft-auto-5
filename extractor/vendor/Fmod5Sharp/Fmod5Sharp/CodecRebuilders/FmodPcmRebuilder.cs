using System;
using Fmod5Sharp.FmodTypes;
using NAudio.Wave;
using System.IO;

namespace Fmod5Sharp.CodecRebuilders
{
    public static class FmodPcmRebuilder
    {
        public static byte[] Rebuild(FmodSample sample, FmodAudioType type)
        {
            var width = type switch
            {
                FmodAudioType.PCM8 => 1,
                FmodAudioType.PCM16 => 2,
                FmodAudioType.PCM32 => 4,
                _ => throw new($"FmodPcmRebuilder does not support encoding of type {type}"),
            };

            var numChannels = sample.Metadata.IsStereo ? 2 : 1;
            var format = WaveFormat.CreateCustomFormat(
                WaveFormatEncoding.Pcm,
                sample.Metadata.Frequency,
                numChannels,
                sample.Metadata.Frequency * numChannels * width,
                numChannels * width,
                width * 8
            );
            using var stream = new MemoryStream();
            // CthExtract patch: finish the WAV (RIFF/data sizes) before taking the bytes, and return only the used
            // part of the buffer (upstream returned GetBuffer() while the writer was still open).
            using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(stream), format))
            {
                var bytes = sample.SampleBytes;
                writer.Write(bytes, 0, Math.Min(bytes.Length, numChannels * width * (int) sample.Metadata.SampleCount));
            }

            return stream.ToArray();
        }
    }
}