using System.Runtime.InteropServices;

namespace UCU.Hardware.Gpu
{
    /// <summary>
    /// One loaded <c>nvapi64.dll</c>: the id → entry point table of a single NVAPI session.
    /// Alive for exactly one operation (see <see cref="NvApiModule"/>).
    /// </summary>
    public interface INvApiModuleTable : IDisposable
    {
        /// <summary><c>nvapi_QueryInterface</c>; false when the driver does not export the id.</summary>
        bool TryQuery(uint id, out IntPtr entry);
    }

    /// <summary>The module load behind a seam, so a test can script a stale session.</summary>
    public interface INvApiModuleLoader
    {
        /// <summary>A freshly loaded nvapi64, or false with the reason the load failed.</summary>
        bool TryLoad(out INvApiModuleTable? table, out string? reason);
    }

    /// <summary>
    /// The scope a scripted table answers <c>Open()</c> with: a fake resolves no pointers, so
    /// there is no module to free.
    /// </summary>
    internal sealed class NvApiNoScope : IDisposable
    {
        internal static readonly NvApiNoScope Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// <c>nvapi64.dll</c> loaded for one operation and freed at its end. NVAPI binds its
    /// session to the display driver instance it first meets there; the engine's own
    /// <c>pnputil /restart-device</c> replaces that instance, and a session that outlived it
    /// answers <c>NVAPI_DEVICE_NOT_FOUND</c> (-6) for the rest of the process — field 0.9.12,
    /// where every NVAPI-backed step of the unlock failed after the restart. Re-initialising
    /// cannot revive such a session; only binding a new one to the driver that is current now
    /// can. So the module is a load/free pair like the reference's NVML use, not a process
    /// singleton. The load is explicit and System32-only: a process-wide
    /// <c>[DllImport]</c> keeps one handle (and one dead session) for its whole life.
    /// </summary>
    public sealed class NvApiModule : INvApiModuleTable
    {
        /// <summary>NVAPI_DEVICE_NOT_FOUND: the driver instance this session bound to is gone.</summary>
        public const int DeviceNotFound = -6;

        /// <summary>NVAPI_INVALID_HANDLE: a handle the session that went away had issued.</summary>
        public const int InvalidHandle = -8;

        /// <summary>The loader the transports use unless a test scripts one.</summary>
        public static readonly INvApiModuleLoader Loader = new Win32Loader();

        /// <summary>LOAD_LIBRARY_SEARCH_SYSTEM32: no other directory is on the search path.</summary>
        private const uint SearchSystem32 = 0x00000800;

        private IntPtr _module;
        private IntPtr _query;

        private NvApiModule(IntPtr module, IntPtr query)
        {
            _module = module;
            _query = query;
        }

        /// <summary>True for the statuses that mean the session lost its driver instance.</summary>
        public static bool IsStale(int status) => status == DeviceNotFound || status == InvalidHandle;

        public bool TryQuery(uint id, out IntPtr entry)
        {
            entry = IntPtr.Zero;
            if (_query == IntPtr.Zero)
                return false;

            entry = Marshal.GetDelegateForFunctionPointer<QueryInterfaceCall>(_query)(id);
            return entry != IntPtr.Zero;
        }

        /// <summary>Frees the module. Idempotent: a second call is a no-op, never a double free.</summary>
        public void Dispose()
        {
            IntPtr module = _module;
            _module = IntPtr.Zero;
            _query = IntPtr.Zero;

            if (module != IntPtr.Zero)
                FreeLibrary(module);
        }

        private sealed class Win32Loader : INvApiModuleLoader
        {
            public bool TryLoad(out INvApiModuleTable? table, out string? reason)
            {
                table = null;

                IntPtr module = LoadLibraryExW("nvapi64.dll", IntPtr.Zero, SearchSystem32);
                if (module == IntPtr.Zero)
                {
                    reason = $"nvapi64.dll could not be loaded (error {Marshal.GetLastWin32Error()})";
                    return false;
                }

                IntPtr query = GetProcAddress(module, "nvapi_QueryInterface");
                if (query == IntPtr.Zero)
                {
                    FreeLibrary(module);
                    reason = "nvapi64.dll does not export nvapi_QueryInterface";
                    return false;
                }

                table = new NvApiModule(module, query);
                reason = null;
                return true;
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr QueryInterfaceCall(uint id);

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);
    }
}
