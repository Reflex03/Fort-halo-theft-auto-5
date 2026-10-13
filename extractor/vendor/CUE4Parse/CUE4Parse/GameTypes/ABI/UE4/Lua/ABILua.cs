using System.Text;
using CUE4Parse.UE4.Lua.Archives;
using CUE4Parse.UE4.Lua.Readers;
using CUE4Parse.UE4.Lua.Writers;
using CUE4Parse.UE4.Versions;

namespace CUE4Parse.GameTypes.ABI.UE4.Lua;

public class FABILua54Archive(string name, byte[] data, bool isMobile, VersionContainer? versions = null) : FLua54Archive(name, data, versions)
{
    private static readonly byte[] _desktopKey = Encoding.ASCII.GetBytes("00000000");
    private static readonly byte[] _mobileKey = Encoding.ASCII.GetBytes("00000000"); // "00000000000" truncated

    private readonly byte[] _key = isMobile ? _mobileKey : _desktopKey;

    public override T Read<T>()
    {
        if (typeof(T) == typeof(byte))
        {
            byte raw = base.Read<byte>();
            byte dec = (byte) (raw ^ _key[0]);
            return (T) (object) dec;
        }

        return base.Read<T>();
    }

    public override byte[] ReadBytes(int length)
    {
        var enc = base.ReadBytes(length);
        var dec = new byte[enc.Length];
        var prev = _key[0];
        var mask = _key.Length;
        for (int i = 0; i < enc.Length; i++)
        {
            var k = (byte) ((_key[i % mask] | prev) & 0xFF);
            var plain = (byte) (enc[i] ^ k);
            dec[i] = plain;
            prev = plain;
        }

        return dec;
    }

    public override ulong ReadLuaInt()
    {
        ulong v = 0;
        while (true)
        {
            var b = base.Read<byte>(); // Not encrypted

            v = (v << 7) | (uint) (b & 0x7F);

            if ((b & 0x80) != 0)
                break;
        }

        return v;
    }
}

public static class ABILuaReader
{
    private const byte LuaVersion = 0x54;
    private static readonly byte[] _opcodeTable =
    [
        0, 0, 00, 00, 00, 00, 00, 00, 0, 0, 0, 0, 0, 00, 00, 00,
        00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00,
        00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00,
        00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00,
        00, 00, 00, 00, 00, 0, 00, 00, 0, 00, 00, 00, 00, 0, 00, 00,
        00, 00, 00
    ];
    private static readonly Dictionary<byte, byte> _opcodeMapping =
        _opcodeTable.Select((mapped, opcode) => (opcode, mapped)).ToDictionary(x => (byte) x.opcode, x => x.mapped);

    public static byte[] DecryptLuaBytecode(byte[] bytes, bool isMobile)
    {
        using var Ar = new FABILua54Archive("ABILua", bytes, isMobile);
        return new FLuaWriter54(ReadLuaBytecode(Ar)).GetBuffer();
    }

    private static LuaBytecode ReadLuaBytecode(FABILua54Archive Ar) => new()
    {
        Header = ReadHeader(Ar),
        MainFunc = FLua54Reader.ReadFunction(Ar, _opcodeMapping)
    };

    private static LuaHeader ReadHeader(FABILua54Archive Ar)
    {
        Ar.ReadBytes(4); // Custom magic
        Ar.Read<byte>(); // Custom version
        var format = Ar.Read<byte>();
        Ar.ReadBytes(6); // Obfuscated luacData
        var instructionSize = Ar.Read<byte>();
        var integerSize = Ar.Read<byte>();
        var numberSize = Ar.Read<byte>();
        Ar.ReadBytes(8); // Obfuscated luacInt
        Ar.ReadBytes(8); // Obfuscated luacNum
        var closure = Ar.Read<byte>();

        return new LuaHeader
        {
            Signature = FLuaReader.LUA_SIGNATURE,
            Version = LuaVersion,
            Format = format,
            LuacData = FLuaReader.LUAC_DATA,
            InstructionSize = instructionSize,
            IntegerSize = integerSize,
            NumberSize = numberSize,
            LuacInt = FLuaReader.LUAC_INT,
            LuacNum = FLuaReader.LUAC_NUM,
            Closure = closure
        };
    }
}
