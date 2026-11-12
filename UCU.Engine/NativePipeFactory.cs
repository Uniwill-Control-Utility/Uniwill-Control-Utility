using System.ComponentModel;
using System.Runtime.InteropServices;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;

namespace UCU.Engine
{
    /// <summary>
    /// Creates the engine's listening pipe handle with the DACL baked into creation.
    ///
    /// .NET 10 offers no managed route to a secured pipe: the PipeSecurity
    /// constructor overload was removed from NamedPipeServerStream, and
    /// SetAccessControl on an already-created pipe throws
    /// UnauthorizedAccessException (the handle CreateNamedPipe opens carries no
    /// WRITE_DAC right). The first field run proved it: the accept loop spun
    /// failing every second. CreateNamedPipeW with a SECURITY_DESCRIPTOR from the
    /// SDDL is the documented Win32 way, and it also removes the window where the
    /// pipe exists unmanaged.
    /// </summary>
    internal static class NativePipeFactory
    {
        private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        // The real Win32 instance cap: NamedPipeServerStream.MaxAllowedServerInstances
        // is a -1 "decide for me" sentinel on modern .NET, not the CreateNamedPipeW value.
        private const uint PipeMaxInstances = 0xFF;

        // dwPipeMode 0 = PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT.
        private const uint PipeModeByteByteWait = 0x00000000;

        private static readonly IntPtr InvalidHandleValue = new(-1);

        // The descriptor is immutable once built and CreateNamedPipeW copies it into the
        // pipe, so one instance per distinct SDDL serves the process. The accept loop used
        // to re-parse the SDDL (ConvertStringSecurityDescriptorToSecurityDescriptor plus an
        // HGlobal round trip and two frees) for every accepted connection.
        private static readonly object DescriptorSync = new();
        private static string? _cachedSddl;
        private static IntPtr _cachedDescriptor;
        private static IntPtr _cachedAttributes;

        /// <summary>
        /// Cached SECURITY_ATTRIBUTES for <paramref name="sddl"/> (process lifetime; the OS
        /// reclaims it at exit). Rebuilt only if a host asks for a different SDDL.
        /// </summary>
        private static IntPtr GetSecurityAttributes(string sddl)
        {
            lock (DescriptorSync)
            {
                if (_cachedDescriptor != IntPtr.Zero && string.Equals(_cachedSddl, sddl, StringComparison.Ordinal))
                    return _cachedAttributes;

                if (_cachedDescriptor != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_cachedAttributes);
                    LocalFree(_cachedDescriptor);
                    _cachedDescriptor = IntPtr.Zero;
                    _cachedAttributes = IntPtr.Zero;
                }

                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                        sddl, 1 /* SECURITY_DESCRIPTOR_REVISION */, out IntPtr descriptor, out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"pipe SDDL rejected: {sddl}");
                }

                // SECURITY_ATTRIBUTES must reach the call as a pointer: a
                // Nullable<SECURITY_ATTRIBUTES> parameter is not marshallable
                // (MarshalDirectiveException), and passing the struct by value
                // would hand the callee a copy that claims the wrong size.
                var attributes = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    lpSecurityDescriptor = descriptor,
                };

                IntPtr attributesPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
                Marshal.StructureToPtr(attributes, attributesPtr, fDeleteOld: false);

                _cachedSddl = sddl;
                _cachedDescriptor = descriptor;
                _cachedAttributes = attributesPtr;

                return attributesPtr;
            }
        }

        /// <summary>
        /// A byte-mode, overlapped server pipe with up to 255 instances.
        /// <paramref name="sddl"/> is the pipe's DACL; null applies the default
        /// descriptor (dev/test hosts).
        /// </summary>
        public static NamedPipeServerStream Create(string pipeName, string? sddl)
        {
            IntPtr saPtr = sddl is null ? IntPtr.Zero : GetSecurityAttributes(sddl);

            IntPtr handle = CreateNamedPipeW(
                $@"\\.\pipe\{pipeName}",
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                PipeModeByteByteWait,
                PipeMaxInstances,
                0, 0, 0, saPtr);

            if (handle == InvalidHandleValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"CreateNamedPipeW('{pipeName}') failed");

            var safeHandle = new SafePipeHandle(handle, ownsHandle: true);
            try
            {
                // The handle-taking constructor: the handle is created overlapped
                // (isAsync) and is a fresh listening instance (isConnected false).
                return new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    safeHandle);
            }
            catch
            {
                safeHandle.Dispose();
                throw;
            }
        }

        #region Win32

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateNamedPipeW(
            string lpName,
            uint dwOpenMode,
            uint dwPipeMode,
            uint nMaxInstances,
            uint nOutBufferSize,
            uint nInBufferSize,
            uint nDefaultTimeOut,
            IntPtr lpSecurityAttributes);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
            string stringSecurityDescriptor,
            uint securityDescriptorRevision,
            out IntPtr securityDescriptor,
            out IntPtr length);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        #endregion
    }
}
