using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Fmod5Sharp.ChunkData;
using Fmod5Sharp.FmodTypes;
using Fmod5Sharp.Util;
using OggVorbisEncoder;

namespace Fmod5Sharp.CodecRebuilders
{
    public static class FmodVorbisRebuilder
    {
        // CthExtract patch: concurrent dictionary, loaded once under a lock (upstream: plain Dictionary, racy first load)
        private static System.Collections.Concurrent.ConcurrentDictionary<uint, FmodVorbisData>? headers;
        private static readonly object HeadersLock = new();

        private static void LoadVorbisHeaders()
        {
            lock (HeadersLock)
            {
                if (headers == null)
                    headers = new(LoadVorbisHeadersCore()!);
            }
        }

        private static Dictionary<uint, FmodVorbisData>? LoadVorbisHeadersCore()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Fmod5Sharp.Util.vorbis_headers_converted.json")
                               ?? throw new Exception($"Embedded resources for vorbis header data not found, has the assembly been tampered with?");
            using StreamReader reader = new(stream);

            var jsonString = reader.ReadToEnd();
            return JsonSerializer.Deserialize(jsonString, Fmod5SharpJsonContext.Default.DictionaryUInt32FmodVorbisData);
        }

        /// <summary>CthExtract patch: the crc32 of the Vorbis setup header a sample needs, or null when it has no VORBISDATA chunk.</summary>
        public static uint? GetSetupHeaderCrc(FmodSample sample)
        {
            var dataChunk = sample.Metadata.Chunks.FirstOrDefault(f => f.ChunkType == FmodSampleChunkType.VORBISDATA);
            return dataChunk?.ChunkData is VorbisChunkData v ? v.Crc32 : null;
        }

        /// <summary>CthExtract patch: true when the embedded table holds the setup header with this crc32.</summary>
        public static bool HasSetupHeader(uint crc32)
        {
            if (headers == null)
                LoadVorbisHeaders();
            return headers!.ContainsKey(crc32);
        }

        /// <summary>
        /// CthExtract patch: add a setup header found elsewhere (e.g. in the player's own FMOD binary) for this run.
        /// The caller has checked that crc32(<paramref name="headerBytes"/>) == <paramref name="crc32"/> and found the
        /// bit offset of the mode count (<paramref name="seekBit"/>).
        /// </summary>
        public static void RegisterSetupHeader(uint crc32, byte[] headerBytes, int seekBit)
        {
            if (headers == null)
                LoadVorbisHeaders();
            headers![crc32] = new FmodVorbisData(headerBytes, seekBit);
        }

        /// <summary>CthExtract patch: number of setup headers in the embedded table.</summary>
        public static int SetupHeaderCount
        {
            get
            {
                if (headers == null)
                    LoadVorbisHeaders();
                return headers!.Count;
            }
        }

        public static byte[] RebuildOggFile(FmodSample sample)
        {
            //Need to rebuild the vorbis header, which requires reading the known blobs from the json file.
            //This requires knowing the crc32 of the data, which is in a VORBISDATA chunk.
            var dataChunk = sample.Metadata.Chunks.FirstOrDefault(f => f.ChunkType == FmodSampleChunkType.VORBISDATA);

            if (dataChunk == null)
            {
                throw new Exception("Rebuilding Vorbis data requires a VORBISDATA chunk, which wasn't found");
            }

            var chunkData = (VorbisChunkData)dataChunk.ChunkData;
            var crc32 = chunkData.Crc32;

            //Ok, we have the crc32, now we need to find the header data.
            if (headers == null)
                LoadVorbisHeaders();
            // CthExtract patch: name the missing setup header instead of a bare KeyNotFoundException.
            if (!headers!.TryGetValue(crc32, out var vorbisData))
                throw new KeyNotFoundException($"Vorbis setup header with crc32 0x{crc32:x8} is not in the lookup table ({headers.Count} entries); sample '{sample.Name}' cannot be rebuilt");
            
            vorbisData.InitBlockFlags();
            
            var infoPacket = BuildInfoPacket((byte)sample.Metadata.Channels, sample.Metadata.Frequency);
            var commentPacket = BuildCommentPacket("Fmod5Sharp (Samboy063)");
            var setupPacket = new OggPacket(vorbisData.HeaderBytes, false, 0, 2);
            
            //Begin building the final stream
            var oggStream = new OggStream(1);
            using var outputStream = new MemoryStream();
            
            oggStream.PacketIn(infoPacket);
            oggStream.PacketIn(commentPacket);
            oggStream.PacketIn(setupPacket);
            
            oggStream.FlushAndCopyTo(outputStream, true);

            CopySampleData(vorbisData, sample.Metadata.SampleCount, sample.SampleBytes, oggStream, outputStream);

            return outputStream.ToArray();
        }

        private static void FlushAndCopyTo(this OggStream stream, Stream other, bool force = false)
        {
            while (stream.PageOut(out var page, force))
            {
                other.Write(page.Header, 0, page.Header.Length);
                other.Write(page.Body, 0, page.Body.Length);
            }
        }

        private static OggPacket BuildInfoPacket(byte channels, int frequency)
        {
            using var memStream = new MemoryStream(30);
            using var writer = new BinaryWriter(memStream);
            
            // Packet Type (1)
            writer.Write((byte)1);
            // Codec (vorbis)
            writer.Write(System.Text.Encoding.UTF8.GetBytes("vorbis"));
            // Version (0)
            writer.Write(0);
            // Num channels
            writer.Write(channels);
            // Frequency
            writer.Write(frequency);
            
            //Leave max, nominal, and min bitrate at 0 (to be auto calculated)
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            // Block Size
            // 4 bits - Short Blocksize [-3]
            // 4 bits - Long Blocksize [-3]
            writer.Write((byte)0b1011_1000);
            // Framing Bit (1)
            writer.Write((byte)1);
            
            return new(memStream.ToArray(), false, 0, 0);
        }
      
        private static OggPacket BuildCommentPacket(string vendor)
        {
            using MemoryStream memoryStream = new();
            using BinaryReader binaryReader = new(memoryStream);

            using MemoryStream oggMs = new();
            using BinaryWriter fm = new(oggMs);

            fm.Seek(0, SeekOrigin.Begin);

            // Packet Type (3)
            fm.Write((byte)3);
            fm.Write(System.Text.Encoding.UTF8.GetBytes("vorbis")); // Codec (vorbis)

            //Length-prefixed vendor string
            fm.Write(vendor.Length);
            fm.Write(System.Text.Encoding.UTF8.GetBytes(vendor));
            
            // Number of comments (0)
            fm.Write(0);
            // Framing Bit (1)
            fm.Write((byte)1);

            return new(oggMs.ToArray(), false, 0, 1);
        }

        private static void CopySampleData(FmodVorbisData vorbisData, uint sampleCount, byte[] sampleBytes, OggStream oggStream, Stream outputStream)
        {
            using var inputStream = new MemoryStream(sampleBytes);
            using var inputReader = new BinaryReader(inputStream);

            ReadSamplePackets(inputReader, out var packetLength, out var packets);

            var packetNum = 1;
            uint granulePos = 0;
            uint previousBlockSize = 0;
            
            var finalPacketNum = packetLength.Count - 1;
            
            for (var i = 0; i < packets.Count; i++)
            {
                var isLast = i == finalPacketNum;
                var packet = packets[i];
                packetNum++;

                //If the input packet is empty, so is the output block, otherwise calculate based on the vorbis data.
                var blockSize = packetLength[i] == 0 ? 0 : vorbisData.GetPacketBlockSize(packet);

                //Calculate next granule position
                if (previousBlockSize == 0)
                    granulePos = 0;
                else
                    granulePos += (blockSize + previousBlockSize) / 4;
                
                //Make sure to end the stream once we reach the sample count in the metadata.
                if (granulePos > sampleCount)
                    granulePos = sampleCount;

                //Set previous block size
                previousBlockSize = blockSize;

                //Write the packet to the stream
                oggStream.PacketIn(new(packet, isLast, (int) granulePos, packetNum));
                oggStream.FlushAndCopyTo(outputStream, isLast);

                //Early end to the stream if this is going past the sample count in the metadata.
                if (granulePos == sampleCount)
                    break;
            }
        }

        private static void ReadSamplePackets(BinaryReader inputReader, out List<ushort> packetLengths, out List<byte[]> packets)
        {
            packetLengths = new();
            packets = new();

            while (inputReader.BaseStream.Position + sizeof(ushort) < inputReader.BaseStream.Length)
            {
                var packetSize = inputReader.ReadUInt16();

                if (packetSize == 0 || packetSize == 0xFFFF)
                    break; //EOS

                packetLengths.Add(packetSize);
                packets.Add(inputReader.ReadBytes(packetSize));
            }
        }
    }
}