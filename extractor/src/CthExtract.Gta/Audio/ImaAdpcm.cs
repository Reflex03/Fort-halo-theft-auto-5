using System.Buffers.Binary;

namespace CthExtract.Gta.Audio;

/// <summary>
/// IMA ADPCM as used by RAGE AWC (codec 4): mono, 0x800-byte frames, each a 4-byte header (s16 step index, s16
/// predictor, little-endian) followed by 0x7FC bytes of 4-bit codes, low nibble first. Standard IMA expansion
/// (Intel/DVI step and index tables; predictor clamped to 16 bits). Format facts from vgmstream (ISC).
/// </summary>
public static class ImaAdpcm
{
    public const int FrameSize = 0x800;
    public const int SamplesPerFrame = (FrameSize - 4) * 2;

    public static readonly short[] StepTable =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97,
        107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
        876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428,
        4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350,
        22385, 24623, 27086, 29794, 32767,
    };

    public static readonly sbyte[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

    /// <summary>Expand one 4-bit code (shift-and-add form of diff = (code + 1/2) * step / 4).</summary>
    public static void Expand(int code, ref int predictor, ref int index)
    {
        int step = StepTable[index];
        int diff = step >> 3;
        if ((code & 1) != 0) diff += step >> 2;
        if ((code & 2) != 0) diff += step >> 1;
        if ((code & 4) != 0) diff += step;
        if ((code & 8) != 0) diff = -diff;
        predictor = Math.Clamp(predictor + diff, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexTable[code], 0, 88);
    }

    /// <summary>Decode consecutive AWC IMA frames to at most <paramref name="sampleCount"/> samples.</summary>
    public static short[] DecodeFrames(ReadOnlySpan<byte> data, int sampleCount)
    {
        int frames = (data.Length + FrameSize - 1) / FrameSize;
        sampleCount = Math.Min(sampleCount, frames * SamplesPerFrame);
        var o = new short[Math.Max(0, sampleCount)];
        int w = 0;
        for (int f = 0; f < frames && w < o.Length; f++)
        {
            var frame = data.Slice(f * FrameSize, Math.Min(FrameSize, data.Length - f * FrameSize));
            if (frame.Length < 4) break;
            w += DecodeFrame(frame, o.AsSpan(w));
        }
        return w == o.Length ? o : o.AsSpan(0, w).ToArray();
    }

    /// <summary>Decode one frame (header + codes) into dst; returns samples written.</summary>
    public static int DecodeFrame(ReadOnlySpan<byte> frame, Span<short> dst)
    {
        int index = Math.Clamp((int)BinaryPrimitives.ReadInt16LittleEndian(frame), 0, 88);
        int predictor = BinaryPrimitives.ReadInt16LittleEndian(frame.Slice(2));
        int w = 0;
        for (int p = 4; p < frame.Length && w < dst.Length; p++)
        {
            Expand(frame[p] & 0xF, ref predictor, ref index);
            dst[w++] = (short)predictor;
            if (w >= dst.Length) break;
            Expand(frame[p] >> 4, ref predictor, ref index);
            dst[w++] = (short)predictor;
        }
        return w;
    }
}
