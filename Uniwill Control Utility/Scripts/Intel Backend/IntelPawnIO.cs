using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Views.Windows;

namespace Uniwill_Control_Utility.Scripts.Intel_Backend
{
    public partial class IntelPawnIo : IDisposable
    {
        private const int FunctionNameBytes = 32;

        private static readonly string[] DevicePaths =
        {
            @"\\?\GLOBALROOT\Device\PawnIO",
            @"\\.\PawnIO"
        };

        private const uint ShareReadWrite = 0x00000003;
        private const uint DeviceType = 41394u << 16;
        private const uint IoctlExecuteFn = 0x841 << 2;
        private const uint IoctlLoadBinary = 0x821 << 2;
        private const int E_HANDLE = unchecked((int)0x80070006);

        private readonly SafeFileHandle? _device;
        private static readonly Version? _installedVersion = ReadInstalledVersion();

        private IntelPawnIo(SafeFileHandle? deviceHandle)
        {
            _device = deviceHandle;
        }

        public static bool IsInstalled => Version != null;
        public static Version? Version => _installedVersion;
        public bool IsLoaded => _device != null && !_device.IsInvalid && !_device.IsClosed;

        public static IntelPawnIo LoadModuleFromFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path must not be empty.", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException("Intel PawnIO module not found.", filePath);

            return LoadModule(File.ReadAllBytes(filePath));
        }

        public static IntelPawnIo LoadModuleFromResource(Assembly assembly, string resourceName)
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return new IntelPawnIo(null);

            using var ms = new MemoryStream();
            stream.CopyTo(ms);

            return LoadModule(ms.ToArray());
        }

        private static IntelPawnIo LoadModule(byte[] moduleBytes)
        {
            foreach (string path in DevicePaths)
            {
                IntPtr raw = CreateFile(
                    path,
                    FileAccess.GENERIC_READ | FileAccess.GENERIC_WRITE,
                    ShareReadWrite,
                    IntPtr.Zero,
                    CreationDisposition.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (raw == IntPtr.Zero || raw.ToInt64() == -1)
                    continue;

                bool ok = DeviceIoControl(
                    raw,
                    ControlCode.LoadBinary,
                    moduleBytes,
                    (uint)moduleBytes.Length,
                    null,
                    0,
                    out _,
                    IntPtr.Zero);

                if (!ok)
                {
                    CloseHandle(raw);
                    continue;
                }

                return new IntelPawnIo(new SafeFileHandle(raw, ownsHandle: true));
            }

            return new IntelPawnIo(null);
        }

        public long[] Execute(string name, long[]? input, int outLength)
        {
            input ??= Array.Empty<long>();
            long[] output = new long[outLength];

            int hr = ExecuteHr(name, input, (uint)input.Length, output, (uint)outLength, out uint returned);

            if (hr != 0 || returned == 0)
                return output;

            if (returned < outLength)
            {
                long[] trimmed = new long[returned];
                Array.Copy(output, trimmed, returned);
                return trimmed;
            }

            return output;
        }

        public int ExecuteHr(
            string name,
            long[]? inBuffer,
            uint inSize,
            long[]? outBuffer,
            uint outSize,
            out uint returnSize)
        {
            ArgumentNullException.ThrowIfNull(name);

            inBuffer ??= Array.Empty<long>();
            outBuffer ??= Array.Empty<long>();

            if (!IsLoaded)
            {
                returnSize = 0;
                return E_HANDLE;
            }

            byte[] request = BuildRequest(name, inBuffer, inSize);
            byte[] response = outSize == 0 ? Array.Empty<byte>() : new byte[outSize * 8];

            bool ok = DeviceIoControl(
                _device!,
                ControlCode.Execute,
                request,
                (uint)request.Length,
                response,
                (uint)response.Length,
                out uint bytesReturned,
                IntPtr.Zero);

            if (!ok)
            {
                returnSize = 0;
                return Marshal.GetHRForLastWin32Error();
            }

            if (response.Length > 0 && outBuffer.Length > 0)
            {
                int copyBytes = Math.Min((int)bytesReturned, outBuffer.Length * 8);
                Buffer.BlockCopy(response, 0, outBuffer, 0, copyBytes);
            }

            returnSize = bytesReturned / 8;
            return 0;
        }

        private static byte[] BuildRequest(string functionName, long[] args, uint argCount)
        {
            byte[] buffer = new byte[FunctionNameBytes + (argCount * 8)];
            byte[] nameBytes = Encoding.ASCII.GetBytes(functionName);

            Buffer.BlockCopy(
                nameBytes,
                0,
                buffer,
                0,
                Math.Min(FunctionNameBytes - 1, nameBytes.Length));

            if (argCount > 0)
                Buffer.BlockCopy(args, 0, buffer, FunctionNameBytes, (int)argCount * 8);

            return buffer;
        }

        public void Close()
        {
            if (IsLoaded)
                _device!.Close();
        }

        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }

        private static Version? ReadInstalledVersion()
        {
            try
            {
                using RegistryKey? key =
                    Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");

                object? raw = key?.GetValue("DisplayVersion");
                return raw is string s && Version.TryParse(s, out Version? v) ? v : null;
            }
            catch
            {
                return null;
            }
        }

        private enum ControlCode : uint
        {
            LoadBinary = DeviceType | IoctlLoadBinary,
            Execute = DeviceType | IoctlExecuteFn
        }

        [Flags]
        private enum FileAccess : uint
        {
            GENERIC_READ = 0x80000000,
            GENERIC_WRITE = 0x40000000
        }

        private enum CreationDisposition : uint
        {
            OPEN_EXISTING = 3
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I4)]
        private static partial bool DeviceIoControl(
            SafeFileHandle device,
            ControlCode ioControlCode,
            [In] byte[]? inBuffer,
            uint inBufferSize,
            byte[]? outBuffer,
            uint nOutBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I4)]
        private static partial bool DeviceIoControl(
            IntPtr hDevice,
            ControlCode dwIoControlCode,
            byte[]? lpInBuffer,
            uint nInBufferSize,
            byte[]? lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr CreateFile(
            [MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
            FileAccess dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            CreationDisposition dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I4)]
        private static partial bool CloseHandle(IntPtr hObject);
    }

    internal sealed class IntelPawnIO : IDisposable
    {
        private const string IOCTL_READ_MSR = "ioctl_read_msr";
        private const string IOCTL_WRITE_MSR = "ioctl_write_msr";
        private const string IOCTL_GET_MCHBAR = "ioctl_get_mchbar_addr";
        private const string IOCTL_READ_DWORD = "ioctl_read_dword";
        private const string IOCTL_READ_QWORD = "ioctl_read_qword";

        private const string PCI_MUTEX_NAME = "Global\\Access_PCI";
        private const string MSR_MUTEX_NAME = "Global\\Access_MSR";

        private readonly IntelPawnIo _msrPawnIo;
        private readonly IntelPawnIo? _mchbarPawnIo;

        private Mutex? _pciMutex;
        private Mutex? _msrMutex;
        private bool _disposed;

        public IntelPawnIO(IntelPawnIo msrPawnIo, IntelPawnIo? mchbarPawnIo = null)
        {
            _msrPawnIo = msrPawnIo ?? throw new ArgumentNullException(nameof(msrPawnIo));
            _mchbarPawnIo = mchbarPawnIo;
        }

        public bool IsLoaded => _msrPawnIo.IsLoaded;
        public bool HasMchbar => _mchbarPawnIo != null && _mchbarPawnIo.IsLoaded;

        public void Open()
        {
            ThrowIfDisposed();

            _pciMutex ??= CreateOrOpenMutex(PCI_MUTEX_NAME);
            _msrMutex ??= CreateOrOpenMutex(MSR_MUTEX_NAME);
        }

        public void Close()
        {
            DisposeMutex(ref _pciMutex);
            DisposeMutex(ref _msrMutex);
        }

        public ulong ReadMsr(uint msr)
        {
            ThrowIfDisposed();

            if (!WaitForMutex(_msrMutex, 50))
                throw new InvalidOperationException("Could not obtain MSR mutex.");

            try
            {
                long[] output = _msrPawnIo.Execute(
                    IOCTL_READ_MSR,
                    new[] { unchecked((long)msr) },
                    1);

                return output.Length == 0 ? 0 : unchecked((ulong)output[0]);
            }
            finally
            {
                SafeReleaseMutex(_msrMutex);
            }
        }

        public void WriteMsr(uint msr, ulong value)
        {
            ThrowIfDisposed();

            if (!WaitForMutex(_msrMutex, 50))
                throw new InvalidOperationException("Could not obtain MSR mutex.");

            try
            {
                int hr = _msrPawnIo.ExecuteHr(
                    IOCTL_WRITE_MSR,
                    new[] { unchecked((long)msr), unchecked((long)value) },
                    2,
                    Array.Empty<long>(),
                    0,
                    out _);

                if (hr != 0)
                    throw new InvalidOperationException(
                        $"MSR write failed. MSR=0x{msr:X}, HRESULT=0x{hr:X8}");
            }
            finally
            {
                SafeReleaseMutex(_msrMutex);
            }
        }

        public ulong GetMchbarAddress()
        {
            ThrowIfDisposed();

            if (!HasMchbar)
                return 0;

            if (!WaitForMutex(_pciMutex, 50))
                return 0;

            try
            {
                long[] output = _mchbarPawnIo!.Execute(
                    IOCTL_GET_MCHBAR,
                    Array.Empty<long>(),
                    1);

                return output.Length == 0 ? 0 : unchecked((ulong)output[0]);
            }
            finally
            {
                SafeReleaseMutex(_pciMutex);
            }
        }

        public uint ReadMchbarDword(uint offset)
        {
            ThrowIfDisposed();

            if (!HasMchbar)
                return 0;

            ulong mchbar = GetMchbarAddress();
            if (mchbar == 0)
                return 0;

            long[] output = _mchbarPawnIo!.Execute(
                IOCTL_READ_DWORD,
                new[] { unchecked((long)(mchbar + offset)) },
                1);

            return output.Length == 0 ? 0 : unchecked((uint)output[0]);
        }

        public ulong ReadMchbarQword(uint offset)
        {
            ThrowIfDisposed();

            if (!HasMchbar)
                return 0;

            ulong mchbar = GetMchbarAddress();
            if (mchbar == 0)
                return 0;

            long[] output = _mchbarPawnIo!.Execute(
                IOCTL_READ_QWORD,
                new[] { unchecked((long)(mchbar + offset)) },
                1);

            return output.Length == 0 ? 0 : unchecked((ulong)output[0]);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Close();
            _disposed = true;
        }

        private static Mutex? CreateOrOpenMutex(string name)
        {
            try
            {
                return new Mutex(false, name);
            }
            catch
            {
                try { return Mutex.OpenExisting(name); }
                catch { return null; }
            }
        }

        private static bool WaitForMutex(Mutex? mutex, int timeoutMs)
        {
            if (mutex == null)
                return true;

            try { return mutex.WaitOne(timeoutMs, false); }
            catch (AbandonedMutexException) { return true; }
            catch { return false; }
        }

        private static void SafeReleaseMutex(Mutex? mutex)
        {
            try { mutex?.ReleaseMutex(); }
            catch { /* Mutex release failed; already released or owned by another thread */ }
        }

        private static void DisposeMutex(ref Mutex? mutex)
        {
            try { mutex?.Close(); }
            catch { /* Mutex dispose failed; already closed */ }
            finally { mutex = null; }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}