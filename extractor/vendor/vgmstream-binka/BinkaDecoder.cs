// C# port of vgmstream src/coding/libs/binka_dec.c and src/util/bitstream_lsb.h (read side)
// (commit 7c520e8f9b078dfde2f84f1b5bcbad0c6c415806).
// Copyright (c) 2008-2025 Adam Gashlin, Fastelbja, Ronny Elfert, bnnm, et al. ISC licence: see LICENSE.
//
// Original notes (binka_dec.c): decodes RAD Game Tools's Bink Audio, a fairly simple VBR transform-based codec,
// "reversed from scratch" (mainly from binkawin.asi/radutil.dll), including Unreal Engine's variation (Bink Audio 2,
// "UEBA"). It does not use anything from RAD's or Epic's sources.
//
// Port notes (CthExtract): statement order and float/double mixing are kept exactly so the output matches the C
// decoder bit for bit; function names are kept from the C source next to each method.
namespace VgmstreamBinka;

/// <summary>Which container the packets come from (binka_mode_t).</summary>
public enum BinkaMode
{
    /// <summary>BCF1 (Bink Audio 1.x files).</summary>
    Bcf1,
    /// <summary>Unreal Engine "UEBA" (Bink Audio 2.0 bitstream).</summary>
    Ueba,
}

/// <summary>LSB-first bit reader (bitstream_lsb.h, read functions only).</summary>
internal unsafe struct BitstreamLsb
{
    private static readonly uint[] MaskTableLsb =
    [
        0x00000000, 0x00000001, 0x00000003, 0x00000007, 0x0000000f, 0x0000001f, 0x0000003f, 0x0000007f, 0x000000ff,
        0x000001ff, 0x000003ff, 0x000007ff, 0x00000fff, 0x00001fff, 0x00003fff, 0x00007fff, 0x0000ffff, 0x0001ffff,
        0x0003ffff, 0x0007ffff, 0x000fffff, 0x001fffff, 0x003fffff, 0x007fffff, 0x00ffffff, 0x01ffffff, 0x03ffffff,
        0x07ffffff, 0x0fffffff, 0x1fffffff, 0x3fffffff, 0x7fffffff, 0xffffffff,
    ];

    private byte* _buf;
    private uint _bMax;
    private uint _bOff;
    public bool Error;

    /// <summary>bl_setup</summary>
    public void Setup(byte* buf, uint bufsize)
    {
        _buf = buf;
        _bMax = bufsize * 8;
        _bOff = 0;
        Error = false;
    }

    /// <summary>bl_skip</summary>
    public int Skip(uint bits)
    {
        if (_bOff + bits > _bMax)
        {
            Error = true;
            return 0;
        }
        _bOff += bits;
        return 1;
    }

    /// <summary>bl_align</summary>
    public void Align(uint bits)
    {
        if (bits == 0)
            return;
        int left = (int)(_bOff % bits);
        if (left == 0)
            return;
        Skip((uint)(bits - left));
    }

    /// <summary>bl_pos</summary>
    public int Pos => (int)_bOff;

    /// <summary>bl_get + bl_read</summary>
    public uint Read(uint bits)
    {
        if (bits > 32 || bits > _bMax - _bOff)
        {
            Error = true;
            return 0;
        }
        if (bits == 0)
            return 0;

        uint pos = _bOff / 8;
        int shift = (int)(_bOff % 8);
        uint mask = MaskTableLsb[bits];

        uint val = (uint)_buf[pos + 0] >> shift;
        if (bits + shift > 8)
        {
            val |= (uint)_buf[pos + 1] << (8 - shift);
            if (bits + shift > 16)
            {
                val |= (uint)_buf[pos + 2] << (16 - shift);
                if (bits + shift > 24)
                {
                    val |= (uint)_buf[pos + 3] << (24 - shift);
                    if (bits + shift > 32)
                    {
                        // upper bits are lost (shifting over 32)
                        val |= (uint)_buf[pos + 4] << (32 - shift);
                    }
                }
            }
        }

        _bOff += bits;
        return val & mask;
    }
}

/// <summary>A Bink Audio decoder for one stream (binka_handle_t and its API).</summary>
public sealed unsafe class BinkaHandle
{
    private const int MaxDecoders = 8;
    private const int MaxFrameChannels = 2;
    private const int MaxChannels = 8;
    private const int MinChannels = 1;
    private const int MaxSampleRate = 96000;
    private const int MinSampleRate = 300;
    private const int MaxFrameSamples = 2048;
    private const int MaxFrameOverlap = 128;
    private const int MaxBands = 26;

    private const uint FlagsDct = 1 << 0;
    private const uint FlagsOutputPlanar = 1 << 1;
    private const uint FlagsBinka2 = 1 << 2;

    /// <summary>binka_decoder_t</summary>
    private sealed class Decoder
    {
        public float[] Table = BinkaData.Cosines;
        public int FrameSamples;
        public float Scale;
        public int OverlapSamples;
        public int OverlapBits;
        public int FrameChannels;
        public bool IsFirstFrame;
        public int BandCount;
        public int TransformSize;
        public uint Flags;
        public readonly int[] BandThresholds = new int[MaxBands];
    }

    public int Channels { get; }
    public int SampleRate { get; }
    /// <summary>Samples per channel produced by each packet (binka_get_frame_samples).</summary>
    public int FrameSamples { get; }

    private readonly Decoder[] _decoders = new Decoder[MaxDecoders];
    private readonly float[] _samples;
    private readonly float[] _overlap;
    private readonly int _decoderCount;

    private BinkaHandle(int sampleRate, int channels, uint flags)
    {
        _samples = new float[MaxFrameSamples * channels];
        _overlap = new float[MaxFrameOverlap * channels];
        Channels = channels;
        SampleRate = sampleRate;
        _decoderCount = (channels + 1) / MaxFrameChannels;

        // base frame samples without overlap
        if (SampleRate < 22050)
            FrameSamples = 480;
        else if (SampleRate < 44100)
            FrameSamples = 960;
        else
            FrameSamples = 1920;

        // all decoders use 2ch except last
        int tempChannels = channels;
        for (int i = 0; i < _decoderCount; i++)
        {
            int frameChannels = tempChannels >= 2 ? 2 : 1;
            _decoders[i] = new Decoder();
            OpenDecoder(_decoders[i], sampleRate, frameChannels, flags);
            tempChannels -= 2;
        }

        Reset();
    }

    /// <summary>binka_init: null when the sample rate or channel count is out of range.</summary>
    public static BinkaHandle? Init(int sampleRate, int channels, BinkaMode mode)
    {
        if (sampleRate < MinSampleRate || sampleRate > MaxSampleRate)
            return null;
        if (channels < MinChannels || channels > MaxChannels)
            return null;

        uint flags = 0x00;
        switch (mode)
        {
            case BinkaMode.Bcf1:
                if (channels > 2)
                    flags |= FlagsOutputPlanar;
                flags |= FlagsDct;
                break;

            case BinkaMode.Ueba:
            {
                // In the 'UEBA open' method these flags are always set, maybe reserved
                bool uebaFlag1 = true;
                bool uebaFlag2 = true;

                if (!uebaFlag1)
                    flags |= FlagsOutputPlanar;
                if (uebaFlag2)
                    flags |= FlagsBinka2;

                // extra flags (not set but implicit in UEBA)
                flags |= FlagsDct;
                break;
            }

            default:
                return null;
        }

        return new BinkaHandle(sampleRate, channels, flags);
    }

    /// <summary>open_decoder</summary>
    private static void OpenDecoder(Decoder decoder, int sampleRate, int frameChannels, uint flags)
    {
        int frameSamples;
        if (sampleRate < 22050)
            frameSamples = 512;
        else if (sampleRate < 44100)
            frameSamples = 1024;
        else
            frameSamples = 2048;

        if ((flags & FlagsDct) == 0)
        {
            // Interleaved mode (mixed channel data, used for RDFT only?)
            sampleRate *= frameChannels;
            frameSamples *= frameChannels;
            frameChannels = 1; // only for calcs below
        }

        int frameSamplesHalf = frameSamples >> 1;
        int sampleRateHalf = (sampleRate + 1) >> 1;

        int bandCount = 0;
        while (bandCount < (MaxBands - 1))
        {
            if (BinkaData.CutoffFrequency[bandCount] >= sampleRateHalf)
                break;
            bandCount++;
        }

        if (frameChannels == 1)
            flags &= 0xFFFFFFFD; // remove FLAGS_OUTPUT_PLANAR

        decoder.Table = BinkaData.Cosines;
        decoder.TransformSize = 2048; // implicit in UE
        decoder.Flags = flags;
        decoder.OverlapSamples = frameSamples >> 4;
        decoder.FrameChannels = frameChannels;
        decoder.BandCount = bandCount;
        decoder.FrameSamples = frameSamples;

        decoder.OverlapBits = 0;
        switch (frameSamples >> 4)
        {
            case 32: decoder.OverlapBits = 5; break;
            case 64: decoder.OverlapBits = 6; break;
            case 128: decoder.OverlapBits = 7; break;
            case 256: decoder.OverlapBits = 8; break; // RDFT stereo only
        }

        // C: decoder->scale = 2.0 / sqrt(frame_samples);  (double math, stored as float)
        decoder.Scale = (float)(2.0 / Math.Sqrt(frameSamples));

        // calculate points where bands change
        Array.Clear(decoder.BandThresholds);
        for (int i = 0; i < bandCount; i++)
        {
            int bandLimit = frameSamplesHalf * BinkaData.CutoffFrequency[i] / sampleRateHalf;
            if (bandLimit == 0) // i = 0 only
                bandLimit = 1;
            decoder.BandThresholds[i] = bandLimit;
        }
        decoder.BandThresholds[bandCount] = frameSamplesHalf;

        decoder.IsFirstFrame = true;
    }

    /// <summary>apply_overlap</summary>
    private static void ApplyOverlap(float* dst, int overlapSamples, float* overlap, int overlapBits)
    {
        if (overlapBits == 0)
            return;

        for (int i = 0; i < overlapSamples; i++)
        {
            float s1 = overlap[i];
            float s2 = (i * (dst[i] - s1)) / overlapSamples;
            dst[i] = (s1 + s2);
        }
    }

    /// <summary>apply_scale</summary>
    private static void ApplyScale(float* coefs, float scale, int frameSamples)
    {
        for (int i = 0; i < frameSamples; i++)
            coefs[i] *= scale;
    }

    /// <summary>output_samples</summary>
    private static void OutputSamples(Decoder dec, float* coefs, float* overlap)
    {
        ApplyScale(coefs, dec.Scale, dec.FrameSamples * dec.FrameChannels);

        int outputSamples = dec.FrameSamples - dec.OverlapSamples;

        int currentOverlapBits = dec.OverlapBits;
        if (dec.IsFirstFrame)
        {
            dec.IsFirstFrame = false;
            currentOverlapBits = 0;
        }

        float* coefsCh = coefs;
        float* overlapCh = overlap;
        for (int ch = 0; ch < dec.FrameChannels; ch++)
        {
            ApplyOverlap(coefsCh, dec.OverlapSamples, overlapCh, currentOverlapBits);

            Buffer.MemoryCopy(&coefsCh[outputSamples], overlapCh, dec.OverlapSamples * sizeof(float), dec.OverlapSamples * sizeof(float));

            coefsCh += dec.FrameSamples;
            overlapCh += dec.OverlapSamples;
        }
    }

    /// <summary>get_float29: unpack Bink Audio's 29-bit floats.</summary>
    private static float GetFloat29(ref BitstreamLsb bs)
    {
        uint code = bs.Read(29);
        int power = (int)((code >> 0) & 0x1f);
        int mantissa = (int)((code >> 5) & 0x77FFFFF); // odd mask but upper bits aren't set anyway
        int sign = (int)((code >> 28) & 0x01);

        float value = (float)(mantissa * BinkaData.Float29Power[power]);
        if (sign != 0)
            value = -value;
        return value;
    }

    /// <summary>unpack_channel: unpack coefs for a single channel.</summary>
    private static bool UnpackChannel(float* coefs, int frameSamples, int bandCount, ref BitstreamLsb bs, int[] bandThresholds, bool isBinka2)
    {
        // read first 2 coefs as packed floats
        coefs[0] = GetFloat29(ref bs);
        coefs[1] = GetFloat29(ref bs);

        // read base scales per band
        uint iBits = isBinka2 ? 7u : 8u;
        float* scalefactors = stackalloc float[MaxBands];
        for (int i = 0; i < bandCount; i++)
        {
            int index = (int)bs.Read(iBits);
            if (index > 95)
                index = 95;
            scalefactors[i] = BinkaData.Scalefactors[index];
        }

        // setup initial band
        float bandScalefactor = 0.0f;
        int band = 0;

        // read codes for all bands
        int pos = 2; // after first two coefs (band 1)
        while (pos < frameSamples)
        {
            int end;

            int rleFlag = (int)bs.Read(1);
            if (rleFlag != 0)
            {
                int rleIndex = (int)bs.Read(4);
                end = pos + 8 * BinkaData.RleTable[rleIndex];
            }
            else
            {
                end = pos + 8;
            }
            if (end > frameSamples)
                end = frameSamples;

            // get size in the bitstream of coefs
            uint qBits = bs.Read(4);
            if (qBits > 0)
            {
                if (isBinka2)
                {
                    // read coefs
                    for (int subpos = pos; subpos < end; subpos++)
                    {
                        int value = (int)bs.Read(qBits);
                        coefs[subpos] = value;
                    }

                    // read signs
                    for (int subpos = pos; subpos < end; subpos++)
                    {
                        if (coefs[subpos] != 0.0)
                        {
                            int negative = (int)bs.Read(1);
                            if (negative != 0)
                                coefs[subpos] = -coefs[subpos];
                        }
                    }

                    // scale
                    while (pos < end)
                    {
                        // move band after reaching threshold
                        if (pos == bandThresholds[band] * 2)
                        {
                            bandScalefactor = scalefactors[band];
                            band++;
                        }

                        coefs[pos] *= bandScalefactor;
                        pos++;
                    }
                }
                else
                {
                    // read and scale coefs
                    while (pos < end)
                    {
                        if (pos == bandThresholds[band] * 2)
                        {
                            bandScalefactor = scalefactors[band];
                            band++;
                        }

                        int value = (int)bs.Read(qBits);
                        if (value != 0)
                        {
                            int negative = (int)bs.Read(1);

                            float coef = value * bandScalefactor;
                            if (negative != 0)
                                coef = -coef;
                            coefs[pos] = coef;
                        }
                        else
                        {
                            coefs[pos] = 0.0f;
                        }
                        pos++;
                    }
                }
            }
            else
            {
                // coefs not saved in the bitstream, set batch to 0
                for (int i = pos; i < end; i++)
                    coefs[i] = 0.0f;
                pos = end;

                // move band after reaching threshold
                while (end > bandThresholds[band] * 2)
                {
                    bandScalefactor = scalefactors[band];
                    band++;
                }
            }
        }

        return !bs.Error;
    }

    /// <summary>decode_frame: returns bytes consumed or -1.</summary>
    private static int DecodeFrame(Decoder dec, byte* src, int srcSize, float* coefs, float* overlap)
    {
        var bs = new BitstreamLsb();
        bs.Setup(src, (uint)srcSize);

        bool isDct = (dec.Flags & FlagsDct) != 0;
        bool isBinka2 = (dec.Flags & FlagsBinka2) != 0;

        if (isDct)
            bs.Skip(2); // OG lib simply skips this (always 0, reserved?)

        float* coefsTmp = coefs;
        fixed (float* table = dec.Table)
        {
            for (int ch = 0; ch < dec.FrameChannels; ch++)
            {
                bool ok = UnpackChannel(coefsTmp, dec.FrameSamples, dec.BandCount, ref bs, dec.BandThresholds, isBinka2);
                if (ok)
                {
                    if (isDct)
                        BinkaTransform.transform_dct(coefsTmp, dec.FrameSamples, dec.TransformSize, table);
                    else
                        BinkaTransform.transform_rdft(coefsTmp, dec.FrameSamples, dec.TransformSize, table);
                }
                else
                {
                    return -1;
                }

                coefsTmp += dec.FrameSamples;
            }
        }

        OutputSamples(dec, coefs, overlap);

        bs.Align(32);
        int bytesDone = bs.Pos / 8;
        return bytesDone;
    }

    /// <summary>binka_decode: decodes one packet (without the sync/size header of BCF1/UEBA) into
    /// <paramref name="dst"/> (FrameSamples * Channels interleaved floats, PCM16 scale). Returns samples per channel,
    /// or a negative value on error.</summary>
    public int Decode(ReadOnlySpan<byte> src, Span<float> dst)
    {
        if (dst.Length < FrameSamples * Channels)
            throw new ArgumentException("destination too small", nameof(dst));

        int srcSize = src.Length;
        fixed (byte* srcBase = src)
        fixed (float* samplesBase = _samples)
        fixed (float* overlapBase = _overlap)
        {
            byte* s = srcBase;
            float* samples = samplesBase;
            float* overlap = overlapBase;
            for (int i = 0; i < _decoderCount; i++)
            {
                Decoder decoder = _decoders[i];

                int bytesDone = DecodeFrame(decoder, s, srcSize, samples, overlap);
                if (bytesDone < 0)
                    return bytesDone;

                samples += decoder.FrameSamples * decoder.FrameChannels;
                overlap += decoder.OverlapSamples * decoder.FrameChannels;
                s += bytesDone;
                srcSize -= bytesDone;
            }
        }

        // packets seem aligned to 0x04, more is probably a bug
        if (srcSize > 0x04)
            return -srcSize;

        CopySamples(dst, _samples, _decoders[0].FrameSamples, FrameSamples, Channels);
        return FrameSamples;
    }

    /// <summary>copy_samples: 11...22...33... into 123123...</summary>
    private static void CopySamples(Span<float> dst, float[] src, int srcSamples, int dstSamples, int channels)
    {
        for (int ch = 0; ch < channels; ch++)
        {
            int dstI = ch;
            int srcI = srcSamples * ch;
            for (int s = 0; s < dstSamples; s++)
            {
                dst[dstI] = src[srcI];
                dstI += channels;
                srcI++;
            }
        }
    }

    /// <summary>binka_reset: the next frame is not windowed with the previous one.</summary>
    public void Reset()
    {
        for (int i = 0; i < _decoderCount; i++)
            _decoders[i].IsFirstFrame = true;
    }
}
