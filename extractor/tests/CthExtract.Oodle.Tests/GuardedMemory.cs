using System.Runtime.InteropServices;

namespace CthExtract.Oodle.Tests;

/// <summary>
/// Anonymous memory with an inaccessible (PROT_NONE) page directly before <see cref="Start"/> and directly after
/// <see cref="End"/>. Any read or write outside [Start, End) that crosses into a guard page kills the process with
/// SIGSEGV, which makes the test run fail loudly. Linux only (libc mmap/mprotect); elsewhere
/// <see cref="IsSupported"/> is false and the fuzzer falls back to ordinary padded buffers.
/// </summary>
internal sealed unsafe class GuardedMemory : IDisposable
{
    const int PROT_NONE = 0, PROT_READ = 1, PROT_WRITE = 2;
    const int MAP_PRIVATE = 0x02, MAP_ANONYMOUS = 0x20;

    [DllImport("libc", SetLastError = true)]
    static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, nint offset);

    [DllImport("libc", SetLastError = true)]
    static extern int mprotect(nint addr, nuint len, int prot);

    [DllImport("libc", SetLastError = true)]
    static extern int munmap(nint addr, nuint length);

    public static bool IsSupported => OperatingSystem.IsLinux() && (RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64);

    readonly nint _base;
    readonly nuint _total;

    /// <summary>First usable byte (page aligned, right after the leading guard page).</summary>
    public byte* Start { get; }
    /// <summary>One past the last usable byte (page aligned, start of the trailing guard page).</summary>
    public byte* End { get; }
    public int Capacity => (int)(End - Start);

    public GuardedMemory(int capacity)
    {
        if (!IsSupported) throw new PlatformNotSupportedException();
        nuint page = (nuint)Environment.SystemPageSize;
        nuint usable = ((nuint)Math.Max(capacity, 1) + page - 1) / page * page;
        _total = usable + 2 * page;
        _base = mmap(0, _total, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (_base == -1 || _base == 0)
            throw new OutOfMemoryException($"mmap failed: errno {Marshal.GetLastPInvokeError()}");
        if (mprotect(_base, page, PROT_NONE) != 0 || mprotect(_base + (nint)(page + usable), page, PROT_NONE) != 0)
            throw new InvalidOperationException($"mprotect failed: errno {Marshal.GetLastPInvokeError()}");
        Start = (byte*)(_base + (nint)page);
        End = Start + usable;
    }

    public void Dispose()
    {
        if (_base != 0)
            munmap(_base, _total);
    }
}
