using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using System.Management;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Wrapper around ACPIDriverDll.dll (the native Uniwill OEM DLL) that provides
    /// ReadEC / WriteEC / ReadIO / WriteIO / ReadIndexIO / WriteIndexIO access to
    /// the Uniwill Embedded Controller.
    ///
    /// The OEM DLL (ACPIDriverDll.dll, an MFC/C++ native library) communicates with
    /// the UWACPIDriver.sys kernel driver which is bound to ACPI device INOU0000.
    /// All EC register addresses come from the uniwill-laptop-driver Linux kernel
    /// module source (which documents the same EC on the same hardware under Linux).
    /// </summary>
    public sealed class UniwillECService : IDisposable
    {
        #region EC Register Addresses (from uniwill-laptop-driver)

        // --- Temperature & sensors ---
        public const ushort REG_CPU_TEMP = 0x043E;
        public const ushort REG_GPU_TEMP = 0x044F;
        public const ushort REG_SSD_TEMP = 0x07D1;

        // --- Fan RPM (big-endian u16) ---
        public const ushort REG_MAIN_FAN_RPM_LO = 0x0464;
        public const ushort REG_MAIN_FAN_RPM_HI = 0x0465;
        public const ushort REG_SECOND_FAN_RPM_LO = 0x046C;
        public const ushort REG_SECOND_FAN_RPM_HI = 0x046B;

        // --- Fan control ---
        public const ushort REG_MANUAL_FAN_CTRL = 0x0751;
        public const ushort REG_FAN_SWITCH_SPEED = 0x0787;
        public const ushort REG_UNIVERSAL_FAN_CTRL = 0x07C5;
        public const ushort REG_AP_OEM_6 = 0x07C6;

        // --- Support flags ---
        public const ushort REG_SUPPORT_1 = 0x0765;
        public const ushort REG_SUPPORT_2 = 0x0766;
        public const ushort REG_SUPPORT_5 = 0x0742;
        public const ushort REG_BIOS_OEM_2 = 0x0782;

        // --- Fan curve tables (16 zones each) ---
        public const ushort REG_CPU_TEMP_END_TABLE = 0x0F00;   // 16 bytes
        public const ushort REG_CPU_TEMP_START_TABLE = 0x0F10;  // 16 bytes
        public const ushort REG_CPU_FAN_SPEED_TABLE = 0x0F20;   // 16 bytes (duty 0-200)
        public const ushort REG_GPU_TEMP_END_TABLE = 0x0F30;   // 16 bytes
        public const ushort REG_GPU_TEMP_START_TABLE = 0x0F40;  // 16 bytes
        public const ushort REG_GPU_FAN_SPEED_TABLE = 0x0F50;   // 16 bytes (duty 0-200)

        // --- Direct fan PWM (WMI-only on Linux; on Windows we use WriteEC through ACPIDriverDll) ---
        public const ushort REG_PWM_1_WRITEABLE = 0x1804;
        public const ushort REG_PWM_2_WRITEABLE = 0x1809;

        // --- Power limits ---
        public const ushort REG_PL1_SETTING = 0x0783;
        public const ushort REG_PL2_SETTING = 0x0784;
        public const ushort REG_PL4_SETTING = 0x0785;
        public const ushort REG_TCC_OFFSET = 0x0786;
        public const ushort REG_PL5_SETTING = 0x0788;  // appears to be the authoritative sustained power cap on AM5/FireRange
        public const ushort REG_MODE_INDEX = 0x07AB;

        // --- Keyboard backlight ---
        public const ushort REG_RGB_RED = 0x0769;
        public const ushort REG_RGB_GREEN = 0x076A;
        public const ushort REG_RGB_BLUE = 0x076B;
        public const ushort REG_TRIGGER = 0x0767;
        public const ushort REG_KBD_STATUS = 0x078C;

        // --- Project ID (hardware revision) ---
        public const ushort REG_PROJECT_ID = 0x0740;

        // --- AP_OEM flags ---
        public const ushort REG_AP_OEM = 0x0741;

        // --- NVIDIA GPU power control (cTGP / Dynamic Boost) ---
        public const ushort REG_CUSTOM_PROFILE = 0x0727;
        public const byte CUSTOM_PROFILE_ENABLE = 0x40; // bit 6
        public const ushort REG_CTGP_DB_ENABLE = 0x0743;       // Enable bits: GENERAL(0x01), DB(0x02), CTGP(0x04)
        public const ushort REG_CTGP_DB_CTGP_OFFSET = 0x0744;
        public const ushort REG_CTGP_DB_TPP_OFFSET = 0x0745;
        public const ushort REG_CTGP_DB_DB_OFFSET = 0x0746;

        // CTGP_DB enable bit masks (from tuxedo-drivers uniwill_interfaces.h)
        public const byte CTGP_DB_ENABLE_GENERAL = 0x01;
        public const byte CTGP_DB_ENABLE_DB = 0x02;
        public const byte CTGP_DB_ENABLE_CTGP = 0x04;

        // --- PWM readback ---
        public const ushort REG_PWM_1 = 0x075B;
        public const ushort REG_PWM_2 = 0x075C;

        // --- Trigger bits ---
        public const byte TRIGGER_RGB_APPLY_COLOR = 0x20;  // bit 5
        public const byte TRIGGER_RGB_LOGO_EFFECT = 0x40;  // bit 6
        public const byte TRIGGER_RGB_RAINBOW_EFFECT = 0x80; // bit 7

        // --- MANUAL_FAN_CTRL bits ---
        public const byte FAN_MODE_TURBO = 0x10;   // bit 4
        public const byte FAN_MODE_HIGH = 0x20;    // bit 5
        public const byte FAN_MODE_BOOST = 0x40;   // bit 6
        public const byte FAN_MODE_USER = 0x80;    // bit 7
        public const byte FAN_LEVEL_MASK = 0x07;   // bits 0-2

        // --- UNIVERSAL_FAN_CTRL bits ---
        public const byte SPLIT_TABLES = 0x80;     // bit 7

        // --- AP_OEM_6 bits ---
        public const byte ENABLE_UNIVERSAL_FAN_CTRL = 0x04; // bit 2

        // --- KBD_STATUS bits ---
        public const byte KBD_WHITE_ONLY = 0x01;   // bit 0
        public const byte KBD_POWER_OFF = 0x02;    // bit 1
        public const byte KBD_APPLY = 0x10;         // bit 4
        public const byte KBD_BRIGHTNESS_MASK = 0xE0; // bits 5-7

        // --- BIOS_OEM_2 bits ---
        public const byte FAN_V2_NEW = 0x01;       // bit 0
        public const byte FAN_V3 = 0x08;           // bit 3
        public const byte ENABLE_CHINA_MODE = 0x40; // bit 6 (needed for RGB on non-China)

        // --- SUPPORT_2 bits ---
        public const byte RGB_KEYBOARD = 0x04;     // bit 2

        // --- SUPPORT_5 bits ---
        public const byte FAN_TURBO_SUPPORTED = 0x10; // bit 4
        public const byte FAN_SUPPORT = 0x20;       // bit 5

        // --- Battery ---
        public const ushort REG_BATTERY_MODE = 0x07A6;
        public const ushort REG_BATTERY_SOC = 0x04AB;

        #endregion

        #region P/Invoke to ACPIDriverDll.dll

        // The OEM DLL exports these functions. They are cdecl with int return.
        // ReadEC(ushort address) -> byte value (as int)
        // WriteEC(ushort address, byte value) -> int status
        // ReadIO, WriteIO, ReadIndexIO, WriteIndexIO, ReadCMOS, WriteCMOS, ReadMEMB, WriteMEMB, ReadPCI, WritePCI

        private const string AcpiDriverDllName = "ACPIDriverDll.dll";

        [DllImport(AcpiDriverDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ReadEC(ushort address);

        [DllImport(AcpiDriverDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WriteEC(ushort address, byte value);

        [DllImport(AcpiDriverDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ReadIO(ushort port);

        [DllImport(AcpiDriverDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WriteIO(ushort port, byte value);

        [DllImport(AcpiDriverDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ReadIndexIO(ushort address);

        [DllImport(AcpiDriverDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WriteIndexIO(ushort address, byte value);

        #endregion

        #region Private State

        private readonly string _dllPath;
        private bool _initialized;
        private bool _disposed;
        private bool _boostActive;
        private bool _turboActive;
        private bool _ecHandshakeDone;
        private static readonly object _lock = new object();

        // Saved state for turbo toggle restore
        private byte[] _savedCpuDuty = Array.Empty<byte>();
        private byte[] _savedGpuDuty = Array.Empty<byte>();
        private byte _savedManualFanCtrl;
        private byte _savedApOem6;
        private byte _savedUfCtrl;

        // 6ms delay between EC operations (from uniwill-laptop-driver UNIWILL_EC_DELAY_US)
        private const int EcDelayMs = 6;

        #endregion

        #region Constructor & Initialization

        /// <summary>
        /// Creates a new UniwillECService that loads ACPIDriverDll.dll from the specified path.
        /// Pass null to use the DLL from the application directory or system PATH.
        /// </summary>
        public UniwillECService(string? dllPath = null)
        {
            _dllPath = dllPath ?? string.Empty;
        }

        /// <summary>
        /// Initializes the EC service by loading ACPIDriverDll.dll.
        /// Returns true if the DLL loaded successfully and the EC is accessible.
        /// </summary>
        public bool Initialize()
        {
            if (_initialized)
                return true;

            lock (_lock)
            {
                if (_initialized)
                    return true;

                try
                {
                    if (!string.IsNullOrEmpty(_dllPath))
                    {
                        // Load the DLL from a specific path
                        NativeLibrary.Load(_dllPath);
                    }

                    // Test EC access by reading the PROJECT_ID register
                    int projectId = ReadEC(REG_PROJECT_ID);
                    DebugLog($"UniwillEC: Initialized successfully. PROJECT_ID = 0x{projectId & 0xFF:X2}");
                    _initialized = true;
                    return true;
                }
                catch (DllNotFoundException)
                {
                    DebugLog($"[ERROR] UniwillEC: ACPIDriverDll.dll not found. Ensure the DLL is in the application directory or specify the path.");
                    return false;
                }
                catch (Exception ex)
                {
                    DebugLog($"[ERROR] UniwillEC: Failed to initialize EC access: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Default OEM fan curves (captured from MechControl Frida trace of GCUService startup).
        /// The EC expects these during the init handshake (step 5) before it will accept
        /// custom tables. Without writing these first, the EC may reject subsequent table writes.
        /// </summary>
        private static readonly byte[] DefaultCpuTempUp =
            { 48, 52, 56, 60, 64, 68, 72, 76, 80, 85, 255, 255, 255, 255, 255, 255 };
        private static readonly byte[] DefaultCpuTempDown =
            { 0, 46, 50, 54, 58, 62, 66, 70, 74, 78, 82, 255, 255, 255, 255, 255 };
        private static readonly byte[] DefaultCpuDuty =
            { 0, 64, 70, 80, 100, 110, 120, 140, 160, 180, 180, 180, 180, 180, 180, 180 };
        private static readonly byte[] DefaultGpuTempUp =
            { 46, 50, 54, 58, 62, 66, 70, 74, 78, 81, 255, 255, 255, 255, 255, 255 };
        private static readonly byte[] DefaultGpuTempDown =
            { 0, 44, 48, 52, 56, 60, 64, 68, 72, 76, 80, 255, 255, 255, 255, 255 };
        private static readonly byte[] DefaultGpuDuty =
            { 0, 60, 70, 80, 102, 110, 120, 140, 160, 180, 180, 180, 180, 180, 180, 180 };

        /// <summary>
        /// Performs the full EC activation handshake required before fan curve changes
        /// will be accepted by the EC. This matches the MechControl initialization sequence.
        ///
        /// The EC has a state machine that requires this handshake before it will apply
        /// custom fan curves. Without it, writes to the fan table registers are accepted
        /// but ignored by the fan control engine.
        ///
        /// This method is idempotent — it only runs once per service lifetime. Subsequent
        /// calls are no-ops to avoid confusing the EC state machine with repeated handshakes.
        /// </summary>
        public void InitializeEc()
        {
            EnsureInitialized();

            lock (_lock)
            {
                if (_ecHandshakeDone)
                {
                    DebugLog("[DIAG] UniwillEC: EC handshake already done — skipping");
                    return;
                }

                DebugLog("[DIAG] UniwillEC: Starting EC activation handshake...");

                // Step 1: Handshake — write to 0x709 then 0x708
                WriteECRaw(0x0709, 0x05);
                Thread.Sleep(50);
                WriteECRaw(0x0708, 0x01);
                Thread.Sleep(50);
                DebugLog("[DIAG] UniwillEC: Step 1 — Handshake complete");

                // Step 2: Activation — write 0x01 to 0x0741 (manual mode).
                // The Linux driver (tuxedo_io) writes 0x01 here. Previously we used 0x05
                // which may have had a side effect of enforcing the BIOS power ceiling.
                WriteECRaw(0x0741, 0x01);
                Thread.Sleep(50);
                DebugLog("[DIAG] UniwillEC: Step 2 — Manual mode enabled (0x741=0x01)");

                // Step 3: Feature enable — write 0x81 to 0x748
                WriteECRaw(0x0748, 0x81);
                Thread.Sleep(50);
                DebugLog("[DIAG] UniwillEC: Step 3 — Feature enable complete");

                // Step 3b: Power management activation — set bit 7 of 0x726 and bit 6 of 0x727.
                // MechControl performs this step between feature enable and fan curve enable.
                // Without it, the EC may accept power limit writes but not apply them to hardware.
                byte reg726 = (byte)(ReadECRaw(0x0726) | 0x80);
                WriteECRaw(0x0726, reg726);
                Thread.Sleep(50);
                byte reg727 = (byte)(ReadECRaw(0x0727) | 0x40);
                WriteECRaw(0x0727, reg727);
                Thread.Sleep(50);
                DebugLog($"[DIAG] UniwillEC: Step 3b — Power management activation (0x726=0x{reg726:X2}, 0x727=0x{reg727:X2})");

                // Step 4: Fan curve enable bits — set bits 7,6,5 of 0x7C5 (OR with 0xE0)
                byte ufCtrl = (byte)(ReadECRaw(0x07C5) | 0xE0);
                WriteECRaw(0x07C5, ufCtrl);
                Thread.Sleep(50);
                DebugLog($"[DIAG] UniwillEC: Step 4 — Fan curve enable bits set (0x{ufCtrl:X2})");

                // Step 5: Write default OEM fan tables.
                // The EC expects this as part of the init sequence. Skipping it causes
                // the EC to reject subsequent custom table writes.
                DebugLog("[DIAG] UniwillEC: Step 5 — Writing default OEM fan tables...");
                WriteECRegistersRaw(REG_CPU_TEMP_END_TABLE, DefaultCpuTempUp);
                WriteECRegistersRaw(REG_CPU_TEMP_START_TABLE, DefaultCpuTempDown);
                WriteECRegistersRaw(REG_CPU_FAN_SPEED_TABLE, DefaultCpuDuty);
                Thread.Sleep(150); // EC needs time to process CPU tables before GPU
                WriteECRegistersRaw(REG_GPU_TEMP_END_TABLE, DefaultGpuTempUp);
                WriteECRegistersRaw(REG_GPU_TEMP_START_TABLE, DefaultGpuTempDown);
                WriteECRegistersRaw(REG_GPU_FAN_SPEED_TABLE, DefaultGpuDuty);
                DebugLog("[DIAG] UniwillEC: Step 5 — Default fan tables written");

                // Step 6: Fan curve parameter — write 0x0C to 0x7C7
                WriteECRaw(0x07C7, 0x0C);
                Thread.Sleep(50);
                DebugLog("[DIAG] UniwillEC: Step 6 — Fan curve parameter set");

                // Step 7: Fan curve mode — write 0x04 to 0x7C6
                WriteECRaw(0x07C6, 0x04);
                Thread.Sleep(50);
                DebugLog("[DIAG] UniwillEC: Step 7 — Fan curve mode enabled");

                // Step 8: BIOS_CTRL — write 0x41 to 0x706
                WriteECRaw(0x0706, 0x41);
                Thread.Sleep(50);
                DebugLog("[DIAG] UniwillEC: Step 8 — BIOS_CTRL set");

                _ecHandshakeDone = true;
                DebugLog("[DIAG] UniwillEC: EC activation handshake complete — fan curves should now apply");
            }
        }

        /// <summary>
        /// Writes a byte directly to EC without the outer lock (used internally by InitializeEc).
        /// </summary>
        private void WriteECRaw(ushort address, byte value)
        {
            int result = WriteEC(address, value);
            Thread.Sleep(EcDelayMs);
            if (result != 0)
            {
                DebugLog($"[WARN] UniwillEC: WriteECRaw(0x{address:X2}, 0x{value:X2}) returned {result}");
            }
        }

        private byte ReadECRaw(ushort address)
        {
            int result = ReadEC(address);
            Thread.Sleep(EcDelayMs);
            return (byte)(result & 0xFF);
        }

        /// <summary>
        /// Writes multiple bytes directly to EC without the outer lock (used by InitializeEc step 5).
        /// </summary>
        private void WriteECRegistersRaw(ushort startAddress, byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                WriteECRaw((ushort)(startAddress + i), data[i]);
            }
        }

        #endregion

        #region EC Read/Write

        /// <summary>
        /// Reads a single byte from the EC at the specified address.
        /// </summary>
        public byte ReadECRegister(ushort address)
        {
            EnsureInitialized();
            lock (_lock)
            {
                int result = ReadEC(address);
                Thread.Sleep(EcDelayMs);
                return (byte)(result & 0xFF);
            }
        }

        /// <summary>
        /// Writes a single byte to the EC at the specified address.
        /// </summary>
        public void WriteECRegister(ushort address, byte value)
        {
            EnsureInitialized();
            lock (_lock)
            {
                int result = WriteEC(address, value);
                Thread.Sleep(EcDelayMs);
                if (result != 0)
                {
                    DebugLog($"[WARN] UniwillEC: WriteEC(0x{address:X2}, 0x{value:X2}) returned {result}");
                }
            }
        }

        /// <summary>
        /// Reads multiple bytes starting at the given address.
        /// </summary>
        public byte[] ReadECRegisters(ushort startAddress, int count)
        {
            EnsureInitialized();
            byte[] data = new byte[count];
            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    data[i] = (byte)(ReadEC((ushort)(startAddress + i)) & 0xFF);
                    Thread.Sleep(EcDelayMs);
                }
            }
            return data;
        }

        /// <summary>
        /// Writes multiple bytes starting at the given address.
        /// </summary>
        public void WriteECRegisters(ushort startAddress, byte[] data)
        {
            EnsureInitialized();
            lock (_lock)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    WriteEC((ushort)(startAddress + i), data[i]);
                    Thread.Sleep(EcDelayMs);
                }
            }
        }

        #endregion

        #region NVIDIA GPU Power Control (cTGP / Dynamic Boost)

        /// <summary>
        /// Activates the EC GPU power control feature set (cTGP + Dynamic Boost).
        /// Must be called BEFORE setting GPU power limits via NVML to raise the
        /// ceiling above NVIDIA's default 150W laptop GPU limit.
        ///
        /// From tuxedo-drivers: tuxedo_nb02_nvidia_power_ctrl.c :: init_db_and_ctgp()
        ///
        /// WARNING: Registers 0x0743-0x0746 overlap with "manual-mode fan-curve" block.
        /// Call this AFTER fan initialization to avoid clobbering fan curve data.
        /// </summary>
        /// <param name="dbOffset">Dynamic Boost range in watts (0-25). Default 25 gives +25W GPU borrow from CPU.</param>
        public void ActivateGpuPowerControl(int dbOffset = 25)
            => ActivateGpuPowerControl(55, dbOffset); // Legacy: hardcoded cTGP=55 for backward compat.

        /// <summary>
        /// Activates GPU power control on the EC with explicit cTGP and Dynamic Boost offsets.
        /// </summary>
        /// <param name="ctgpOffset">cTGP offset written to EC register 0x0744. Computed as (targetWatts - defaultWatts) per UCC/tuxedo-drivers.</param>
        /// <param name="dbOffset">Dynamic Boost range in watts (0-25). Default 25 gives +25W GPU borrow from CPU.</param>
        public void ActivateGpuPowerControl(int ctgpOffset, int dbOffset)
        {
            EnsureInitialized();
            lock (_lock)
            {
                ctgpOffset = Math.Clamp(ctgpOffset, 0, 255);
                DebugLog($"ActivateGpuPowerControl(ctgpOffset={ctgpOffset}, dbOffset={dbOffset})");

                // Prerequisite: enable CUSTOM_PROFILE mode so the EC accepts GPU power writes.
                byte beforeCp = (byte)ReadEC(REG_CUSTOM_PROFILE);
                byte customProfile = (byte)(beforeCp | CUSTOM_PROFILE_ENABLE);
                WriteEC(REG_CUSTOM_PROFILE, customProfile);
                Thread.Sleep(EcDelayMs);

                // Step 0: Set GPU power management enable flag (0x0741).
                WriteEC(0x0741, 0x01);
                Thread.Sleep(EcDelayMs);

                // Step 0.5: Enable CTGP/DB feature in EC (0x0743).
                // tuxedo-drivers writes this register to enable GPU power control.
                // Bits: GENERAL(0x01), DB(0x02), CTGP(0x04)
                // All three must be set for the NVIDIA driver to recognize EC power limits.
                byte ctgpDbEnable = CTGP_DB_ENABLE_GENERAL | CTGP_DB_ENABLE_DB | CTGP_DB_ENABLE_CTGP; // 0x07
                WriteEC(REG_CTGP_DB_ENABLE, ctgpDbEnable);
                Thread.Sleep(EcDelayMs);

                // Step 1: cTGP offset (targetWatts - defaultWatts, per UCC/tuxedo-drivers).
                WriteEC(REG_CTGP_DB_CTGP_OFFSET, (byte)ctgpOffset);
                Thread.Sleep(EcDelayMs);

                // Step 2: TPP offset = 255 (max total power pool)
                WriteEC(REG_CTGP_DB_TPP_OFFSET, 255);
                Thread.Sleep(EcDelayMs);

                // Step 3: Dynamic Boost offset (watts the GPU can borrow from CPU)
                byte dbClamped = (byte)Math.Clamp(dbOffset, 0, 25);
                WriteEC(REG_CTGP_DB_DB_OFFSET, dbClamped);
                Thread.Sleep(EcDelayMs);

                // Verify writes by reading back all registers
                byte r0 = (byte)ReadEC(REG_CUSTOM_PROFILE);
                byte r1 = (byte)ReadEC(REG_CTGP_DB_ENABLE);
                byte r2 = (byte)ReadEC(REG_CTGP_DB_CTGP_OFFSET);
                byte r3 = (byte)ReadEC(REG_CTGP_DB_TPP_OFFSET);
                byte r4 = (byte)ReadEC(REG_CTGP_DB_DB_OFFSET);
                DebugLog($"ActivateGpuPowerControl readback: 0x0727=0x{r0:X2} 0x0743=0x{r1:X2} 0x0744=0x{r2:X2} (wanted 0x{ctgpOffset:X2}) 0x0745=0x{r3:X2} 0x0746=0x{r4:X2} (wanted db=0x{dbClamped:X2})");
            }
        }

        /// <summary>
        /// Disables the EC GPU power control feature set. Restores NVIDIA default 150W ceiling.
        /// </summary>
        public void DeactivateGpuPowerControl()
        {
            EnsureInitialized();
            lock (_lock)
            {
                // Disable CTGP/DB feature in EC (0x0743 = 0x00)
                WriteEC(REG_CTGP_DB_ENABLE, 0x00);
                Thread.Sleep(EcDelayMs);

                // Reset offsets to defaults
                WriteEC(REG_CTGP_DB_CTGP_OFFSET, 55);
                Thread.Sleep(EcDelayMs);
                WriteEC(REG_CTGP_DB_TPP_OFFSET, 0);
                Thread.Sleep(EcDelayMs);
                WriteEC(REG_CTGP_DB_DB_OFFSET, 0);
            }
        }

        /// <summary>
        /// Gets the current TCC (Thermal Comfort Control) offset from the EC.
        /// Returns the value of register 0x0786 (CPU TCC offset).
        /// </summary>
        public int GetTccOffset()
        {
            return ReadECRegister(REG_TCC_OFFSET);
        }

        /// <summary>
        /// Sets the TCC offset on the EC (register 0x0786).
        /// Value is offset from 95°C default. 0 = no offset (95°C), 10 = 85°C throttle.
        /// </summary>
        public void SetTccOffset(int value)
        {
            WriteECRegister(REG_TCC_OFFSET, (byte)Math.Clamp(value, 0, 255));
        }

        #endregion

        #region Fan Control

        /// <summary>
        /// Reads the CPU temperature directly from the EC (degrees Celsius).
        /// </summary>
        public int GetCpuTemperature()
        {
            return ReadECRegister(REG_CPU_TEMP);
        }

        /// <summary>
        /// Reads the GPU temperature directly from the EC (degrees Celsius).
        /// </summary>
        public int GetGpuTemperature()
        {
            return ReadECRegister(REG_GPU_TEMP);
        }

        /// <summary>
        /// Reads the main fan RPM (big-endian u16: 0x0464 is high byte, 0x0465 is low byte).
        /// </summary>
        public int GetMainFanRpm()
        {
            byte hi = ReadECRegister(REG_MAIN_FAN_RPM_LO);
            byte lo = ReadECRegister(REG_MAIN_FAN_RPM_HI);
            return (hi << 8) | lo;
        }

        /// <summary>
        /// Reads the secondary fan RPM (registers 0x046C/0x046B).
        /// </summary>
        public int GetSecondFanRpm()
        {
            byte lo = ReadECRegister(REG_SECOND_FAN_RPM_LO);
            byte hi = ReadECRegister(REG_SECOND_FAN_RPM_HI);
            return (hi << 8) | lo;
        }

        /// <summary>
        /// The OEM default duty values (10 active zones) that the EC expects as a valid
        /// gradient. We scale these proportionally when the user requests a different speed.
        /// </summary>
        private static readonly byte[] OemDefaultCpuDuty =
            { 0, 64, 70, 80, 100, 110, 120, 140, 160, 180 };
        private static readonly byte[] OemDefaultGpuDuty =
            { 0, 60, 70, 80, 102, 110, 120, 140, 160, 180 };

        /// <summary>
        /// Sets both fans to a specific speed percentage (0-100) by uploading a near-flat
        /// fan curve table to the EC and enabling custom auto mode.
        ///
        /// The EC rejects perfectly flat tables (all zones = same duty). Instead we write
        /// a near-flat curve: zone 0 = 0 (fan off at lowest temp), zones 1-15 = target duty.
        /// This effectively gives a fixed speed for any real-world temperature (&gt;0°C).
        ///
        /// IMPORTANT: The EC fan curve state machine can lock up if SPLIT_TABLES is
        /// cleared while custom mode is already active. This method avoids toggling
        /// SPLIT_TABLES if it's already set — it only does a False→True transition
        /// when the bit is actually cleared.
        ///
        /// CRITICAL: The EC requires an 8-step activation handshake (from MechControl)
        /// before it will apply custom fan curves. This is called at the start of this
        /// method to ensure the EC is in a state that accepts table writes.
        /// </summary>
        public void SetFanSpeedPercent(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            byte targetDuty = (byte)(percent * 2); // EC uses 0-200 scale

            // Run the EC activation handshake (idempotent — only runs once).
            InitializeEc();

            // Build near-flat duty tables: zone 0 = 0, zones 1-15 = target duty.
            // The EC rejects perfectly flat tables, so zone 0 stays at 0.
            // All real-world temps (&gt;0°C) will hit zone 1+ and get the target duty.
            byte[] cpuDuty = new byte[16];
            byte[] gpuDuty = new byte[16];
            for (int i = 1; i < 16; i++)
            {
                cpuDuty[i] = targetDuty;
                gpuDuty[i] = targetDuty;
            }

            // Temperature threshold tables (same as OEM defaults).
            byte[] cpuTempUp   = { 48, 52, 56, 60, 64, 68, 72, 76, 80, 85, 255, 255, 255, 255, 255, 255 };
            byte[] cpuTempDown = {  0, 46, 50, 54, 58, 62, 66, 70, 74, 78,  82, 255, 255, 255, 255, 255 };
            byte[] gpuTempUp   = { 46, 50, 54, 58, 62, 66, 70, 74, 78, 81, 255, 255, 255, 255, 255, 255 };
            byte[] gpuTempDown = {  0, 44, 48, 52, 56, 60, 64, 68, 72, 76,  80, 255, 255, 255, 255, 255 };

            // Write CPU fan curve table
            WriteECRegisters(REG_CPU_TEMP_END_TABLE, cpuTempUp);
            WriteECRegisters(REG_CPU_TEMP_START_TABLE, cpuTempDown);
            WriteECRegisters(REG_CPU_FAN_SPEED_TABLE, cpuDuty);

            // Give the EC time to process CPU tables before GPU writes.
            Thread.Sleep(150);

            // Write GPU fan curve table
            WriteECRegisters(REG_GPU_TEMP_END_TABLE, gpuTempUp);
            WriteECRegisters(REG_GPU_TEMP_START_TABLE, gpuTempDown);
            WriteECRegisters(REG_GPU_FAN_SPEED_TABLE, gpuDuty);

            DebugLog($"[DIAG] UniwillEC: Wrote near-flat duty tables (targetDuty={targetDuty}) for {percent}% speed");

            // Clear boost mode — custom tables don't need it
            byte fanCtrl = ReadECRegister(REG_MANUAL_FAN_CTRL);
            fanCtrl = (byte)(fanCtrl & ~FAN_MODE_BOOST);
            WriteECRegister(REG_MANUAL_FAN_CTRL, fanCtrl);
            _boostActive = false;

            // Only toggle SPLIT_TABLES if it's currently False.
            // Clearing SPLIT_TABLES while the EC is in custom fan mode can cause
            // the EC fan state machine to lock up, requiring a reboot to recover.
            byte ufCtrl = ReadECRegister(REG_UNIVERSAL_FAN_CTRL);
            bool splitWasSet = (ufCtrl & SPLIT_TABLES) != 0;

            if (splitWasSet)
            {
                // SPLIT is already True — just ensure ENABLE_CUSTOM is set.
                // Toggle ENABLE_CUSTOM off then on to force the EC to re-read the new tables.
                byte apOem6 = ReadECRegister(REG_AP_OEM_6);
                apOem6 = (byte)(apOem6 & ~ENABLE_UNIVERSAL_FAN_CTRL);
                WriteECRegister(REG_AP_OEM_6, apOem6);
                Thread.Sleep(50);
                apOem6 = ReadECRegister(REG_AP_OEM_6);
                apOem6 |= ENABLE_UNIVERSAL_FAN_CTRL;
                WriteECRegister(REG_AP_OEM_6, apOem6);
                DebugLog($"[DIAG] UniwillEC: Set fan speed to {percent}% (targetDuty {targetDuty}), SPLIT already True — toggled custom to force re-read");
            }
            else
            {
                // SPLIT is False — do a proper False→True transition.
                // Add a delay after writing tables to let the EC settle.
                Thread.Sleep(50);

                ufCtrl = (byte)(ufCtrl | SPLIT_TABLES);
                WriteECRegister(REG_UNIVERSAL_FAN_CTRL, ufCtrl);

                // Also set ENABLE_CUSTOM
                byte apOem6 = ReadECRegister(REG_AP_OEM_6);
                apOem6 |= ENABLE_UNIVERSAL_FAN_CTRL;
                WriteECRegister(REG_AP_OEM_6, apOem6);

                DebugLog($"[DIAG] UniwillEC: Set fan speed to {percent}% (targetDuty {targetDuty}) via SPLIT False→True transition");
            }
        }

        /// <summary>
        /// Scales an OEM default duty gradient to a target max duty value.
        /// The EC expects monotonically non-decreasing duty values across zones,
        /// with padding zones set to 180 (not the raw target, to match OEM format).
        /// Returns a full 16-byte array (10 active zones + 6 padding).
        /// </summary>
        private static byte[] ScaleDutyGradient(byte[] defaultDuty, byte targetMax)
        {
            byte[] result = new byte[16];
            int activeCount = defaultDuty.Length; // 10

            for (int i = 0; i < activeCount; i++)
            {
                // Scale each zone proportionally to the target max.
                // The OEM max is 180, so we scale targetMax/180 * defaultValue.
                // Zone 0 is always 0 (fan off at lowest temp).
                if (i == 0)
                {
                    result[i] = 0;
                }
                else
                {
                    int scaled = (int)Math.Round((double)defaultDuty[i] * targetMax / 180.0);
                    // Ensure monotonically non-decreasing
                    scaled = Math.Max(scaled, result[i - 1]);
                    result[i] = (byte)Math.Clamp(scaled, 0, 200);
                }
            }

            // Padding zones: use 180 as OEM does (not the raw target value).
            // When targetMax < 180, use targetMax for padding to maintain the flat top.
            byte padValue = (byte)Math.Min((int)targetMax, 180);
            for (int i = activeCount; i < 16; i++)
            {
                result[i] = padValue;
            }

            return result;
        }

        /// <summary>
        /// Disables custom fan tables and restores EC automatic profile mode.
        /// This reverses what SetFanSpeedPercent does.
        /// </summary>
        public void RestoreAutoFanControl()
        {
            // Disable custom fan tables (matching uniwill_fan_disable_custom_tables)
            byte apOem6 = ReadECRegister(REG_AP_OEM_6);
            apOem6 = (byte)(apOem6 & ~ENABLE_UNIVERSAL_FAN_CTRL);
            WriteECRegister(REG_AP_OEM_6, apOem6);

            byte ufCtrl = ReadECRegister(REG_UNIVERSAL_FAN_CTRL);
            ufCtrl = (byte)(ufCtrl & ~SPLIT_TABLES);
            WriteECRegister(REG_UNIVERSAL_FAN_CTRL, ufCtrl);

            // Clear ALL manual fan control modes (USER, BOOST, TURBO, HIGH)
            byte fanCtrl = ReadECRegister(REG_MANUAL_FAN_CTRL);
            fanCtrl = (byte)(fanCtrl & ~(FAN_MODE_USER | FAN_MODE_BOOST | FAN_MODE_TURBO | FAN_MODE_HIGH));
            WriteECRegister(REG_MANUAL_FAN_CTRL, fanCtrl);
            _boostActive = false;

            // Also clear turbo state flag so button UI resets
            _turboActive = false;

            DebugLog("UniwillEC: Restored automatic fan control");
        }

        /// <summary>
        /// Toggles turbo mode. When enabled: sets FAN_MODE_BOOST on MANUAL_FAN_CTRL to force
        /// fans to maximum speed. When disabled: restores saved EC state.
        /// Based on uniwill-laptop-driver fan mode 0 (full speed).
        /// </summary>
        public void ToggleTurbo()
        {
            InitializeEc();

            if (_turboActive)
            {
                // --- DISABLE TURBO: restore saved state ---
                if (_savedCpuDuty.Length == 16)
                    WriteECRegisters(REG_CPU_FAN_SPEED_TABLE, _savedCpuDuty);

                // Give the EC time to process CPU table before GPU table.
                Thread.Sleep(150);

                if (_savedGpuDuty.Length == 16)
                    WriteECRegisters(REG_GPU_FAN_SPEED_TABLE, _savedGpuDuty);

                WriteECRegister(REG_MANUAL_FAN_CTRL, _savedManualFanCtrl);
                WriteECRegister(REG_AP_OEM_6, _savedApOem6);
                WriteECRegister(REG_UNIVERSAL_FAN_CTRL, _savedUfCtrl);

                _turboActive = false;
                DebugLog("[DIAG] UniwillEC: Turbo mode DISABLED — state restored");
                return;
            }

            // --- ENABLE TURBO: save current state, then force max ---
            _savedCpuDuty = ReadECRegisters(REG_CPU_FAN_SPEED_TABLE, 16);
            _savedGpuDuty = ReadECRegisters(REG_GPU_FAN_SPEED_TABLE, 16);
            _savedManualFanCtrl = ReadECRegister(REG_MANUAL_FAN_CTRL);
            _savedApOem6 = ReadECRegister(REG_AP_OEM_6);
            _savedUfCtrl = ReadECRegister(REG_UNIVERSAL_FAN_CTRL);

            // Minimal enable: set BOOST flag and ensure custom tables are active.
            // This mirrors the disable path which only clears BOOST on 0x751.
            byte fanCtrl = ReadECRegister(REG_MANUAL_FAN_CTRL);
            fanCtrl = (byte)(fanCtrl | FAN_MODE_BOOST);
            WriteECRegister(REG_MANUAL_FAN_CTRL, fanCtrl);

            WriteECRegister(REG_AP_OEM_6, ENABLE_UNIVERSAL_FAN_CTRL);
            WriteECRegister(REG_UNIVERSAL_FAN_CTRL, SPLIT_TABLES);

            _turboActive = true;
            DebugLog("[DIAG] UniwillEC: Turbo mode ENABLED — fans at max duty");
        }

        /// <summary>
        /// Async version of ToggleTurbo that runs EC I/O on a background thread
        /// so the UI thread is never blocked during the PWM repeat loop.
        /// </summary>
        public async Task ToggleTurboAsync()
        {
            if (_turboActive)
            {
                // Disable path is fast — no need for background thread
                ToggleTurbo();
                return;
            }

            // Run the enable sequence on a background thread.
            await Task.Run(() =>
            {
                InitializeEc();

                // Save current state
                _savedCpuDuty = ReadECRegisters(REG_CPU_FAN_SPEED_TABLE, 16);
                _savedGpuDuty = ReadECRegisters(REG_GPU_FAN_SPEED_TABLE, 16);
                _savedManualFanCtrl = ReadECRegister(REG_MANUAL_FAN_CTRL);
                _savedApOem6 = ReadECRegister(REG_AP_OEM_6);
                _savedUfCtrl = ReadECRegister(REG_UNIVERSAL_FAN_CTRL);

                // Minimal enable: set BOOST flag and ensure custom tables are active
                byte fanCtrl = ReadECRegister(REG_MANUAL_FAN_CTRL);
                fanCtrl = (byte)(fanCtrl | FAN_MODE_BOOST);
                WriteECRegister(REG_MANUAL_FAN_CTRL, fanCtrl);

                WriteECRegister(REG_AP_OEM_6, ENABLE_UNIVERSAL_FAN_CTRL);
                WriteECRegister(REG_UNIVERSAL_FAN_CTRL, SPLIT_TABLES);

                _turboActive = true;
                DebugLog("[DIAG] UniwillEC: Turbo mode ENABLED — fans at max duty (async)");
            });
        }

        /// <summary>
        /// Returns true if turbo mode is currently active.
        /// </summary>
        public bool IsTurboActive() => _turboActive;

        /// <summary>
        /// Attempts to recover from an EC fan state machine lockup by performing
        /// a full reset sequence: clear all control bits, write safe default tables,
        /// then re-enable with a clean False→True SPLIT transition.
        ///
        /// Use this when fans are stuck and neither UCU nor XMG Control Center can
        /// change the PWM. This avoids requiring a full reboot.
        /// </summary>
        public void ResetFanState()
        {
            DebugLog("[DIAG] UniwillEC: Attempting fan state reset...");

            // Step 1: Clear ALL control bits
            byte apOem6 = ReadECRegister(REG_AP_OEM_6);
            apOem6 = (byte)(apOem6 & ~ENABLE_UNIVERSAL_FAN_CTRL);
            WriteECRegister(REG_AP_OEM_6, apOem6);

            byte fanCtrl = ReadECRegister(REG_MANUAL_FAN_CTRL);
            fanCtrl = (byte)(fanCtrl & ~(FAN_MODE_BOOST | FAN_MODE_USER | FAN_MODE_TURBO | FAN_MODE_HIGH));
            WriteECRegister(REG_MANUAL_FAN_CTRL, fanCtrl);

            byte ufCtrl = ReadECRegister(REG_UNIVERSAL_FAN_CTRL);
            ufCtrl = (byte)(ufCtrl & ~SPLIT_TABLES);
            WriteECRegister(REG_UNIVERSAL_FAN_CTRL, ufCtrl);

            // Step 2: Wait for the EC to process the clear
            Thread.Sleep(200);

            // Step 3: Write safe default tables (gradient to 100% duty to prevent thermal issues)
            byte[] safeCpuDuty = ScaleDutyGradient(OemDefaultCpuDuty, 200);
            byte[] safeGpuDuty = ScaleDutyGradient(OemDefaultGpuDuty, 200);

            WriteECRegisters(REG_CPU_FAN_SPEED_TABLE, safeCpuDuty);
            Thread.Sleep(150); // EC needs time to process CPU table before GPU
            WriteECRegisters(REG_GPU_FAN_SPEED_TABLE, safeGpuDuty);

            // Step 4: Wait for tables to settle
            Thread.Sleep(100);

            // Step 5: Re-enable with a clean transition
            ufCtrl = ReadECRegister(REG_UNIVERSAL_FAN_CTRL);
            ufCtrl |= SPLIT_TABLES;
            WriteECRegister(REG_UNIVERSAL_FAN_CTRL, ufCtrl);

            apOem6 = ReadECRegister(REG_AP_OEM_6);
            apOem6 |= ENABLE_UNIVERSAL_FAN_CTRL;
            WriteECRegister(REG_AP_OEM_6, apOem6);

            DebugLog("[DIAG] UniwillEC: Fan state reset complete — fans at 100%");
        }

        /// <summary>
        /// Uploads a fan curve table to the EC.
        /// Each zone has: temp_up (°C), temp_down (°C, hysteresis), duty (0-200 scale).
        /// The table has 16 zones (indices 0-15).
        /// </summary>
        public void UploadFanCurve(FanCurveSource source, int[] tempUp, int[] tempDown, int[] duty)
        {
            if (tempUp.Length != 16 || tempDown.Length != 16 || duty.Length != 16)
                throw new ArgumentException("Fan curve tables must have 16 entries.");

            ushort baseAddr = source switch
            {
                FanCurveSource.Cpu => REG_CPU_TEMP_END_TABLE,
                FanCurveSource.Gpu => REG_GPU_TEMP_END_TABLE,
                _ => throw new ArgumentOutOfRangeException(nameof(source))
            };

            // Clamp values
            for (int i = 0; i < 16; i++)
            {
                tempUp[i] = Math.Clamp(tempUp[i], 0, 255);
                tempDown[i] = Math.Clamp(tempDown[i], 0, 255);
                duty[i] = Math.Clamp(duty[i], 0, 200);
            }

            // Write temp_up table
            byte[] tempUpBytes = Array.ConvertAll(tempUp, b => (byte)b);
            WriteECRegisters(baseAddr, tempUpBytes);

            // Write temp_down table
            byte[] tempDownBytes = Array.ConvertAll(tempDown, b => (byte)b);
            WriteECRegisters((ushort)(baseAddr + 0x10), tempDownBytes);

            // Write duty table
            byte[] dutyBytes = Array.ConvertAll(duty, b => (byte)b);
            WriteECRegisters((ushort)(baseAddr + 0x20), dutyBytes);

            // Enable universal fan control
            byte ufCtrl = ReadECRegister(REG_UNIVERSAL_FAN_CTRL);
            ufCtrl |= SPLIT_TABLES;
            WriteECRegister(REG_UNIVERSAL_FAN_CTRL, ufCtrl);

            byte apOem6 = ReadECRegister(REG_AP_OEM_6);
            apOem6 |= ENABLE_UNIVERSAL_FAN_CTRL;
            WriteECRegister(REG_AP_OEM_6, apOem6);

            DebugLog($"UniwillEC: Uploaded {source} fan curve (16 zones)");
        }

        /// <summary>
        /// Applies separate CPU and GPU fan curves to the EC.
        /// Runs the EC activation handshake, writes the curves, and forces the EC
        /// to re-read the tables by toggling ENABLE_CUSTOM.
        /// </summary>
        public void ApplyFanCurve(
            Uniwill_Control_Utility.Models.EcFanCurve cpuCurve,
            Uniwill_Control_Utility.Models.EcFanCurve gpuCurve)
        {
            ApplyFanCurve(cpuCurve, gpuCurve, null, null);
        }

        /// <summary>
        /// Applies CPU and GPU fan curves with optional custom temperature steps.
        /// The temp_up and temp_down (hysteresis) arrays are derived from the user's
        /// temperature steps: temp_up = step + 1, temp_down = step - 1.
        /// Zone 0 (OFF) is handled specially: temp_up = 0, temp_down = step - 2.
        /// </summary>
        public void ApplyFanCurve(
            Uniwill_Control_Utility.Models.EcFanCurve cpuCurve,
            Uniwill_Control_Utility.Models.EcFanCurve gpuCurve,
            int[]? cpuTemps = null, int[]? gpuTemps = null)
        {
            InitializeEc();

            byte[] cpuDuty = cpuCurve.ToEcDutyTable();
            byte[] gpuDuty = gpuCurve.ToEcDutyTable();

            // Default temperature steps from XMG Control Center (9-point scale).
            int[] defaultTemps = { 0, 55, 60, 65, 70, 75, 80, 85, 90, 95, 97 };

            byte[] finalCpuTempUp = ToEcTempUp(cpuTemps ?? defaultTemps);
            byte[] finalCpuTempDown = ToEcTempDown(cpuTemps ?? defaultTemps);
            byte[] finalGpuTempUp = ToEcTempUp(gpuTemps ?? defaultTemps);
            byte[] finalGpuTempDown = ToEcTempDown(gpuTemps ?? defaultTemps);

            WriteECRegisters(REG_CPU_TEMP_END_TABLE, finalCpuTempUp);
            WriteECRegisters(REG_CPU_TEMP_START_TABLE, finalCpuTempDown);
            WriteECRegisters(REG_CPU_FAN_SPEED_TABLE, cpuDuty);

            // Give the EC time to process the CPU table writes before sending GPU
            // data. The EC appears to use a shared internal buffer for both fan
            // table regions, and rapid back-to-back writes can cause the GPU
            // writes to be dropped or partially applied.
            Thread.Sleep(150);

            WriteECRegisters(REG_GPU_TEMP_END_TABLE, finalGpuTempUp);
            WriteECRegisters(REG_GPU_TEMP_START_TABLE, finalGpuTempDown);
            WriteECRegisters(REG_GPU_FAN_SPEED_TABLE, gpuDuty);

            // Wait for GPU tables to settle before touching control registers.
            Thread.Sleep(150);

            // Clear boost mode
            byte fanCtrl = ReadECRegister(REG_MANUAL_FAN_CTRL);
            fanCtrl = (byte)(fanCtrl & ~FAN_MODE_BOOST);
            WriteECRegister(REG_MANUAL_FAN_CTRL, fanCtrl);

            // Ensure SPLIT is set
            byte ufCtrl = ReadECRegister(REG_UNIVERSAL_FAN_CTRL);
            if ((ufCtrl & SPLIT_TABLES) == 0)
            {
                ufCtrl = (byte)(ufCtrl | SPLIT_TABLES);
                WriteECRegister(REG_UNIVERSAL_FAN_CTRL, ufCtrl);
                Thread.Sleep(50);
            }

            // Toggle ENABLE_CUSTOM to force EC to re-read tables.
            // The EC needs time to process the table writes before re-enabling.
            byte apOem6 = ReadECRegister(REG_AP_OEM_6);
            apOem6 = (byte)(apOem6 & ~ENABLE_UNIVERSAL_FAN_CTRL);
            WriteECRegister(REG_AP_OEM_6, apOem6);
            Thread.Sleep(150);
            apOem6 = ReadECRegister(REG_AP_OEM_6);
            apOem6 |= ENABLE_UNIVERSAL_FAN_CTRL;
            WriteECRegister(REG_AP_OEM_6, apOem6);
            Thread.Sleep(150);

            DebugLog($"[DIAG] UniwillEC: Applied fan curves - CPU '{cpuCurve.Name}': [{string.Join(", ", cpuDuty)}] | GPU '{gpuCurve.Name}': [{string.Join(", ", gpuDuty)}]");
        }

        /// <summary>
        /// Reads the current fan curve table from the EC.
        /// </summary>
        public (int[] tempUp, int[] tempDown, int[] duty) ReadFanCurve(FanCurveSource source)
        {
            ushort baseAddr = source switch
            {
                FanCurveSource.Cpu => REG_CPU_TEMP_END_TABLE,
                FanCurveSource.Gpu => REG_GPU_TEMP_END_TABLE,
                _ => throw new ArgumentOutOfRangeException(nameof(source))
            };

            int[] tempUp = Array.ConvertAll(ReadECRegisters(baseAddr, 16), b => (int)b);
            int[] tempDown = Array.ConvertAll(ReadECRegisters((ushort)(baseAddr + 0x10), 16), b => (int)b);
            int[] duty = Array.ConvertAll(ReadECRegisters((ushort)(baseAddr + 0x20), 16), b => (int)b);

            return (tempUp, tempDown, duty);
        }

        /// <summary>
        /// Reads the current PWM duty value for a fan (0-200 scale).
        /// </summary>
        public int GetFanPwm(int fanIndex)
        {
            ushort reg = fanIndex == 0 ? REG_PWM_1 : REG_PWM_2;
            return ReadECRegister(reg);
        }

        /// <summary>
        /// Attempts to set fan PWM directly by writing to the duty registers (0x75B/0x75C).
        /// This bypasses the fan curve table mechanism entirely.
        /// Returns true if the write succeeded (return code 0).
        /// </summary>
        public bool SetFanPwmDirect(int fanIndex, int duty)
        {
            duty = Math.Clamp(duty, 0, 200);
            ushort reg = fanIndex == 0 ? REG_PWM_1 : REG_PWM_2;

            EnsureInitialized();
            lock (_lock)
            {
                int result = WriteEC(reg, (byte)duty);
                Thread.Sleep(EcDelayMs);
                DebugLog($"[DIAG] UniwillEC: Direct PWM write to 0x{reg:X2} = {duty}, result={result}");
                return result == 0;
            }
        }

        /// <summary>
        /// Sets both fans to a specific duty (0-200) by writing directly to the PWM registers.
        /// </summary>
        public void SetFanPwmBothDirect(int duty)
        {
            duty = Math.Clamp(duty, 0, 200);

            // Set USER mode (bit 7) to tell the EC to stop its auto fan loop
            // and respect the values we write to 0x75B/0x75C.
            byte fanCtrl = ReadECRegister(REG_MANUAL_FAN_CTRL);
            fanCtrl |= FAN_MODE_USER;
            WriteECRegister(REG_MANUAL_FAN_CTRL, fanCtrl);
            DebugLog($"[DIAG] UniwillEC: Set MANUAL_FAN_CTRL USER mode (0x{fanCtrl:X2})");

            SetFanPwmDirect(0, duty);
            SetFanPwmDirect(1, duty);
            DebugLog($"[DIAG] UniwillEC: Set both fans direct PWM to {duty}/200 in USER mode");
        }

        /// <summary>
        /// Attempts to control fan speed by writing to the fanSwitchSpeed register (0x787).
        /// MechControl uses this register with encoding fan_switch_speed (100-12700 RPM).
        /// This may be the actual trigger that makes the EC apply fan speed changes.
        /// </summary>
        public void SetFanSwitchSpeed(int rpm)
        {
            rpm = Math.Clamp(rpm, 100, 12700);

            // fanSwitchSpeed is a u16 — write low byte then high byte
            byte lo = (byte)(rpm & 0xFF);
            byte hi = (byte)((rpm >> 8) & 0xFF);

            WriteECRegister(REG_FAN_SWITCH_SPEED, lo);
            DebugLog($"[DIAG] UniwillEC: Set fanSwitchSpeed(0x787) = {rpm} (0x{rpm:X4})");
        }

        #endregion

        #region Keyboard RGB

        /// <summary>
        /// Sets the keyboard backlight color (RGB).
        /// Each channel is 0-50. After writing R/G/B, we toggle the RGB_APPLY_COLOR trigger.
        /// Note: RGB 0x000000 is interpreted by EC as "restore default", so we clamp to 0x010101.
        /// </summary>
        public void SetKeyboardColor(byte red, byte green, byte blue)
        {
            // Clamp to EC range (0-50)
            red = (byte)Math.Clamp((int)red, 0, 50);
            green = (byte)Math.Clamp((int)green, 0, 50);
            blue = (byte)Math.Clamp((int)blue, 0, 50);

            // Clamp to avoid all-zero (EC interprets as "restore default")
            if (red == 0 && green == 0 && blue == 0)
            {
                red = 1;
                green = 1;
                blue = 1;
            }

            WriteECRegister(REG_RGB_RED, red);
            WriteECRegister(REG_RGB_GREEN, green);
            WriteECRegister(REG_RGB_BLUE, blue);

            // Trigger apply
            byte trigger = ReadECRegister(REG_TRIGGER);
            trigger ^= TRIGGER_RGB_APPLY_COLOR; // toggle bit 5
            WriteECRegister(REG_TRIGGER, trigger);

            DebugLog($"[KBD] UniwillEC: Set keyboard color to R={red} G={green} B={blue}");
        }

        /// <summary>
        /// Sets the keyboard backlight brightness (0-7).
        /// </summary>
        public void SetKeyboardBrightness(int brightness)
        {
            brightness = Math.Clamp(brightness, 0, 7);
            byte kbdStatus = ReadECRegister(REG_KBD_STATUS);

            // Clear brightness bits and apply bit
            kbdStatus = (byte)(kbdStatus & ~KBD_BRIGHTNESS_MASK);
            kbdStatus = (byte)((kbdStatus | (byte)(brightness << 5)) | KBD_APPLY);

            WriteECRegister(REG_KBD_STATUS, kbdStatus);

            DebugLog($"[KBD] UniwillEC: Set keyboard brightness to {brightness}");
        }

        /// <summary>
        /// Returns true if the keyboard supports RGB (not white-only) based on SUPPORT_2 register.
        /// </summary>
        public bool IsRgbKeyboard()
        {
            byte support2 = ReadECRegister(REG_SUPPORT_2);
            bool isRgb = (support2 & RGB_KEYBOARD) != 0;
            DebugLog($"[KBD] UniwillEC: IsRgbKeyboard check — SUPPORT_2(0x{REG_SUPPORT_2:X4}) = 0x{support2:X2}, RGB bit = {isRgb}");
            return isRgb;
        }

        /// <summary>
        /// Returns true if the keyboard is white-only based on KBD_STATUS register.
        /// </summary>
        public bool IsWhiteOnlyKeyboard()
        {
            byte kbdStatus = ReadECRegister(REG_KBD_STATUS);
            DebugLog($"[KBD] UniwillEC: IsWhiteOnlyKeyboard check — KBD_STATUS(0x{REG_KBD_STATUS:X4}) = 0x{kbdStatus:X2}, WHITE_ONLY bit = {(kbdStatus & KBD_WHITE_ONLY) != 0}");
            return (kbdStatus & KBD_WHITE_ONLY) != 0;
        }

        /// <summary>
        /// Enables China mode on BIOS_OEM_2 (0x0782). This is required for RGB keyboard
        /// control on non-China hardware. Without this, the EC may reject RGB writes.
        /// </summary>
        public void SetChinaMode()
        {
            byte biosOem2 = ReadECRegister(REG_BIOS_OEM_2);
            biosOem2 |= ENABLE_CHINA_MODE; // set bit 6
            WriteECRegister(REG_BIOS_OEM_2, biosOem2);
            DebugLog($"[KBD] UniwillEC: Set China mode — BIOS_OEM_2(0x{REG_BIOS_OEM_2:X4}) = 0x{biosOem2:X2}");
        }

        /// <summary>
        /// Triggers the Logo lighting effect on the keyboard.
        /// This toggles bit 6 of the TRIGGER register.
        /// </summary>
        public void TriggerLogoEffect()
        {
            byte trigger = ReadECRegister(REG_TRIGGER);
            trigger ^= TRIGGER_RGB_LOGO_EFFECT; // toggle bit 6
            WriteECRegister(REG_TRIGGER, trigger);
            DebugLog("[KBD] UniwillEC: Triggered Logo keyboard effect");
        }

        /// <summary>
        /// Triggers the Rainbow lighting effect on the keyboard.
        /// This toggles bit 7 of the TRIGGER register.
        /// </summary>
        public void TriggerRainbowEffect()
        {
            byte trigger = ReadECRegister(REG_TRIGGER);
            trigger ^= TRIGGER_RGB_RAINBOW_EFFECT; // toggle bit 7
            WriteECRegister(REG_TRIGGER, trigger);
            DebugLog("[KBD] UniwillEC: Triggered Rainbow keyboard effect");
        }

        /// <summary>
        /// Turns off the keyboard backlight by setting the KBD_POWER_OFF bit.
        /// </summary>
        public void TurnOffKeyboard()
        {
            byte kbdStatus = ReadECRegister(REG_KBD_STATUS);
            kbdStatus |= KBD_POWER_OFF; // set bit 1
            kbdStatus |= KBD_APPLY;     // set bit 4 to tell EC to apply the change
            WriteECRegister(REG_KBD_STATUS, kbdStatus);
            DebugLog($"[KBD] UniwillEC: Keyboard backlight turned off (KBD_STATUS = 0x{kbdStatus:X2})");
        }

        /// <summary>
        /// Turns on the keyboard backlight by clearing the KBD_POWER_OFF bit.
        /// </summary>
        public void TurnOnKeyboard()
        {
            byte kbdStatus = ReadECRegister(REG_KBD_STATUS);
            DebugLog($"[KBD] UniwillEC: TurnOnKeyboard — current KBD_STATUS = 0x{kbdStatus:X2}");
            kbdStatus &= unchecked((byte)~KBD_POWER_OFF); // clear bit 1
            kbdStatus |= KBD_APPLY;     // set bit 4 to tell EC to apply the change
            WriteECRegister(REG_KBD_STATUS, kbdStatus);
            DebugLog($"[KBD] UniwillEC: Keyboard backlight turned on (writing KBD_STATUS = 0x{kbdStatus:X2})");
        }

        /// <summary>
        /// Returns true if fan control is supported by this hardware.
        /// </summary>
        public bool IsFanControlSupported()
        {
            byte support5 = ReadECRegister(REG_SUPPORT_5);
            return (support5 & FAN_SUPPORT) != 0;
        }

        #endregion

        #region Power Presets

        /// <summary>
        /// Gets the current performance mode index (0=quiet, 1=balanced, 2=performance, 3=battery_saver).
        /// </summary>
        public int GetPerformanceMode()
        {
            return ReadECRegister(REG_MODE_INDEX);
        }

        /// <summary>
        /// Sets the performance mode (0=quiet, 1=balanced, 2=performance, 3=battery_saver).
        /// </summary>
        public void SetPerformanceMode(int mode)
        {
            mode = Math.Clamp(mode, 0, 3);
            WriteECRegister(REG_MODE_INDEX, (byte)mode);
            DebugLog($"UniwillEC: Set performance mode to {mode}");
        }

        /// <summary>
        /// Reads the CPU PL1 (long duration power limit) in watts.
        /// </summary>
        public int GetPl1()
        {
            return ReadECRegister(REG_PL1_SETTING);
        }

        /// <summary>
        /// Reads the CPU PL2 (short duration power limit) in watts.
        /// </summary>
        public int GetPl2()
        {
            return ReadECRegister(REG_PL2_SETTING);
        }

        /// <summary>
        /// Reads the CPU PL4 (peak power limit) in watts.
        /// </summary>
        public int GetPl4()
        {
            return ReadECRegister(REG_PL4_SETTING);
        }

        /// <summary>
        /// Sets the CPU PL1 power limit in watts.
        /// </summary>
        public void SetPl1(int watts)
        {
            WriteECRegister(REG_PL1_SETTING, (byte)Math.Clamp(watts, 0, 255));
        }

        /// <summary>
        /// Sets the CPU PL2 power limit in watts.
        /// </summary>
        public void SetPl2(int watts)
        {
            WriteECRegister(REG_PL2_SETTING, (byte)Math.Clamp(watts, 0, 255));
        }

        /// <summary>
        /// Sets the CPU PL4 power limit in watts.
        /// </summary>
        public void SetPl4(int watts)
        {
            WriteECRegister(REG_PL4_SETTING, (byte)Math.Clamp(watts, 0, 255));
        }

        /// <summary>
        /// Reads the CPU PL5 (sustained power limit) in watts.
        /// This appears to be the authoritative power cap on AM5/FireRange.
        /// </summary>
        public int GetPl5()
        {
            return ReadECRegister(REG_PL5_SETTING);
        }

        /// <summary>
        /// Sets the CPU PL5 power limit in watts.
        /// On AM5/FireRange, this may be the actual enforced sustained power cap.
        /// </summary>
        public void SetPl5(int watts)
        {
            WriteECRegister(REG_PL5_SETTING, (byte)Math.Clamp(watts, 0, 255));
        }

        /// <summary>
        /// Sets the OVERBOOST performance profile on the EC (0x0751 bit 4).
        /// From tuxedo_io::uw_set_performance_profile_v1(PROFILE_OVERBOOST):
        ///   read 0x0751, clear bits 1,4,7, then set bit 4 (0x10).
        /// This must be called BEFORE writing PL1/PL2/PL4 to signal the EC
        /// to raise the power ceiling. Without this, the EC re-evaluates PL5
        /// using the default Windows ceiling (55W) when PL1/PL2/PL4 are written.
        /// </summary>
        public void SetOverboostProfile()
        {
            EnsureInitialized();
            try
            {
                byte current = ReadECRaw(REG_MANUAL_FAN_CTRL);
                byte next = (byte)(current & ~(0xa0 | 0x10)); // clear bits 1,4,7
                next |= 0x10; // set bit 4 (OVERBOOST)
                WriteECRaw(REG_MANUAL_FAN_CTRL, next);
                Thread.Sleep(50);
                DebugLog($"[DIAG] UniwillEC: Overboost profile set (0x751: 0x{current:X2} -> 0x{next:X2})");
            }
            catch (Exception ex)
            {
                DebugLog($"[DIAG] UniwillEC: Failed to set overboost profile: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures the power management activation bits are set before writing power limits.
        /// The EC may clear these bits after init (e.g., on mode switch or profile change).
        /// Without them, the EC accepts writes but may not apply them to hardware.
        ///
        /// On NB02, this replicates the Linux tuxedo_io driver sequence from uniwill_keyboard_probe:
        ///   1. uniwill_write_ec_ram(0x0751, 0x00) — reset to balanced
        ///   2. uniwill_set_custom_profile_mode(true) — clear-then-set toggle on 0x0727 bit 6
        ///   3. uniwill_write_ec_ram(0x0741, 0x01) — ensure manual mode
        /// And from uw_set_tdp() (called before each TDP write):
        ///   4. uw_set_performance_profile_v1(PROFILE_OVERBOOST) — 0x0751 = 0x10
        /// </summary>
        public void EnsurePowerManagementActive()
        {
            EnsureInitialized();
            try
            {
                // Step 1: Power management activation (0x0726 bit 7)
                byte cur726 = ReadECRaw(0x0726);
                byte want726 = (byte)(cur726 | 0x80);
                if (cur726 != want726)
                {
                    WriteECRaw(0x0726, want726);
                    Thread.Sleep(50);
                }

                // Step 2: Reset performance profile to balanced (0x00).
                // Linux driver does this before the custom profile toggle.
                WriteECRaw(REG_MANUAL_FAN_CTRL, 0x00);
                Thread.Sleep(50);

                // Step 3: Custom profile mode toggle on 0x0727 bit 6.
                // From tuxedo_keyboard::uniwill_set_custom_profile_mode(true):
                //   read 0x0727, clear bit 6, write back, msleep(50), set bit 6, write back
                byte cur727 = ReadECRaw(REG_CUSTOM_PROFILE);
                byte clear727 = (byte)(cur727 & ~(1 << 6));
                WriteECRaw(REG_CUSTOM_PROFILE, clear727);
                Thread.Sleep(50);
                byte set727 = (byte)(clear727 | (1 << 6));
                WriteECRaw(REG_CUSTOM_PROFILE, set727);
                Thread.Sleep(50);
                DebugLog($"[DIAG] UniwillEC: Custom profile toggle 0x727: 0x{cur727:X2} -> 0x{clear727:X2} -> 0x{set727:X2}");

                // Step 4: Ensure manual mode (0x0741 = 0x01).
                // Linux driver writes this after custom profile toggle.
                WriteECRaw(0x0741, 0x01);
                Thread.Sleep(50);

                // Step 5: Overboost performance profile (0x0751 = 0x10).
                // From tuxedo_io::uw_set_performance_profile_v1(PROFILE_OVERBOOST):
                //   clear bits 1,4,7 (0xa0 | 0x10), then set bit 4 (0x10)
                WriteECRaw(REG_MANUAL_FAN_CTRL, 0x10);
                Thread.Sleep(50);
                DebugLog("[DIAG] UniwillEC: Overboost profile set (0x751=0x10)");
            }
            catch (Exception ex)
            {
                DebugLog($"[DIAG] UniwillEC: Failed to ensure power management active: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies CPU power limits directly to the EC with proper inter-write delays
        /// matching the fan curve activation pattern. This must be called AFTER SMU
        /// commands (slow-limit, fast-limit) because the EC enforces a hardware ceiling
        /// that clamps SMU settings from above.
        ///
        /// Uses the same registers as tuxedo-drivers' uw_set_tdp(): PL1 (0x0783),
        /// PL2 (0x0784), PL4 (0x0785). Sequence mirrors EnsurePowerManagementActive
        /// followed by EC writes with 50ms delays between each.
        /// </summary>
        /// <param name="pl1Watts">Sustained power limit in watts (PL1)</param>
        /// <param name="pl2Watts">Boost power limit in watts (PL2)</param>
        public void ApplyCpuPowerLimits(int pl1Watts, int pl2Watts)
        {
            EnsureInitialized();

            // PL4 (peak/clamp) should be at least as high as PL2 to avoid capping boost.
            int pl4Watts = Math.Max(pl1Watts, pl2Watts);

            lock (_lock)
            {
                DebugLog($"[DIAG] UniwillEC: ApplyCpuPowerLimits(pl1={pl1Watts}W, pl2={pl2Watts}W, pl4={pl4Watts}W)");

                // Step 1: Ensure power management is active (prerequisite for EC to accept power writes)
                try
                {
                    EnsurePowerManagementActiveInternal();
                }
                catch (Exception ex)
                {
                    DebugLog($"[WARN] UniwillEC: EnsurePowerManagementActiveInternal failed: {ex.Message}");
                }

                // Step 2: Write PL1 (0x0783) — sustained power limit
                WriteECRaw(REG_PL1_SETTING, (byte)Math.Clamp(pl1Watts, 0, 255));
                Thread.Sleep(50);

                // Step 3: Write PL2 (0x0784) — boost power limit
                WriteECRaw(REG_PL2_SETTING, (byte)Math.Clamp(pl2Watts, 0, 255));
                Thread.Sleep(50);

                // Step 4: Write PL4 (0x0785) — peak/clamp power limit
                WriteECRaw(REG_PL4_SETTING, (byte)Math.Clamp(pl4Watts, 0, 255));
                Thread.Sleep(100);

                // Verify by reading back
                byte rPl1 = ReadECRaw(REG_PL1_SETTING);
                byte rPl2 = ReadECRaw(REG_PL2_SETTING);
                byte rPl4 = ReadECRaw(REG_PL4_SETTING);
                DebugLog($"[DIAG] UniwillEC: Readback — PL1=0x{rPl1:X2}({rPl1}W) PL2=0x{rPl2:X2}({rPl2}W) PL4=0x{rPl4:X2}({rPl4}W)");
            }
        }

        /// <summary>
        /// Internal version of EnsurePowerManagementActive without the outer try/catch
        /// so callers can handle exceptions themselves.
        /// </summary>
        private void EnsurePowerManagementActiveInternal()
        {
            // Step 1: Power management activation (0x0726 bit 7)
            byte cur726 = ReadECRaw(0x0726);
            byte want726 = (byte)(cur726 | 0x80);
            if (cur726 != want726)
            {
                WriteECRaw(0x0726, want726);
                Thread.Sleep(50);
            }

            // Step 2: Custom profile mode toggle on 0x0727 bit 6.
            // From tuxedo_keyboard::uniwill_set_custom_profile_mode(true):
            //   read 0x0727, clear bit 6, write back, msleep(50), set bit 6, write back
            byte cur727 = ReadECRaw(REG_CUSTOM_PROFILE);
            byte clear727 = (byte)(cur727 & ~(1 << 6));
            WriteECRaw(REG_CUSTOM_PROFILE, clear727);
            Thread.Sleep(50);
            byte set727 = (byte)(clear727 | (1 << 6));
            WriteECRaw(REG_CUSTOM_PROFILE, set727);
            Thread.Sleep(50);

            // Step 3: Ensure manual mode (0x0741 = 0x01)
            WriteECRaw(0x0741, 0x01);
            Thread.Sleep(50);

            // Step 4: Overboost performance profile on 0x0751 using read-modify-write.
            // CRITICAL: 0x0751 (REG_MANUAL_FAN_CTRL) carries dual-purpose bits:
            //   bit 4 = performance profile (Overboost)
            //   bits 5-7 = fan mode (High/Boost/User)
            //   bits 0-2 = fan level
            // A hard overwrite (0x00 or 0x10) destroys the fan mode state, causing
            // the EC to re-enter default fan control and ramp fans to max.
            // Use read-modify-write to only set bit 4, preserving fan bits.
            byte cur751 = ReadECRaw(REG_MANUAL_FAN_CTRL);
            byte want751 = (byte)(cur751 | FAN_MODE_TURBO); // set bit 4 only
            WriteECRaw(REG_MANUAL_FAN_CTRL, want751);
            Thread.Sleep(50);

            DebugLog($"[DIAG] UniwillEC: Power management active for CPU power limits (0x751: 0x{cur751:X2} -> 0x{want751:X2})");
        }

        #endregion

        #region Hardware Info

        /// <summary>
        /// Reads the PROJECT_ID register to identify the hardware revision.
        /// </summary>
        public int GetProjectId()
        {
            return ReadECRegister(REG_PROJECT_ID);
        }

        #endregion

        #region Helpers

        private void EnsureInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException("UniwillECService not initialized. Call Initialize() first.");
            if (_disposed)
                throw new ObjectDisposedException(nameof(UniwillECService));
        }

        #endregion

        #region Logging

        /// <summary>
        /// Logs to Debug, Trace, and Serilog so output appears in VS Debug Output
        /// and in the UCU log file for runtime diagnosis.
        /// </summary>
        private static void DebugLog(string message)
        {
            Debug.WriteLine(message);
            Trace.WriteLine(message);
            try { Log.Information("[EC] {Message}", message); } catch { /* Serilog may not be ready */ }
        }

        /// <summary>
        /// Derives the temp_up (rising threshold) EC array from user temperature steps.
        /// temp_up[i] = step[i] + 1 for zones 1-10; zone 0 = 0.
        /// Padded to 16 elements with 255.
        /// </summary>
        private static byte[] ToEcTempUp(int[] temps)
        {
            byte[] result = new byte[16];
            Array.Fill(result, (byte)255);
            int count = Math.Min(temps.Length, 11);
            for (int i = 0; i < count; i++)
            {
                if (i == 0)
                    result[i] = 0;
                else
                    result[i] = (byte)Math.Clamp(temps[i] + 1, 0, 255);
            }
            return result;
        }

        /// <summary>
        /// Derives the temp_down (falling/hysteresis threshold) EC array from user temperature steps.
        /// temp_down[i] = step[i] - 1 for zones 1-10; zone 0 = step[0] - 2 (clamped to 0).
        /// Padded to 16 elements with 255.
        /// </summary>
        private static byte[] ToEcTempDown(int[] temps)
        {
            byte[] result = new byte[16];
            Array.Fill(result, (byte)255);
            int count = Math.Min(temps.Length, 11);
            for (int i = 0; i < count; i++)
            {
                if (i == 0)
                    result[i] = (byte)Math.Clamp(temps[i] - 2, 0, 255);
                else
                    result[i] = (byte)Math.Clamp(temps[i] - 1, 0, 255);
            }
            return result;
        }

        #endregion
        #region Battery

        /// <summary>
        /// Battery telemetry data read from EC registers and Windows battery API.
        /// </summary>
        public struct BatteryTelemetry
        {
            public bool IsAcPowered;
            public int StateOfCharge;       // percentage 0-100
            public int HealthPercent;       // percentage, 0 if unknown
            public int TemperatureC;        // Celsius, negative if unknown
            public int CycleCount;          // -1 if unknown
            public int PresentVoltageMv;    // millivolts, 0 if unknown
            public int ChargeRateMa;        // milliamps (positive=charging, negative=discharging), 0 if unknown
        }

        // Battery mode raw values from MechControl IDY_7Y profile.
        // bitfields: batteryMode mask 0x38 (bits 3-5 of register 0x7A6)
        // batteryModes: High Capacity (0x08), Balanced (0x18), Stationary (0x28)
        private static readonly byte[] BatteryModeValues = { 0x08, 0x18, 0x28 };

        /// <summary>
        /// Reads the battery charging mode from EC register 0x7A6.
        /// Mode bits are in bits 3-5 (mask 0x38). Raw values: 0x08, 0x18, 0x28.
        /// Returns: 0 = 100% (High Capacity), 1 = 80% (Balanced), 2 = 60% (Stationary)
        /// Returns 0 if no mode is set (raw bits 3-5 are clear).
        /// </summary>
        public int GetBatteryMode()
        {
            EnsureInitialized();
            byte val = ReadECRegister(REG_BATTERY_MODE);
            // Mask to bits 3-5 (the battery mode field). The EC may have other
            // flags in the same register, so an exact-byte match is unreliable.
            byte masked = (byte)(val & 0x38);
            int mode = Array.IndexOf(BatteryModeValues, masked);
            if (mode < 0)
                mode = 0; // Default to High Capacity if value unrecognized
            DebugLog($"[Battery] GetBatteryMode: raw=0x{val:X2}, masked=0x{masked:X2}, mode={mode}");
            return mode;
        }

        /// <summary>
        /// Writes the battery charging mode to EC register 0x7A6.
        /// Mode: 0 = 100% (High Capacity, 0x08), 1 = 80% (Balanced, 0x18), 2 = 60% (Stationary, 0x28)
        /// </summary>
        public void SetBatteryMode(int mode)
        {
            EnsureInitialized();
            if (mode < 0 || mode > 2)
                throw new ArgumentOutOfRangeException(nameof(mode), "Battery mode must be 0, 1, or 2.");

            byte val = BatteryModeValues[mode];
            WriteECRegister(REG_BATTERY_MODE, val);
            DebugLog($"[Battery] SetBatteryMode: mode={mode} (raw=0x{val:X2})");
        }

      // Battery telemetry EC registers (from MechControl decompiled analysis)
        // All multi-byte values are little-endian uint16
        public const ushort REG_BATTERY_POWER_SOURCE = 0x0490;  // bit 0 = AC connected
        public const ushort REG_BATTERY_DESIGN_CAP_LO = 0x0402; // Design capacity (mAh, LE u16)
        public const ushort REG_BATTERY_FULL_CHARGE_CAP_LO = 0x0404; // Full charge capacity (mAh, LE u16)
        public const ushort REG_BATTERY_DESIGN_VOLTAGE_LO = 0x0408; // Design voltage (mV, LE u16)
        public const ushort REG_BATTERY_CHARGE_RATE_LO = 0x0434; // Charge rate (mA, LE u16)
        public const ushort REG_BATTERY_REMAINING_CAP_LO = 0x0436; // Remaining capacity (mAh, LE u16)
        public const ushort REG_BATTERY_PRESENT_VOLTAGE_LO = 0x0438; // Present voltage (mV, LE u16)
        public const ushort REG_BATTERY_TEMP_LO = 0x04A2; // Temperature (Kelvin*10, LE u16)
        public const ushort REG_BATTERY_CYCLE_COUNT_LO = 0x04A6; // Cycle count (LE u16)

        /// <summary>
        /// Reads battery telemetry directly from EC registers (matches MechControl approach).
        /// Returns null if battery info is unavailable.
        /// </summary>
        public BatteryTelemetry? ReadBatteryTelemetry()
        {
            EnsureInitialized();
            try
            {
                // Power source: bit 0 of 0x490
                byte powerSource = ReadECRegister(REG_BATTERY_POWER_SOURCE);
                bool isAcPowered = (powerSource & 1) != 0;

                // State of charge from 0x4AB (byte, percentage)
                int stateOfCharge = ReadECRegister(REG_BATTERY_SOC);

                // Cycle count: LE u16 at 0x4A6
                int cycleCount = ReadEcUInt16(REG_BATTERY_CYCLE_COUNT_LO);

                // Temperature: LE u16 at 0x4A2, value is Kelvin�10
                int tempKelvinX10 = ReadEcUInt16(REG_BATTERY_TEMP_LO);
                int temperatureC = (int)(tempKelvinX10 / 10.0 - 273.15);

                // Present voltage: LE u16 at 0x438 (mV)
                int presentVoltageMv = ReadEcUInt16(REG_BATTERY_PRESENT_VOLTAGE_LO);

                // Charge rate: LE u16 at 0x434 (mA)
                int chargeRateMa = ReadEcUInt16(REG_BATTERY_CHARGE_RATE_LO);

                // Health: FullChargeCapacity / DesignCapacity � 100
                int designCapacityMah = ReadEcUInt16(REG_BATTERY_DESIGN_CAP_LO);
                int fullChargeCapacityMah = ReadEcUInt16(REG_BATTERY_FULL_CHARGE_CAP_LO);
                int healthPercent = designCapacityMah > 0
                    ? (int)((long)fullChargeCapacityMah * 100 / designCapacityMah)
                    : 0;

                var telemetry = new BatteryTelemetry
                {
                    IsAcPowered = isAcPowered,
                    StateOfCharge = stateOfCharge,
                    HealthPercent = healthPercent,
                    TemperatureC = temperatureC,
                    CycleCount = cycleCount,
                    PresentVoltageMv = presentVoltageMv,
                    ChargeRateMa = chargeRateMa
                };

                Debug.WriteLine($"[EC] [Battery] ReadBatteryTelemetry: AC={(isAcPowered ? "plugged" : "unplugged")}, SoC={stateOfCharge}%, Health={healthPercent}%, Temp={temperatureC}�C, Cycles={cycleCount}, Voltage={presentVoltageMv}mV, ChargeRate={chargeRateMa}mA");
                return telemetry;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads only the battery temperature from the EC (Kelvin�10 at 0x4A2).
        /// Lightweight � reads a single register pair, no full telemetry.
        /// </summary>
        public int ReadBatteryTemperatureC()
        {
            EnsureInitialized();
            int tempKelvinX10 = ReadEcUInt16(REG_BATTERY_TEMP_LO);
            return (int)(tempKelvinX10 / 10.0 - 273.15);
        }

        /// <summary>
        /// Reads a little-endian uint16 from two consecutive EC registers.
        /// </summary>
        private int ReadEcUInt16(ushort loAddress)
        {
            byte lo = ReadECRegister(loAddress);
            byte hi = ReadECRegister((ushort)(loAddress + 1));
            return (hi << 8) | lo;
        }

        #endregion
    
  
        


        #region IDisposable

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        #endregion

        #region Enums

        public enum FanCurveSource
        {
            Cpu,
            Gpu
        }

        #endregion
    }
}

