using System.Text;
using CUE4Parse.UE4.Lua.Archives;
using CUE4Parse.UE4.Lua.Readers;
using CUE4Parse.UE4.Versions;

namespace CUE4Parse.GameTypes.Tencent.RocoKingdomWorld.Lua;

public class FNRCLuaArchive(string name, byte[] data, VersionContainer? versions = null) : FLua54Archive(name, data, versions)
{
    // Strings are encrypted
    public override string ReadLuaString()
    {
        ulong size = ReadLuaInt();
        if (size <= 1)
            return string.Empty;

        int length = (int) size - 1;
        byte[] b = ReadBytes(length);

        int seed = length + 3;
        for (int i = 0; i < length; i++)
        {
            b[i] = (byte) (b[i] ^ (seed + i));
        }

        return Encoding.UTF8.GetString(b);
    }
}

public class NRCLuaReader
{
    private static readonly byte[] _opcodeMapping = [
        0, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00,
        00, 00, 0, 00, 0, 00, 00, 0, 00, 00, 00, 00, 00, 00, 00, 00,
        00, 00, 00, 00, 00, 0, 00, 00, 0, 00, 00, 0, 00, 00, 00, 00,
        00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 0, 00, 00, 00,
        0, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 00, 0, 00, 00, 00,
        00, 00, 00
    ];

    public static LuaBytecode ReadBytecode(FNRCLuaArchive Ar, bool usesNewHeader)
    {
        LuaHeader header;
        if (usesNewHeader)
        {
            header = FLua54Reader.DefaultHeader;
            header.Closure = Ar.Read<byte>();
        }
        else
        {
            header = FLua54Reader.ReadHeader(Ar);
            header.Version = 0x54; // Header is standard except the version
        }

        var result = new LuaBytecode
        {
            Header = header,
            MainFunc = ReadFunction(Ar)
        };

        return result;
    }

    private static LuaFunction ReadFunction(FNRCLuaArchive Ar)
    {
        var f = new LuaFunction
        {
            SourceName = Ar.ReadLuaString(),
            LineDefined = Ar.ReadLuaInt(),
            Code = [.. Ar.ReadLuaArray(() => Ar.ReadBytes(4)).SelectMany(x => x)],
            LastLineDefined = Ar.ReadLuaInt(), // Shuffled
            NumParams = Ar.Read<byte>(), // Shuffled
            Constants = Ar.ReadLuaArray(() => FLua54Reader.ReadConstant(Ar)),
            IsVarArg = Ar.Read<byte>() // Shuffled
        };

        f.Upvalues = ReadUpvalues(Ar, f);
        f.Protos = Ar.ReadLuaArray(() => ReadFunction(Ar));
        f.Debug = FLua54Reader.ReadDebug(Ar);

        MapOpcodes(f.Code); // Opcode is shuffled

        return f;
    }

    private static LuaUpvalue[] ReadUpvalues(FNRCLuaArchive Ar, LuaFunction f)
    {
        int sizeUp = (int) Ar.ReadLuaInt();
        Ar.Read<byte>(); // Shuffled
        f.MaxStackSize = 255; // Read above, but I had to increase it manually for some reason, something else might be wrong?
        var upvalues = new LuaUpvalue[sizeUp];
        for (int i = 0; i < sizeUp; i++)
        {
            upvalues[i] = FLua54Reader.ReadUpvalue(Ar);
        }
        return upvalues;
    }

    private static void MapOpcodes(byte[] code)
    {
        for (int i = 0; i < code.Length; i += 4)
        {
            uint instr = BitConverter.ToUInt32(code, i);
            byte opcode = (byte) (instr & 0x7F);
            byte mapped = _opcodeMapping[opcode];

            instr = (instr & ~0x7Fu) | (uint) (mapped & 0x7F);
            Array.Copy(BitConverter.GetBytes(instr), 0, code, i, 4);
        }
    }
}
