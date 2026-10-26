using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UCU.Hardware.Ec
{
    /// <summary>
    /// Talks to UWACPIDriver.sys (bound to the ACPI device INOU0000) through
    /// \\.\ACPIDriver using the EC read/write IOCTLs.
    ///
    /// The IOCTL codes were recovered from MechControl's decompiled EcDevice.cs and
    /// are proven on NB05 (decimal 2621482120 read / 2621482124 write). This is the
    /// only code in the product that touches the driver, and it is the reason the
    /// process hosting it must run as LocalSystem: opening the device requires the
    /// driver's handle permissions, which is what the UI process no longer has.
    /// </summary>
    public sealed class EcDriverTransport : IEcTransport
    {
        /// <summary>Device path published by UWACPIDriver.sys.</summary>
        public const string AcpiDriverDevice = @"\\.\ACPIDriver";

        // IOCTL codes from MechControl's EcDevice.cs - proven to work on NB05.
        private const uint IOCTL_ECREAD = 0x9C40A488;
        private const uint IOCTL_ECWRITE = 0x9C40A48C;

        // File access/share flags
        private const uint GENERIC_READ_WRITE = 0xC0000000; // GENERIC_READ | GENERIC_WRITE
        private const uint FILE_SHARE_RW = 3;               // FILE_SHARE_READ | FILE_SHARE_WRITE
        private const uint OPEN_EXISTING = 3;

        private static readonly nint InvalidHandle = new(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            nint lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            nint hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            nint hDevice,
            uint dwIoControlCode,
            ref int lpInBuffer,
            int nInBufferSize,
            ref int lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            nint lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            nint hDevice,
            uint dwIoControlCode,
            int[] lpInBuffer,
            int nInBufferSize,
            ref int lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            nint lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(nint hObject);

        private nint _handle = InvalidHandle;
        private readonly int[] _writeBuffer = new int[2];
        private bool _disposed;

        public bool IsOpen => _handle != InvalidHandle;

        public void Open()
        {
            ThrowIfDisposed();

            if (_handle != InvalidHandle)
                return;

            _handle = OpenDriverHandle();
        }

        public void Reconnect()
        {
            ThrowIfDisposed();

            CloseCurrentHandle();
            _handle = OpenDriverHandle();
        }

        public byte Read(ushort address)
        {
            ThrowIfDisposed();

            int lpIn = address;
            int lpOut = 0;
            if (!DeviceIoControl(_handle, IOCTL_ECREAD, ref lpIn, 4, ref lpOut, 4, out _, nint.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"EC read failed at 0x{address:X3}");
            }

            return (byte)(lpOut & 0xFF);
        }

        public void Write(ushort address, byte value)
        {
            ThrowIfDisposed();

            _writeBuffer[0] = address;
            _writeBuffer[1] = value;
            int lpOut = 0;
            if (!DeviceIoControl(_handle, IOCTL_ECWRITE, _writeBuffer, 8, ref lpOut, 4, out _, nint.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"EC write failed at 0x{address:X3} value 0x{value:X2}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            CloseCurrentHandle();
        }

        /// <summary>Opens a handle to \\.\ACPIDriver for EC communication.</summary>
        private static nint OpenDriverHandle()
        {
            nint handle = CreateFileW(
                AcpiDriverDevice,
                GENERIC_READ_WRITE,
                FILE_SHARE_RW,
                nint.Zero,
                OPEN_EXISTING,
                0,
                nint.Zero);

            if (handle == InvalidHandle)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Failed to open {AcpiDriverDevice} (error {Marshal.GetLastWin32Error()}). " +
                    "Is the UWACPIDriver.sys kernel driver installed and started?");
            }

            return handle;
        }

        private void CloseCurrentHandle()
        {
            if (_handle == InvalidHandle)
                return;

            try { CloseHandle(_handle); } catch { /* ignore */ }
            _handle = InvalidHandle;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(EcDriverTransport));
        }
    }
}
