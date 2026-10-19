using System;
using System.Linq;
using System.Runtime.InteropServices;
using Serilog;

namespace Uniwill_Control_Utility.Services.Performance
{
    /// <summary>
    /// Writes formatted OSD text into RTSS shared memory so RTSS renders it
    /// inside hooked game processes (same mechanism MSI Afterburner / FanCtrl use).
    /// 
    /// This completely avoids topmost WPF windows, cross-GPU DWM composition,
    /// and all overlay-positioning issues — RTSS injects the text via its own
    /// DLL into the game's rendering pipeline.
    /// </summary>
    internal static partial class RtssOsdWriter
    {
        private const string SharedMemoryName = "RTSSSharedMemoryV2";
        private const uint ValidSignature = 0x52545353; // 'RTSS'
        private const uint MinVersion = 0x00020000;     // v2.0
        private const uint ExtendedTextVersion = 0x00020007; // v2.7 — 4096 char buffer
        private const uint LockingVersion = 0x0002000e; // v2.14 — dwBusy locking

        private const uint FileMapAllAccess = 0x001F;

        // Owner name claimed in OSD slot (slot 0 reserved for primary apps like MSI Afterburner).
        private static readonly byte[] OwnerName = System.Text.Encoding.ASCII.GetBytes("UCU");

        // Cached mapping to avoid repeated OpenFileMapping / MapViewOfFile calls.
        private static IntPtr _cachedMappingHandle;
        private static IntPtr _cachedViewPtr;
        private static int _claimedSlot; // -1 = not claimed

        /// <summary>
        /// Write an OSD string into RTSS shared memory.
        /// Returns true if the write succeeded (RTSS running, slot claimed, data written).
        /// </summary>
        internal static bool Update(string osdText)
        {
            if (!AcquireMapping(out IntPtr viewPtr, out uint version))
            {
                try { Log.Information("[RTSS] Update: AcquireMapping failed"); } catch { }
                return false;
            }

            if (!TryGetHeader(viewPtr, out RtssHeader header))
            {
                try { Log.Information("[RTSS] Update: TryGetHeader failed"); } catch { }
                return false;
            }

            // Find or claim a slot.
            int slot = FindOrClaimSlot(viewPtr, ref header);
            if (slot < 0)
            {
                try { Log.Information("[RTSS] Update: FindOrClaimSlot failed — no slot available"); } catch { }
                return false;
            }

            _claimedSlot = slot;

            // Build the byte array to write. Use Windows-1252 so that ° (U+00B0)
            // survives as byte 0xB0 which RTSS renders as the degree symbol.
            byte[] textBytes = System.Text.Encoding.GetEncoding(1252).GetBytes(osdText);
            int maxLen = version >= ExtendedTextVersion ? 4096 : 256;
            if (textBytes.Length > maxLen)
                textBytes = textBytes[..maxLen];

            unsafe
            {
                void* basePtr = (void*)viewPtr.ToPointer();
                byte* entryPtr = ((byte*)basePtr) + header.OsdArrOffset + (ulong)(slot * header.OsdEntrySize);

                // Write to extended or regular buffer. When the text is empty,
                // zero the entire buffer so RTSS clears any previously rendered
                // OSD text (including live replacement tags like <FR>).
                if (version >= ExtendedTextVersion)
                {
                    byte* szOsdEx = entryPtr + 512; // szOSD[256] + szOSDOwner[256] = 512
                    for (int i = 0; i < 4096; i++)
                        szOsdEx[i] = textBytes.Length > i ? textBytes[i] : (byte)0;
                }
                else
                {
                    byte* szOsd = entryPtr;
                    for (int i = 0; i < 256; i++)
                        szOsd[i] = textBytes.Length > i ? textBytes[i] : (byte)0;
                }

                // Increment the global OSD frame counter to signal RTSS.
                RtssHeader* hdr = (RtssHeader*)basePtr;
                hdr->OsdFrame++;

                // Reset dwBusy lock (v2.14+).
                if (version >= LockingVersion)
                {
                    long* dwBusy = (long*)((byte*)basePtr + 32); // offset of dwBusy
                    *dwBusy = 0;
                }

                // Flush so RTSS sees the new frame immediately.
                FlushViewOfFile((IntPtr)basePtr, IntPtr.Zero);
            }

            try { Log.Information("[RTSS] Update: wrote slot {Slot}, text {TextLength} chars", slot, osdText.Length); } catch { }
            return true;
        }

        /// <summary>
        /// Release the OSD slot by zeroing the entire entry so RTSS clears the
        /// rendered text. Keeps the shared-memory mapping alive and remembers
        /// which slot was used so the next Update() can re-claim the same slot.
        /// </summary>
        internal static void Release()
        {
            if (!AcquireMapping(out IntPtr viewPtr, out uint version))
                return;

            if (!TryGetHeader(viewPtr, out RtssHeader header))
            {
                // Reset state on failure — next Update will re-claim.
                _claimedSlot = -1;
                return;
            }

            unsafe
            {
                void* basePtr = (void*)viewPtr.ToPointer();

                // Scan all slots to find every UCU-owned entry and zero it.
                // Do NOT re-claim — let the slot go fully empty so RTSS sees
                // an unowned slot and clears any cached rendered text.
                for (int slot = 1; slot < header.OsdArrSize; slot++)
                {
                    byte* entryPtr = ((byte*)basePtr) + header.OsdArrOffset + (ulong)(slot * header.OsdEntrySize);

                    // Check if this slot is owned by us.
                    bool isOurs = true;
                    for (int i = 0; i < OwnerName.Length; i++)
                    {
                        if (entryPtr[256 + i] != OwnerName[i])
                        {
                            isOurs = false;
                            break;
                        }
                    }

                    if (!isOurs)
                        continue;

                    // Zero out the entire entry (including owner name) so RTSS
                    // sees a fully empty, unowned slot and clears the rendered OSD.
                    for (int i = 0; i < header.OsdEntrySize; i++)
                        entryPtr[i] = 0;
                }

                // Increment frame counter to signal RTSS that the slot changed.
                RtssHeader* hdr = (RtssHeader*)basePtr;
                hdr->OsdFrame++;

                if (version >= LockingVersion)
                {
                    long* dwBusy = (long*)((byte*)basePtr + 32);
                    *dwBusy = 0;
                }

                // Flush so RTSS sees the zeroed slot immediately.
                FlushViewOfFile((IntPtr)basePtr, IntPtr.Zero);
            }

            // Reset so the next Update() claims a fresh slot via Pass 2.
            _claimedSlot = -1;
            // NOTE: deliberately NOT calling ReleaseCachedMapping() — the
            // mapping stays alive so the next tick can AcquireMapping instantly.
        }

        private static bool AcquireMapping(out IntPtr viewPtr, out uint version)
        {
            version = 0;

            // Try cached handle.
            if (_cachedViewPtr != IntPtr.Zero)
            {
                try
                {
                    RtssHeader cachedHeader = Marshal.PtrToStructure<RtssHeader>(_cachedViewPtr);
                    if (cachedHeader.Signature == ValidSignature && cachedHeader.Version >= MinVersion)
                    {
                        viewPtr = _cachedViewPtr;
                        version = cachedHeader.Version;
                        return true;
                    }
                }
                catch
                {
                    // Cache corrupted.
                }

                ReleaseCachedMapping();
                _claimedSlot = -1;
            }

            IntPtr mappingHandle = OpenFileMapping(FileMapAllAccess, false, SharedMemoryName);
            if (mappingHandle == IntPtr.Zero)
            {
                viewPtr = IntPtr.Zero;
                return false;
            }

            viewPtr = MapViewOfFile(mappingHandle, FileMapAllAccess, 0, 0, 0);
            if (viewPtr == IntPtr.Zero)
            {
                CloseHandle(mappingHandle);
                viewPtr = IntPtr.Zero;
                return false;
            }

            RtssHeader header = Marshal.PtrToStructure<RtssHeader>(viewPtr);
            if (header.Signature != ValidSignature || header.Version < MinVersion)
            {
                UnmapViewOfFile(viewPtr);
                CloseHandle(mappingHandle);
                viewPtr = IntPtr.Zero;
                return false;
            }

            _cachedMappingHandle = mappingHandle;
            _cachedViewPtr = viewPtr;
            version = header.Version;
            return true;
        }

        private static bool TryGetHeader(IntPtr viewPtr, out RtssHeader header)
        {
            try
            {
                header = Marshal.PtrToStructure<RtssHeader>(viewPtr);
                return header.Signature == ValidSignature && header.Version >= MinVersion;
            }
            catch
            {
                header = default;
                return false;
            }
        }

        private static int FindOrClaimSlot(IntPtr viewPtr, ref RtssHeader header)
        {
            unsafe
            {
                byte* basePtr = (byte*)viewPtr.ToPointer();

                // Pass 1: find existing slot owned by UCU.
                for (int i = 1; i < header.OsdArrSize; i++)
                {
                    byte* entry = basePtr + header.OsdArrOffset + (ulong)(i * header.OsdEntrySize);
                    if (IsOwnedBy(entry))
                        return i;
                }

                // Pass 2: claim first empty slot.
                for (int i = 1; i < header.OsdArrSize; i++)
                {
                    byte* entry = basePtr + header.OsdArrOffset + (ulong)(i * header.OsdEntrySize);
                    if (IsEmpty(entry))
                    {
                        ClaimSlot(entry);
                        return i;
                    }
                }
            }

            return -1; // No slot available.
        }

        private static unsafe bool IsOwnedBy(byte* entry)
        {
            for (int i = 0; i < OwnerName.Length; i++)
            {
                if (entry[256 + i] != OwnerName[i])
                    return false;
            }
            return true;
        }

        private static unsafe bool IsEmpty(byte* entry)
        {
            return entry[256] == 0; // szOSDOwner[0] == 0 means unclaimed.
        }

        private static unsafe void ClaimSlot(byte* entry)
        {
            byte* owner = entry + 256; // szOSDOwner offset
            for (int i = 0; i < OwnerName.Length; i++)
                owner[i] = OwnerName[i];
        }

        private static void ReleaseCachedMapping()
        {
            if (_cachedViewPtr != IntPtr.Zero)
            {
                UnmapViewOfFile(_cachedViewPtr);
                _cachedViewPtr = IntPtr.Zero;
            }
            if (_cachedMappingHandle != IntPtr.Zero)
            {
                CloseHandle(_cachedMappingHandle);
                _cachedMappingHandle = IntPtr.Zero;
            }
        }

        private static int[] EnumerableRange(int start, int count)
        {
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = start + i;
            return result;
        }

        #region RTSS shared memory structs

        // RTSS_SHARED_MEMORY header (from RTSSharedMemory.h).
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct RtssHeader
        {
            public uint Signature;  // 'RTSS' = 0x52545353
            public uint Version;    // (major << 16) + minor, e.g. 0x0002000e = v2.14
            public uint AppEntrySize;
            public uint AppArrOffset;
            public uint AppArrSize;
            public uint OsdEntrySize;
            public uint OsdArrOffset;
            public uint OsdArrSize;
            public uint OsdFrame;
            // v2.14+: long dwBusy (offset 32)
        }

        #endregion

        #region P/Invoke

        [LibraryImport("kernel32.dll", EntryPoint = "OpenFileMappingW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr OpenFileMapping(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
            uint dwFileOffsetHigh, uint dwFileOffsetLow, ulong dwNumberOfBytesToMap);

        [LibraryImport("kernel32.dll")]
        private static partial int UnmapViewOfFile(IntPtr lpBaseAddress);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool FlushViewOfFile(IntPtr lpBaseAddress, IntPtr dwNumberOfBytesToFlush);

        [LibraryImport("kernel32.dll")]
        private static partial int CloseHandle(IntPtr hObject);

        #endregion
    }
}
