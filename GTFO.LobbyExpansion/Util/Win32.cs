using System.Runtime.InteropServices;

namespace GTFO.LobbyExpansion.Util;

public static class Win32
{
    [Flags]
    public enum MemoryProtections : uint
    {
        NoAccess = 0x01,
        ReadOnly = 0x02,
        ReadWrite = 0x04,
        WriteCopy = 0x08,
        Execute = 0x10,
        ExecuteRead = 0x20,
        ExecuteReadWrite = 0x40,
        ExecuteWriteCopy = 0x80,
        GuardModifierflag = 0x100,
        NoCacheModifierflag = 0x200,
        WriteCombineModifierflag = 0x400,
    }

    [DllImport("kernel32.dll", EntryPoint = "VirtualProtect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeVirtualProtect(
        IntPtr lpAddress,
        IntPtr dwSize,
        MemoryProtections flNewProtect,
        out MemoryProtections lpflOldProtect);

    public static bool VirtualProtect(
        IntPtr lpAddress,
        IntPtr dwSize,
        MemoryProtections flNewProtect,
        out MemoryProtections lpflOldProtect)
    {
        if (lpAddress == IntPtr.Zero)
            throw new ArgumentException($"{nameof(lpAddress)} cannot be zero.", nameof(lpAddress));

        if (dwSize.ToInt64() <= 0)
            throw new ArgumentOutOfRangeException(nameof(dwSize), "The memory region size must be greater than zero.");

        return NativeVirtualProtect(lpAddress, dwSize, flNewProtect, out lpflOldProtect);
    }
}
