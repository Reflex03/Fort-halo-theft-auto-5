using System.Text;

namespace CUE4Parse.UE4.FMod;

/// <summary>
/// Known encryption keys:
///
/// - Godbreakers: (key removed in this vendored copy)
/// - The Darkest Files: (key removed in this vendored copy)
/// - PAPERHEAD EP0: (key removed in this vendored copy)
/// - Delverium Demo: (key removed in this vendored copy)
/// - Mr. Nomad Demo: (key removed in this vendored copy)
///
/// Credits to https://github.com/vgmstream/vgmstream/blob/master/src/meta/fsb_keys.h and other sources:
/// - Double Fine Productions: (key removed in this vendored copy)
/// - DJ Hero 2 (X360): (key removed in this vendored copy)
/// - N++ (PC?): (key removed in this vendored copy)
/// - Slightly Mad Studios (Project CARS, World of Speed): (key removed in this vendored copy)
/// - Ghost in the Shell: First Assault (PC): (key removed in this vendored copy)
/// - RevHeadz Engine Sounds (Mobile): (key removed in this vendored copy)
/// - Dark Souls 3 (PC): (key removed in this vendored copy)
/// - Need for Speed Shift 2 Unleashed (PC): (key removed in this vendored copy)
/// - Mortal Kombat X/XL (PC): (key removed in this vendored copy)
/// - Mirror War: Reincarnation of Holiness (PC): (key removed in this vendored copy)
/// - Xian Xia Chuan (PC): (key removed in this vendored copy)
/// - Critter Crunch / Superbrothers: Sword & Sworcery (PC): (key removed in this vendored copy)
/// - Cyphers: (key removed in this vendored copy)
/// - Xuan Dou Zhi Wang / King of Combat: (key removed in this vendored copy)
/// - Ji Feng Zhi Ren / Kritika Online: (key removed in this vendored copy)
/// - Invisible Inc. (PC?): (key removed in this vendored copy)
/// - Guitar Hero 3: (key removed in this vendored copy)
/// - Supreme Commander 2 (PC): (key removed in this vendored copy)
/// - Cookie Run: Ovenbreak: (key removed in this vendored copy)
/// - Monster Jam (PS2): (key removed in this vendored copy)
/// - Sekiro: Shadows Die Twice (PC): (key removed in this vendored copy)
/// - SCP: Unity (PC): (key removed in this vendored copy)
/// - Worms Rumble Beta (PC): (key removed in this vendored copy)
/// - Bubble Fighter (PC): (key removed in this vendored copy)
/// - Fall Guys (PC) ~2021-11: (key removed in this vendored copy)
/// - Fall Guys (PC) ~2022-07: (key removed in this vendored copy)
/// - Fall Guys (PC) ~2023-05: (key removed in this vendored copy)
/// - Achilles: Legends Untold (PC): (key removed in this vendored copy)
/// - Cult of the Lamb Demo (PC): (key removed in this vendored copy)
/// - Signalis (PC): (key removed in this vendored copy)
/// - Ash Echoes beta (Android): (key removed in this vendored copy)
/// - Afterimage demo (PC): (key removed in this vendored copy)
/// - Blanc (PC/Switch): (key removed in this vendored copy)
/// - Nishuihan Mobile (Android): (key removed in this vendored copy)
/// - Forza Motorsport (PC): (key removed in this vendored copy)
/// - JDM: Japanese Drift Master (PC): (key removed in this vendored copy)
/// - Ys Online: The Call of Solum (PC): (key removed in this vendored copy)
/// - Test Drive: Ferrari Racing Legends (PC): (key removed in this vendored copy)
/// - Hello Kitty: Island Adventure (iOS): (key removed in this vendored copy)
/// - Rivals of Aether 2 (PC): (key removed in this vendored copy)
/// - Final Fantasy XV: War for Eos (Android): (key removed in this vendored copy)
/// - Forza Motorsport 2023 (PC): (key removed in this vendored copy)
/// - AirRider CrazyRacing (PC): (key removed in this vendored copy)
/// - Wanderstop (PC): (key removed in this vendored copy)
/// - UNBEATABLE Demo (PC): (key removed in this vendored copy)
/// - Rennsport (PC): (key removed in this vendored copy)
/// - Gunner, HEAT, PC! (PC): (key removed in this vendored copy)
/// - Duet Night Abyss (PC) beta: (key removed in this vendored copy)
/// </summary>
public class Fsb5Decryption
{
    
    private static readonly string FSB5Header = "FSB5";
    private static readonly byte[] ReverseBitsTable =
    [
        0x00, 0x80, 0x40, 0xC0, 0x20, 0xA0, 0x60, 0xE0, 0x10, 0x90, 0x50, 0xD0, 0x30, 0xB0, 0x70, 0xF0,
        0x08, 0x88, 0x48, 0xC8, 0x28, 0xA8, 0x68, 0xE8, 0x18, 0x98, 0x58, 0xD8, 0x38, 0xB8, 0x78, 0xF8,
        0x04, 0x84, 0x44, 0xC4, 0x24, 0xA4, 0x64, 0xE4, 0x14, 0x94, 0x54, 0xD4, 0x34, 0xB4, 0x74, 0xF4,
        0x0C, 0x8C, 0x4C, 0xCC, 0x2C, 0xAC, 0x6C, 0xEC, 0x1C, 0x9C, 0x5C, 0xDC, 0x3C, 0xBC, 0x7C, 0xFC,
        0x02, 0x82, 0x42, 0xC2, 0x22, 0xA2, 0x62, 0xE2, 0x12, 0x92, 0x52, 0xD2, 0x32, 0xB2, 0x72, 0xF2,
        0x0A, 0x8A, 0x4A, 0xCA, 0x2A, 0xAA, 0x6A, 0xEA, 0x1A, 0x9A, 0x5A, 0xDA, 0x3A, 0xBA, 0x7A, 0xFA,
        0x06, 0x86, 0x46, 0xC6, 0x26, 0xA6, 0x66, 0xE6, 0x16, 0x96, 0x56, 0xD6, 0x36, 0xB6, 0x76, 0xF6,
        0x0E, 0x8E, 0x4E, 0xCE, 0x2E, 0xAE, 0x6E, 0xEE, 0x1E, 0x9E, 0x5E, 0xDE, 0x3E, 0xBE, 0x7E, 0xFE,
        0x01, 0x81, 0x41, 0xC1, 0x21, 0xA1, 0x61, 0xE1, 0x11, 0x91, 0x51, 0xD1, 0x31, 0xB1, 0x71, 0xF1,
        0x09, 0x89, 0x49, 0xC9, 0x29, 0xA9, 0x69, 0xE9, 0x19, 0x99, 0x59, 0xD9, 0x39, 0xB9, 0x79, 0xF9,
        0x05, 0x85, 0x45, 0xC5, 0x25, 0xA5, 0x65, 0xE5, 0x15, 0x95, 0x55, 0xD5, 0x35, 0xB5, 0x75, 0xF5,
        0x0D, 0x8D, 0x4D, 0xCD, 0x2D, 0xAD, 0x6D, 0xED, 0x1D, 0x9D, 0x5D, 0xDD, 0x3D, 0xBD, 0x7D, 0xFD,
        0x03, 0x83, 0x43, 0xC3, 0x23, 0xA3, 0x63, 0xE3, 0x13, 0x93, 0x53, 0xD3, 0x33, 0xB3, 0x73, 0xF3,
        0x0B, 0x8B, 0x4B, 0xCB, 0x2B, 0xAB, 0x6B, 0xEB, 0x1B, 0x9B, 0x5B, 0xDB, 0x3B, 0xBB, 0x7B, 0xFB,
        0x07, 0x87, 0x47, 0xC7, 0x27, 0xA7, 0x67, 0xE7, 0x17, 0x97, 0x57, 0xD7, 0x37, 0xB7, 0x77, 0xF7,
        0x0F, 0x8F, 0x4F, 0xCF, 0x2F, 0xAF, 0x6F, 0xEF, 0x1F, 0x9F, 0x5F, 0xDF, 0x3F, 0xBF, 0x7F, 0xFF
    ];

    public static bool IsFSB5Header(Stream stream)
    {
        long savedPos = stream.Position;
        Span<byte> header = stackalloc byte[4];
        stream.ReadExactly(header);
        stream.Position = savedPos;
        return Encoding.ASCII.GetString(header) == FSB5Header;
    }

    public static Stream Decrypt(Stream fsbStream, byte[]? key)
    {
        if (key == null || key.Length == 0)
            throw new ArgumentException("FSB5 is encrypted, but encryption key wasn't provided, cannot decrypt.", nameof(key));

        var decryptedStream = new Fsb5DecryptingStream(fsbStream, key, ReverseBitsTable);

        if (!IsFSB5Header(decryptedStream))
        {
            decryptedStream.Dispose();
            throw new Exception("Failed to decrypt FSB5, make sure encryption key is correct.");
        }

#if DEBUG
        Log.Debug("Decrypted FSB5 successfully");
#endif

        decryptedStream.Position = 0;
        return decryptedStream;
    }
}

public class Fsb5DecryptingStream : Stream
{
    private readonly Stream BaseStream;
    private readonly byte[] Key;
    private readonly byte[] ReverseBitsTable;

    public Fsb5DecryptingStream(Stream baseStream, byte[] key, byte[] reverseTable)
    {
        BaseStream = baseStream;
        Key = key;
        ReverseBitsTable = reverseTable;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long startPosition = BaseStream.Position;
        int bytesRead = BaseStream.Read(buffer, offset, count);

        for (int i = 0; i < bytesRead; i++)
            buffer[offset + i] = (byte) (ReverseBitsTable[buffer[offset + i]] ^ Key[(startPosition + i) % Key.Length]);

        return bytesRead;
    }

    public override bool CanRead => BaseStream.CanRead;
    public override bool CanSeek => BaseStream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => BaseStream.Length;
    public override long Position { get => BaseStream.Position; set => BaseStream.Position = value; }
    public override void Flush() => BaseStream.Flush();
    public override long Seek(long offset, SeekOrigin origin) => BaseStream.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
