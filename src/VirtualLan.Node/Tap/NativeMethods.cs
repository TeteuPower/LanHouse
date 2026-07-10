using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace VirtualLan.Node.Tap;

[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    // --- CreateFile ---
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;
    internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    /// <summary>
    /// IOCTLs do tap-windows6. Codificação: CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, function, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    /// = (0x22 &lt;&lt; 16) | (function &lt;&lt; 2)
    /// </summary>
    internal static class Ioctl
    {
        private const uint FileDeviceUnknown = 0x00000022;

        private static uint CtlCode(uint function) => (FileDeviceUnknown << 16) | (function << 2);

        internal static readonly uint GetMac = CtlCode(1);            // 0x220004
        internal static readonly uint GetVersion = CtlCode(2);        // 0x220008
        internal static readonly uint GetMtu = CtlCode(3);            // 0x22000C
        internal static readonly uint SetMediaStatus = CtlCode(6);    // 0x220018
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref int lpInBuffer,
        int nInBufferSize,
        nint lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        nint lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        int nInBufferSize,
        [Out] byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        nint lpOverlapped);
}
