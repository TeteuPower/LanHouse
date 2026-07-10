using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using VirtualLan.Core.Diagnostics;
using VirtualLan.Core.Net;

namespace VirtualLan.Node.Tap;

/// <summary>
/// Adaptador Ethernet virtual (tap-windows6) aberto para I/O de quadros crus.
///
/// Cada <see cref="ReadFrameAsync"/> devolve exatamente um quadro Ethernet; cada
/// <see cref="WriteFrameAsync"/> injeta exatamente um. O driver não faz nem espera framing
/// adicional, e não entrega FCS.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TapDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;

    public TapAdapterInfo Adapter { get; }

    /// <summary>MAC atribuído pelo driver ao adaptador. É a identidade L2 deste nó.</summary>
    public MacAddress MacAddress { get; }

    public Version DriverVersion { get; }

    private TapDevice(TapAdapterInfo adapter, SafeFileHandle handle, MacAddress mac, Version driverVersion)
    {
        Adapter = adapter;
        _handle = handle;
        MacAddress = mac;
        DriverVersion = driverVersion;

        // bufferSize 0 = sem buffer intermediário: queremos um read → um quadro.
        _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
    }

    public static TapDevice Open(TapAdapterInfo adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        var handle = NativeMethods.CreateFile(
            adapter.DevicePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            dwShareMode: 0,
            lpSecurityAttributes: 0,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_SYSTEM | NativeMethods.FILE_FLAG_OVERLAPPED,
            hTemplateFile: 0);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();

            string hint = error switch
            {
                5 => " (execute como Administrador)",
                32 => " (outro processo já está usando este adaptador — OpenVPN?)",
                _ => string.Empty,
            };

            throw new Win32Exception(error, $"Falha ao abrir {adapter.DevicePath}{hint}");
        }

        try
        {
            var driverVersion = QueryVersion(handle);
            var mac = QueryMac(handle);

            SetMediaStatus(handle, connected: true);

            Log.Info($"TAP aberto: {adapter.Name} mac={mac} driver={driverVersion}");
            return new TapDevice(adapter, handle, mac, driverVersion);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Diz ao driver para reportar "cabo conectado". Sem isto, a pilha IP do Windows
    /// considera o adaptador desconectado e não envia nada por ele.
    /// </summary>
    private static void SetMediaStatus(SafeFileHandle handle, bool connected)
    {
        int status = connected ? 1 : 0;

        if (!NativeMethods.DeviceIoControl(
                handle, NativeMethods.Ioctl.SetMediaStatus,
                ref status, sizeof(int),
                lpOutBuffer: 0, nOutBufferSize: 0,
                out _, lpOverlapped: 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "TAP_IOCTL_SET_MEDIA_STATUS falhou");
        }
    }

    private static MacAddress QueryMac(SafeFileHandle handle)
    {
        byte[] buffer = new byte[6];

        if (!NativeMethods.DeviceIoControl(
                handle, NativeMethods.Ioctl.GetMac,
                lpInBuffer: 0, nInBufferSize: 0,
                buffer, buffer.Length,
                out int returned, lpOverlapped: 0) || returned != 6)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "TAP_IOCTL_GET_MAC falhou");
        }

        return new MacAddress(buffer);
    }

    private static Version QueryVersion(SafeFileHandle handle)
    {
        byte[] buffer = new byte[12]; // 3 x ULONG: major, minor, debug

        if (!NativeMethods.DeviceIoControl(
                handle, NativeMethods.Ioctl.GetVersion,
                lpInBuffer: 0, nInBufferSize: 0,
                buffer, buffer.Length,
                out int returned, lpOverlapped: 0) || returned < 8)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "TAP_IOCTL_GET_VERSION falhou");
        }

        int major = BitConverter.ToInt32(buffer, 0);
        int minor = BitConverter.ToInt32(buffer, 4);
        return new Version(major, minor);
    }

    /// <summary>Lê um quadro Ethernet. Bloqueia (assincronamente) até haver um.</summary>
    public ValueTask<int> ReadFrameAsync(Memory<byte> destination, CancellationToken cancellationToken)
        => _stream.ReadAsync(destination, cancellationToken);

    /// <summary>Injeta um quadro Ethernet na pilha do Windows.</summary>
    public ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        => _stream.WriteAsync(frame, cancellationToken);

    public void Dispose()
    {
        try
        {
            if (!_handle.IsClosed) SetMediaStatus(_handle, connected: false);
        }
        catch (Win32Exception)
        {
            // Se o device já sumiu, não há o que desconectar.
        }

        _stream.Dispose();
        _handle.Dispose();
    }
}
