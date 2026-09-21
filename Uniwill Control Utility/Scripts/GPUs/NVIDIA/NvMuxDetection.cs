using System;
using System.Runtime.InteropServices;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Scripts.GPUs.NVIDIA
{
    /// <summary>
    /// GPU working mode detected from NVAPI.
    /// - Hybrid: dGPU outputs are unattached (Optimus mode, iGPU drives the panel)
    /// - dGPU Direct: dGPU outputs are physically connected (MUX switch closed)
    /// - Unknown: could not determine (no NVIDIA GPU, NVAPI unavailable)
    /// </summary>
    public enum GpuWorkingMode
    {
        Unknown,
        Hybrid,
        DGpuDirect
    }

    /// <summary>
    /// Detects the current GPU working mode (Hybrid vs dGPU Direct / MUX switch state)
    /// using NVAPI. Read-only — does not modify GPU state.
    ///
    /// On laptops with a hardware MUX switch:
    /// - Hybrid mode (Optimus): dGPU outputs are "unattached" — the iGPU drives the panel,
    ///   and the dGPU renders frames that are composited by the iGPU.
    /// - dGPU Direct (MUX closed): dGPU outputs are physically connected to the panel.
    ///
    /// Detection method:
    /// 1. NvAPI_GPU_GetConnectedDisplayIds — returns connected displays on the dGPU.
    ///    If count > 0 and isConnected=1 → dGPU Direct mode.
    ///    If count == 0 or all unconnected → likely Hybrid mode.
    /// 2. NvAPI_EnumNvidiaUnAttachedDisplayHandle — enumerates unattached dGPU outputs.
    ///    If handles found → confirms Hybrid mode (Optimus active).
    /// </summary>
    internal static class NvMuxDetection
    {
        // NVAPI interface IDs from nvapi_interface.h
        private const uint NvAPI_Initialize = 0x0150E828;
        private const uint NvAPI_EnumNvidiaUnAttachedDisplayHandle = 0x20DE9260;
        private const uint NvAPI_GPU_GetConnectedDisplayIds = 0x0078DBA2;

        private const int NVAPI_OK = 0;
        private const int NVAPI_END_ENUMERATION = 7;

        // NV_GPU_DISPLAYIDS struct (version 2)
        private const int NvGpuDisplayIdsVer = 0x00030002;

        private delegate int NvApiInitializeDelegate();
        private delegate int NvApiEnumUnAttachedDelegate(uint index, out IntPtr handle);
        private delegate int NvApiGetConnectedDisplayIdsDelegate(
            IntPtr gpuHandle,
            IntPtr displayIds,
            ref uint displayIdCount,
            uint flags);

        private static NvApiInitializeDelegate? _initFunc;
        private static NvApiEnumUnAttachedDelegate? _enumUnattachedFunc;
        private static NvApiGetConnectedDisplayIdsDelegate? _getConnectedIdsFunc;
        private static bool _functionsLoaded;

        /// <summary>
        /// Detects the current GPU working mode.
        /// </summary>
        /// <returns>GpuWorkingMode indicating Hybrid, dGPU Direct, or Unknown.</returns>
        public static GpuWorkingMode DetectMode()
        {
            try
            {
                EnsureFunctionsLoaded();

                if (_initFunc == null)
                    return GpuWorkingMode.Unknown;

                _initFunc();

                // Use the existing NvApiPrivate.TryGetFirstGpuHandle to get the dGPU handle
                if (!NvApiPrivate.TryGetFirstGpuHandle(out IntPtr gpuHandle))
                    return GpuWorkingMode.Unknown;

                // Step 1: Check for connected displays on the dGPU
                bool hasConnectedDisplays = HasConnectedDisplays(gpuHandle);

                // Step 2: Check for unattached displays (Optimus outputs)
                bool hasUnattachedDisplays = HasUnattachedDisplays();

                // Decision logic:
                // - Connected displays on dGPU → dGPU Direct (MUX closed)
                // - Unattached displays but no connected → Hybrid (Optimus)
                // - Neither → Unknown (could be desktop, could be driver issue)
                if (hasConnectedDisplays)
                    return GpuWorkingMode.DGpuDirect;

                if (hasUnattachedDisplays)
                    return GpuWorkingMode.Hybrid;

                // Fallback: if we have an NVIDIA GPU but neither connected nor unattached,
                // it's likely a desktop GPU or an error state.
                return GpuWorkingMode.Unknown;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogInfo($"NvMuxDetection: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                return GpuWorkingMode.Unknown;
            }
        }

        /// <summary>
        /// Checks if the dGPU has physically connected displays (MUX switch closed).
        /// Uses NvAPI_GPU_GetConnectedDisplayIds.
        /// </summary>
        private static bool HasConnectedDisplays(IntPtr gpuHandle)
        {
            if (_getConnectedIdsFunc == null)
                return false;

            // First call: get count (pass NULL for displayIds)
            uint count = 0;
            int status = _getConnectedIdsFunc(gpuHandle, IntPtr.Zero, ref count, 0);

            if (status != NVAPI_OK || count == 0)
                return false;

            // Second call: get the actual display IDs
            int structSize = Marshal.SizeOf<int>() * 8; // NV_GPU_DISPLAYIDS is 20 bytes, but we just need version + displayId + isConnected
            // Actually the full struct is: version(4) + connectorType(4) + displayId(4) + flags(4) + reserved(4) = 20 bytes
            int nvGpuDisplayIdsSize = 20;
            IntPtr buffer = Marshal.AllocHGlobal(nvGpuDisplayIdsSize * (int)count);
            try
            {
                status = _getConnectedIdsFunc(gpuHandle, buffer, ref count, 0);
                if (status != NVAPI_OK || count == 0)
                    return false;

                // Check each display for isConnected bit
                for (uint i = 0; i < count; i++)
                {
                    IntPtr entryPtr = IntPtr.Add(buffer, (int)i * nvGpuDisplayIdsSize);
                    // The flags field is at offset 0x0C (12 bytes into the struct)
                    // isConnected is bit 6 of the flags field
                    int flags = Marshal.ReadInt32(IntPtr.Add(entryPtr, 12));
                    bool isConnected = (flags & (1 << 6)) != 0; // bit 6 = isConnected

                    if (isConnected)
                        return true;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return false;
        }

        /// <summary>
        /// Checks if there are unattached dGPU outputs (Optimus/Hybrid mode).
        /// Uses NvAPI_EnumNvidiaUnAttachedDisplayHandle.
        /// </summary>
        private static bool HasUnattachedDisplays()
        {
            if (_enumUnattachedFunc == null)
                return false;

            // Try to enumerate unattached displays (index 0)
            int status = _enumUnattachedFunc(0, out IntPtr handle);

            return status == NVAPI_OK;
        }

        private static void EnsureFunctionsLoaded()
        {
            if (_functionsLoaded)
                return;

            try
            {
                IntPtr initPtr = NvApiPrivate.QueryInterface(NvAPI_Initialize);
                if (initPtr != IntPtr.Zero)
                    _initFunc = Marshal.GetDelegateForFunctionPointer<NvApiInitializeDelegate>(initPtr);

                IntPtr enumPtr = NvApiPrivate.QueryInterface(NvAPI_EnumNvidiaUnAttachedDisplayHandle);
                if (enumPtr != IntPtr.Zero)
                    _enumUnattachedFunc = Marshal.GetDelegateForFunctionPointer<NvApiEnumUnAttachedDelegate>(enumPtr);

                IntPtr connectedPtr = NvApiPrivate.QueryInterface(NvAPI_GPU_GetConnectedDisplayIds);
                if (connectedPtr != IntPtr.Zero)
                    _getConnectedIdsFunc = Marshal.GetDelegateForFunctionPointer<NvApiGetConnectedDisplayIdsDelegate>(connectedPtr);
            }
            catch
            {
                // Ignore loading failures — DetectMode will return Unknown
            }

            _functionsLoaded = true;
        }
    }
}
