using Blake3;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Exceptions;
using GenericReader;
using Newtonsoft.Json;

namespace CUE4Parse.GameTypes.Aion2.Objects;

[JsonConverter(typeof(FAion2KeyManifestFileConverter))]
public class FAion2KeyManifestFile
{
    private static readonly byte[] _manifestHashKey =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    public Dictionary<ulong, FAesKey> AesKeys = [];
    private const int PayloadOffset = 8;

    public FAion2KeyManifestFile(GameFile file)
    {
        var data = file.SafeRead() ?? throw new ParserException("Unable to read key_manifest.dat");

        if (data.Length < 8 || (data.Length - 8) % 0x30 != 0)
            throw new ParserException("Invalid AION2 key_manifest.dat header");

        var count = (data.Length - PayloadOffset) / 0x30;

        using var hasher = Hasher.New();
        hasher.Update(_manifestHashKey);
        var manifestKey = new FAesKey(hasher.Finalize().AsSpan().ToArray());
        var decrypted = data.Decrypt(PayloadOffset, count * 0x30, manifestKey);
        using var decryptedAr = new GenericBufferReader(decrypted);
        AesKeys = new Dictionary<ulong, FAesKey>(count);
        for (var i = 0; i < count; i++)
        {
            var seed = decryptedAr.Read<ulong>();
            var key = decryptedAr.ReadArray<byte>(32);
            decryptedAr.Position += 8;
            AesKeys[seed] = new FAesKey(key);
        }
    }
}

public class FAion2KeyManifestFileConverter : JsonConverter<FAion2KeyManifestFile>
{
    public override FAion2KeyManifestFile? ReadJson(JsonReader reader, Type objectType, FAion2KeyManifestFile? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        throw new NotImplementedException();
    }

    public override void WriteJson(JsonWriter writer, FAion2KeyManifestFile? value, JsonSerializer serializer)
    {
        writer.WriteStartObject();

        writer.WritePropertyName(nameof(value.AesKeys));
        serializer.Serialize(writer, value?.AesKeys);

        writer.WriteEndObject();
    }
}
