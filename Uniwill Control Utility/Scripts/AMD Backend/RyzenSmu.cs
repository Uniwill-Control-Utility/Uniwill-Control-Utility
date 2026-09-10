using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.AMD_Backend;
using Uniwill_Control_Utility.Scripts.Misc;
using static RyzenSmu.RyzenSMU;

[assembly: CLSCompliant(false)]


namespace RyzenSmu
{
    class Addresses
    {
        public static void setAddresses()
        {
            RyzenSmu.Smu.SMU_PCI_ADDR = 0x00000000u;
            RyzenSmu.Smu.SMU_OFFSET_ADDR = 0xB8u;
            RyzenSmu.Smu.SMU_OFFSET_DATA = 0xBCu;

            // Only FireRange (Socket AM5) is supported.
            if (Family.FAM == Family.RyzenFamily.FireRange) Socket_AM5_V1();
        }

        private static void Socket_FT5_FP5_AM4()
        {
            RyzenSmu.Smu.MP1_ADDR_MSG = 0x3B10528u;
            RyzenSmu.Smu.MP1_ADDR_RSP = 0x3B10564u;
            RyzenSmu.Smu.MP1_ADDR_ARG = 0x3B10998u;

            RyzenSmu.Smu.PSMU_ADDR_MSG = 0x03B10A20u;
            RyzenSmu.Smu.PSMU_ADDR_RSP = 0x03B10A80u;
            RyzenSmu.Smu.PSMU_ADDR_ARG = 0x03B10A88u;

            SMUCommands.commands = new List<(string, bool, uint)>
            {
                ("enable-feature",true, 0x05u),
                ("disable-feature",true, 0x06u),
                ("stapm-limit",true, 0x1au),
                ("stapm-limit",false, 0x2eu),
                ("stapm-time",true , 0x1eu),
                ("stapm-time",false , 0x32u),
                ("fast-limit",true , 0x1bu),
                ("fast-limit",false , 0x30u),
                ("slow-limit",true , 0x1cu),
                ("slow-limit",false , 0x2fu),
                ("slow-time",true , 0x1du),
                ("slow-time",false , 0x31u),
                ("tctl-temp",true , 0x1fu),
                ("tctl-temp",false , 0x33u),
                ("cHTC-temp",false , 0x56u),
                ("vrm-current",true , 0x20u),
                ("vrm-current",false , 0x34u),
                ("vrmmax-current",true , 0x22u),
                ("vrmmax-current",false , 0x36u),
                ("vrmsoc-current",true , 0x21u),
                ("vrmsoc-current",false , 0x35u),
                ("vrmsocmax-current",true , 0x23u),
                ("vrmsocmax-current",false , 0x37u),
                ("psi0-current",true , 0x24u),
                ("psi0-current",false , 0x38u),
                ("psi0soc-current",true , 0x25u),
                ("psi0soc-current",false , 0x39u),
                ("prochot-deassertion-ramp",true , 0x26u),
                ("prochot-deassertion-ramp",false , 0x3au),
                ("power-saving",true , 0x19u),
                ("max-performance",true , 0x18u),
                ("enable-oc",true , 0x58u),
                ("disable-oc",true , 0x3fu),
                ("oc-clk",false , 0x7du),
                ("per-core-oc-clk",false , 0x7eu),
                ("oc-clk",true , 0x59u),
                ("per-core-oc-clk",true , 0x5au),
                ("oc-clk",true , 0x41u),
                ("oc-volt",true , 0x5bu),
                ("oc-volt",false , 0x7cu),
                ("oc-volt",true , 0x40u),
                ("set-gpuclockoverdrive-byvid",true , 0x3du),
                ("set-gpuclockoverdrive-byvid",false , 0x61u),
                ("pbo-scalar",true , 0x57u),
                ("pbo-scalar",false , 0x63u),
                ("get-pbo-scalar",false , 0x62u),
                ("max-cpuclk",true, 0x44u),
                ("min-cpuclk",true, 0x45u),
                ("max-gfxclk",true, 0x46u),
                ("max-gfxclk",false, 0x68u),
                ("min-gfxclk",true, 0x47u),
                ("min-gfxclk",false, 0x69u),
                ("max-socclk-frequency",true, 0x48u),
                ("max-socclk-frequency",false, 0x66u),
                ("min-socclk-frequency",true, 0x49u),
                ("min-socclk-frequency",false, 0x67u),
                ("max-fclk-frequency",true, 0x4au),
                ("min-fclk-frequency",true, 0x4bu),
                ("max-vcn",true, 0x4cu),
                ("min-vcn",true, 0x4du),
                ("max-lclk",true, 0x4eu),
                ("min-lclk",true, 0x4fu),
                ("set-cogfx",false, 0x59u),
                ("setcpu-freqto-ramstate",true , 0x2fu),
                ("stopcpu-freqto-ramstate",true , 0x30u),
                ("stopcpu-freqto-ramstate",true , 0x31u),
                ("set-ulv-vid",true , 0x35u),
                ("set-vddoff-vid",true , 0x3au),
                ("set-vmin-freq",true , 0x3bu),
                ("get-sustained-power-and-thm-limit",true , 0x43u),
                ("get-sustained-power-and-thm-limit",false , 0x65u),
                ("get-pbo-fused-power-limit",false , 0x7Fu),
                ("get-pbo-fused-slow-limit",false , 0x80u),
                ("get-pbo-fused-fast-limit",false , 0x81u),
                ("get-pbo-fused-apu-slow-limit",false , 0x82u),
                ("get-pbo-fused-vrmtdc-limit",false , 0x83u),
                ("get-pbo-fused-vrmsoc-current",false , 0x84u)
            };
        }

        private static void Socket_FP6_AM4()
        {
            RyzenSmu.Smu.MP1_ADDR_MSG = 0x3B10528u;
            RyzenSmu.Smu.MP1_ADDR_RSP = 0x3B10564u;
            RyzenSmu.Smu.MP1_ADDR_ARG = 0x3B10998u;

            RyzenSmu.Smu.PSMU_ADDR_MSG = 0x03B10A20u;
            RyzenSmu.Smu.PSMU_ADDR_RSP = 0x03B10A80u;
            RyzenSmu.Smu.PSMU_ADDR_ARG = 0x03B10A88u;

            SMUCommands.commands = new List<(string, bool, uint)>
            {
                ("enable-feature",true, 0x05u),
                ("disable-feature",true, 0x07u),
                ("stapm-limit",true , 0x14u),
                ("stapm-limit",false , 0x31u),
                ("ppt-limit",false , 0x33u),
                ("stapm-time",true , 0x18u),
                ("stapm-time",false , 0x36u),
                ("fast-limit",true , 0x15u),
                ("fast-limit",false , 0x32u),
                ("slow-limit",true , 0x16u),
                ("slow-limit",false , 0x33u),
                ("slow-time",true , 0x17u),
                ("slow-time",false , 0x35u),
                ("tctl-temp",true , 0x19u),
                ("cHTC-temp",false , 0x37u),
                ("apu-skin-temp",true , 0x38u),
                ("dgpu-skin-temp",true , 0x39u),
                ("dgpu-skin-temp",false , 0x92u),
                ("vrm-current",true , 0x1au),
                ("vrm-current",false , 0x38u),
                ("vrmmax-current",true , 0x1cu),
                ("vrmmax-current",false , 0x3au),
                ("vrmsoc-current",true , 0x1bu),
                ("vrmsoc-current",false , 0x39u),
                ("vrmsocmax-current",true , 0x1du),
                ("vrmsocmax-current",false , 0x3bu),
                ("psi0-current",true , 0x1eu),
                ("psi0-current",false , 0x3cu),
                ("psi0soc-current",true , 0x1fu),
                ("psi0soc-current",false , 0x3du),
                ("prochot-deassertion-ramp",true , 0x20u),
                ("prochot-deassertion-ramp",false , 0x3eu),
                ("skin-temp-limit",true , 0x53u),
                ("apu-slow-limit",true , 0x21u),
                ("apu-slow-limit",false , 0x34u),
                ("power-saving",true , 0x12u),
                ("max-performance",true , 0x11u),
                ("enable-oc",false , 0x17u),
                ("enable-oc",true , 0x2fu),
                ("disable-oc",false , 0x18u),
                ("disable-oc",true , 0x30u),
                ("oc-clk",true , 0x31u),
                ("oc-clk",false , 0x19u),
                ("per-core-oc-clk",true , 0x32u),
                ("per-core-oc-clk",false , 0x1au),
                ("oc-volt",true , 0x33u),
                ("oc-volt",false , 0x1bu),
                ("set-gpuclockoverdrive-byvid",true , 0x34u),
                ("gfx-clk",false , 0x89u),
                ("gfx-clk",false , 0x1cu),
                ("pbo-scalar",true , 0x49u),
                ("pbo-scalar",false , 0x3fu),
                ("get-pbo-scalar",false , 0x0fu),
                ("set-cogfx",false , 0x53u),
                ("get-cogfx-options",false , 0xC6u),
                ("get-sustained-power-and-thm-limit",true , 0x5bu),
                ("get-pbo-fused-power-limit",false , 0x11u),
                ("get-pbo-fused-slow-limit",false , 0x12u),
                ("get-pbo-fused-fast-limit",false , 0x13u),
                ("get-pbo-fused-apu-slow-limit",false , 0x14u),
                ("get-pbo-fused-vrmtdc-limit",false , 0x15u),
                ("get-pbo-fused-vrmsoc-current",false , 0x16u)
            };
        }

        private static void Socket_FF3()
        {
            RyzenSmu.Smu.MP1_ADDR_MSG = 0x3B10528u;
            RyzenSmu.Smu.MP1_ADDR_RSP = 0x3B10578u;
            RyzenSmu.Smu.MP1_ADDR_ARG = 0x3B10998u;

            RyzenSmu.Smu.PSMU_ADDR_MSG = 0x03B10a20u;
            RyzenSmu.Smu.PSMU_ADDR_RSP = 0x03B10a80u;
            RyzenSmu.Smu.PSMU_ADDR_ARG = 0x03B10a88u;

            SMUCommands.commands = new List<(string, bool, uint)>
            {
                ("enable-feature",true, 0x05u),
                ("disable-feature",true, 0x07u),
                ("stapm-limit",true, 0x14u),
                ("stapm-limit",false , 0x31u),
                ("stapm-time",true , 0x18u),
                ("fast-limit",true , 0x15u),
                ("slow-limit",true , 0x16u),
                ("slow-time",true , 0x17u),
                ("tctl-temp",true , 0x19u),
                ("cHTC-temp",false , 0x37u),
                ("apu-skin-temp",true , 0x33u),
                ("vrm-current",true , 0x1au),
                ("vrmmax-current",true , 0x1cu),
                ("vrmsoc-current",true , 0x1bu),
                ("vrmsocmax-current",true , 0x1du),
                ("vrmgfx-current",true , 0x1eu),
                ("vrmgfxmax-current",true , 0x1fu),
                ("prochot-deassertion-ramp",true , 0x22u),
                ("gfx-clk",false , 0x89u),
                ("gfx-clk",false , 0x1cu),
                ("power-saving",true , 0x12u),
                ("max-performance",true , 0x11u),
                ("set-cogfx",false , 0xb7u),
                ("get-sustained-power-and-thm-limit",true , 0x54u),
                ("skin-temp-limit",true , 0x4au),
                ("apu-slow-limit",true , 0x23u)
            };
        }

        private static void Socket_AM4_V1()
        {
            RyzenSmu.Smu.MP1_ADDR_MSG = 0X3B10528u;
            RyzenSmu.Smu.MP1_ADDR_RSP = 0X3B10564u;
            RyzenSmu.Smu.MP1_ADDR_ARG = 0X3B10598u;

            RyzenSmu.Smu.PSMU_ADDR_MSG = 0x03B1051Cu;
            RyzenSmu.Smu.PSMU_ADDR_RSP = 0X03B10568u;
            RyzenSmu.Smu.PSMU_ADDR_ARG = 0X03B10590u;

            SMUCommands.commands = new List<(string, bool, uint)>
            {
                ("ppt-limit",false, 0x64u),
                ("ppt-limit",true, 0x31u),
                ("tdc-limit",false , 0x65u),
                ("edc-limit",false , 0x66u),
                ("tctl-temp",false , 0x68u),
                ("pbo-scalar",false , 0x6au),
                ("oc-clk", false, 0x6cu),
                ("oc-clk", true, 0x39u),
                ("per-core-oc-clk",false , 0x6du),
                ("oc-volt", false, 0x6eu),
                ("oc-volt", true, 0x38u),
                ("enable-oc",true , 0x23u),
                ("enable-oc",false , 0x6bu),
                ("disable-oc",true , 0x24u),
                ("get-sustained-power-and-thm-limit",true , 0x36u),
                ("setcpu-freqto-ramstate",true , 0x23u),
                ("stopcpu-freqto-ramstate",true , 0x24u),
                ("stopcpu-freqto-ramstate",true , 0x25u)
            };
        }

        private static void Socket_AM4_V2()
        {
            RyzenSmu.Smu.MP1_ADDR_MSG = 0x3B10530u;
            RyzenSmu.Smu.MP1_ADDR_RSP = 0x3B1057Cu;
            RyzenSmu.Smu.MP1_ADDR_ARG = 0x3B109C4u;

            RyzenSmu.Smu.PSMU_ADDR_MSG = 0x03B10524u;
            RyzenSmu.Smu.PSMU_ADDR_RSP = 0x03B10570u;
            RyzenSmu.Smu.PSMU_ADDR_ARG = 0x03B10A40u;

            SMUCommands.commands = new List<(string, bool, uint)>
            {
                ("enable-feature",true, 0x03u),
                ("disable-feature",true, 0x04u),
                ("ppt-limit",true, 0x3Du),
                ("ppt-limit",false, 0x53u),
                ("tdc-limit",true , 0x3Bu),
                ("tdc-limit",false , 0x54u),
                ("edc-limit",true , 0x3cu),
                ("edc-limit",false , 0x55u),
                ("tctl-temp",true , 0x3Eu),
                ("tctl-temp",false , 0x56u),
                ("pbo-scalar",false , 0x58u),
                ("oc-clk", true, 0x26u),
                ("oc-clk", false, 0x5cu),
                ("per-core-oc-clk",true , 0x27u),
                ("per-core-oc-clk",false , 0x5du),
                ("oc-volt", true, 0x28u),
                ("oc-volt", false, 0x61u),
                ("enable-oc",true , 0x24u),
                ("enable-oc",false , 0x5au),
                ("disable-oc",true , 0x25u),
                ("disable-oc",false , 0x5bu),
                ("set-boost-limit-frequency",true , 0x2bu),
                ("get-pbo-scalar",false , 0x6cu),
                ("get-sustained-power-and-thm-limit",true , 0x23u),
                ("get-overclocking-support",false , 0x6fu),
                ("get-coper-options",false , 0x7cu)
            };

        }

        private static void Socket_AM5_V1()
        {
            RyzenSmu.Smu.MP1_ADDR_MSG = 0x3B10530u;
            RyzenSmu.Smu.MP1_ADDR_RSP = 0x3B1057Cu;
            RyzenSmu.Smu.MP1_ADDR_ARG = 0x3B109C4u;

            RyzenSmu.Smu.PSMU_ADDR_MSG = 0x03B10524u;
            RyzenSmu.Smu.PSMU_ADDR_RSP = 0x03B10570u;
            RyzenSmu.Smu.PSMU_ADDR_ARG = 0x03B10A40u;

            SMUCommands.commands = new List<(string, bool, uint)>
            {
                ("enable-feature",true, 0x03u), // Use MP1 address
                ("disable-feature",true, 0x04u),
                ("stapm-limit",true, 0x4fu),
                ("stapm-time",true , 0x53u),
                ("fast-limit",true , 0x3eu),
                ("slow-limit",true , 0x5fu),
                ("slow-time",true , 0x60u),
                ("vrm-current",true , 0x3cu),
                ("vrm-current",false , 0x57u), // Use RSMU address
                ("ppt-limit",true, 0x3eu),
                ("ppt-limit",false, 0x56u),
                ("tdc-limit",true , 0x3cu),
                ("tdc-limit",false , 0x57u),
                ("edc-limit",true , 0x3du),
                ("edc-limit",false , 0x58u),
                ("tctl-temp",true , 0x3fu),
                ("tctl-temp",false , 0x59u),
                ("pbo-scalar",false , 0x5bu),
                ("oc-clk", false, 0x5fu),
                ("per-core-oc-clk",false , 0x60u),
                ("oc-volt", false, 0x61u),
                ("enable-oc",false , 0x5du),
                ("disable-oc",false , 0x5eu),
                ("slow-limit",false , 0xcbu),
                ("skin-temp-limit",true , 0x5eu),
                ("apu-slow-limit",true , 0x60u),
                ("vrmmax-current",true , 0x3du),
                ("vrmmax-current",false , 0x58u),
                ("cHTC-temp",false , 0x59u),
                ("get-pbo-scalar",false , 0x6du),
                ("set-coall",false , 0x07u),
                ("set-coall",true , 0x36u),
                ("set-coper",false , 0x06u),
                ("set-coper",true , 0x35u),
                ("set-cogfx",false , 0xA7u),
                ("set-boost-limit-frequency",true , 0x2bu),
                ("set-vddoff-vid",true , 0x4bu),
                ("set-fll-btc-enable",true , 0x37u),
                ("get-sustained-power-and-thm-limit",true , 0x23u),
                ("get-overclocking-support",false , 0x6fu),
                ("get-max-cpu-clk",false , 0x6eu),
                ("get-min-gfx-clk",false , 0xCeu),
                ("get-max-gfx-clk",false , 0xCfu),
                ("get-curr-gfx-clk",false , 0xD8u),
                ("disable-prochot",false , 0x5du),
                ("get-coper-options",false , 0xD5u),
                ("get-cogfx-options",false , 0xD7u),
                ("get-pbo-fused-vrmsoc-current",false , 0xD9u),
                ("get-pbo-fused-vrmtdc-limit",false , 0xDbu),
                ("get-pbo-fused-slow-limit",false , 0xDcu),
                ("get-pbo-fused-apu-slow-limit",false , 0xDau),
                ("get-pbo-fused-tctl-temp",false , 0xDeu)
            };
        }
    }

    static class SMUCommands
    {
        private static List<(string Name, bool IsMp1, uint Address)> _commands = new();
        private static Dictionary<string, (bool IsMp1, uint Address)[]> _commandIndex = new(StringComparer.Ordinal);

        public static List<(string, bool, uint)> commands
        {
            get => _commands;
            set
            {
                _commands = value ?? new List<(string, bool, uint)>();
                _commandIndex = _commands
                    .GroupBy(command => command.Name, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(command => (command.IsMp1, command.Address)).ToArray(),
                        StringComparer.Ordinal);
            }
        }

        public static Smu RyzenAccess = new Smu();

        public static void applySettings(string commandName, uint value)
        {
            uint[] args = new uint[6];
            args[0] = value;
            Execute(commandName, args);
        }

        private static void Execute(string commandName, uint[] args)
        {
            if (!_commandIndex.TryGetValue(commandName, out (bool IsMp1, uint Address)[]? matchingCommands))
                throw new ArgumentException($"Command '{commandName}' not found");

            if (!RyzenAccess.EnsureInitialised())
                throw new InvalidOperationException("AMD PawnIO failed to initialise.");

            var originalArguments = (uint[])args.Clone();
            Status lastStatus = Status.FAILED;
            foreach ((bool isMp1, uint address) in matchingCommands)
            {
                Array.Copy(originalArguments, args, originalArguments.Length);
                lastStatus = isMp1
                    ? RyzenAccess.SendMp1(address, ref args)
                    : RyzenAccess.SendRsmu(address, ref args);

                DiagnosticLogger.LogInfo($"SMU {commandName} via {(isMp1 ? "MP1" : "RSMU")} 0x{address:X} arg=0x{originalArguments[0]:X} -> {(int)lastStatus} ({lastStatus})");

                if (lastStatus == Status.OK)
                    return;
            }

            throw new InvalidOperationException($"SMU command '{commandName}' failed with status {lastStatus}.");
        }
    }

    class Smu
    {
        private static readonly object initializationLock = new();
        public static RyzenSMU? ryzenSMU;
        public static AMDPawnIo? pawnIo;
        private RyzenSMU.Mailbox? mp1Mailbox;
        private RyzenSMU.Mailbox? rsmuMailbox;

        public bool EnsureInitialised()
        {
            lock (initializationLock)
            {
                if (pawnIo?.IsLoaded == true && ryzenSMU != null)
                    return true;

                string modulePath = Path.Combine(
                    Settings.Default.Path,
                    "Assets",
                    "AMD",
                    "PawnIO",
                    "RyzenSMU.bin");

                pawnIo = AMDPawnIo.LoadModuleFromFile(modulePath);

                if (!pawnIo.IsLoaded)
                {
                    DiagnosticLogger.LogInfo("SMU PawnIO failed to load from: " + modulePath);
                    return false;
                }

                DiagnosticLogger.LogInfo($"SMU PawnIO loaded. MP1=0x{MP1_ADDR_MSG:X}/0x{MP1_ADDR_RSP:X}/0x{MP1_ADDR_ARG:X}, RSMU=0x{PSMU_ADDR_MSG:X}/0x{PSMU_ADDR_RSP:X}/0x{PSMU_ADDR_ARG:X}");

                ryzenSMU = new RyzenSMU(pawnIo);
                ryzenSMU.Open();
                mp1Mailbox = ryzenSMU.RegisterMailbox("MP1", MP1_ADDR_MSG, MP1_ADDR_RSP, MP1_ADDR_ARG, 6u);
                rsmuMailbox = ryzenSMU.RegisterMailbox("RSMU", PSMU_ADDR_MSG, PSMU_ADDR_RSP, PSMU_ADDR_ARG, 6u);
                return true;
            }
        }

        public void Deinitialize()
        {
            lock (initializationLock)
            {
                if (pawnIo != null && pawnIo.IsLoaded)
                {
                    ryzenSMU?.Close();
                    pawnIo.Close();
                }

                mp1Mailbox = null;
                rsmuMailbox = null;
                ryzenSMU = null;
                pawnIo = null;
            }
        }

        public static uint SMU_PCI_ADDR { get; set; }
        public static uint SMU_OFFSET_ADDR { get; set; }
        public static uint SMU_OFFSET_DATA { get; set; }

        public static uint MP1_ADDR_MSG { get; set; }
        public static uint MP1_ADDR_RSP { get; set; }
        public static uint MP1_ADDR_ARG { get; set; }

        public static uint PSMU_ADDR_MSG { get; set; }
        public static uint PSMU_ADDR_RSP { get; set; }
        public static uint PSMU_ADDR_ARG { get; set; }


        public Status SendMp1(uint message, ref uint[] arguments)
        {
            if (!EnsureInitialised() || ryzenSMU == null || mp1Mailbox == null)
                return Status.FAILED;

            return ryzenSMU.SendSmuCommand(mp1Mailbox, message, ref arguments);
        }

        public Status SendRsmu(uint message, ref uint[] arguments)
        {
            if (!EnsureInitialised() || ryzenSMU == null || rsmuMailbox == null)
                return Status.FAILED;

            return ryzenSMU.SendSmuCommand(rsmuMailbox, message, ref arguments);
        }

    }
    /// <summary>
    /// High-level interface for sending commands to the AMD SMU via PawnIO.
    /// Handles mutex co-ordination, mailbox selection, and argument flow.
    /// </summary>
    internal sealed class RyzenSMU : IDisposable
    {
        // PawnIO function names used for register access
        private const string IOCTL_READ_SMU_REGISTER = "ioctl_read_smu_register";
        private const string IOCTL_WRITE_SMU_REGISTER = "ioctl_write_smu_register";

        // Maximum number of polls whilst waiting for the SMU to respond
        private const ushort POLL_LIMIT = 8192;

        // Global mutex names.
        private const string ISA_MUTEX_NAME = "Global\\Access_ISABUS.HTP.Method";
        private const string PCI_MUTEX_NAME = "Global\\Access_PCI";

        private readonly AMDPawnIo _pawnIo;

        // Mutex handles owned by this instance
        private Mutex? _isaMutex;
        private Mutex? _pciMutex;

        private bool _disposed;

        // --------------------------------------------------------------------
        // Mailbox handling
        // --------------------------------------------------------------------

        /// <summary>
        /// Represents a single SMU mailbox (for example, MP1 or RSMU).
        /// Addresses are fully configurable and may be changed at runtime.
        /// </summary>
        public sealed class Mailbox
        {
            public Mailbox(string name, uint maxArgs = 6u)
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Unnamed" : name.Trim();
                MAX_ARGS = maxArgs;
            }

            /// <summary>
            /// Friendly name used for debugging/logging only.
            /// </summary>
            public string Name { get; }

            public uint MAX_ARGS { get; set; }

            public uint SMU_ADDR_MSG { get; set; }
            public uint SMU_ADDR_RSP { get; set; }
            public uint SMU_ADDR_ARG { get; set; }

            public bool IsValid =>
                MAX_ARGS > 0u &&
                SMU_ADDR_MSG != 0u &&
                SMU_ADDR_RSP != 0u &&
                SMU_ADDR_ARG != 0u;
        }

        // Registered mailboxes by name (MP1, RSMU, etc.)
        private readonly Dictionary<string, Mailbox> _mailboxes =
            new(StringComparer.OrdinalIgnoreCase);

        // Last command metadata (handy when diagnosing failures)
        public string? LastMailboxName { get; private set; }
        public uint LastCommand { get; private set; }
        public Status LastStatus { get; private set; } = Status.FAILED;

        public RyzenSMU(AMDPawnIo pawnIo)
        {
            _pawnIo = pawnIo ?? throw new ArgumentNullException(nameof(pawnIo));
        }

        /// <summary>
        /// SMU-visible status codes only.
        /// Transport or mutex failures are reported as FAILED.
        /// </summary>
        public enum Status : byte
        {
            OK = 0x01,
            FAILED = 0xFF,
            UNKNOWN_CMD = 0xFE,
            CMD_REJECTED_PREREQ = 0xFD,
            CMD_REJECTED_BUSY = 0xFC
        }

        // --------------------------------------------------------------------
        // Lifecycle
        // --------------------------------------------------------------------

        /// <summary>
        /// Opens the global mutexes required for SMU access.
        /// Safe to call multiple times.
        /// </summary>
        public void Open()
        {
            ThrowIfDisposed();

            _isaMutex ??= CreateOrOpenMutex(ISA_MUTEX_NAME);
            _pciMutex ??= CreateOrOpenMutex(PCI_MUTEX_NAME);
        }

        /// <summary>
        /// Releases mutex handles owned by this instance.
        /// </summary>
        public void Close()
        {
            DisposeMutex(ref _isaMutex);
            DisposeMutex(ref _pciMutex);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Close();
            _disposed = true;
        }

        // --------------------------------------------------------------------
        // Mailbox registration
        // --------------------------------------------------------------------

        /// <summary>
        /// Registers (or replaces) a mailbox with a friendly name.
        /// Useful for keeping a known set such as "MP1" and "RSMU".
        /// </summary>
        public Mailbox RegisterMailbox(
            string name,
            uint msgAddr,
            uint rspAddr,
            uint argAddr,
            uint maxArgs = 6u)
        {
            var mailbox = new Mailbox(name, maxArgs)
            {
                SMU_ADDR_MSG = msgAddr,
                SMU_ADDR_RSP = rspAddr,
                SMU_ADDR_ARG = argAddr
            };

            _mailboxes[mailbox.Name] = mailbox;
            return mailbox;
        }

        // --------------------------------------------------------------------
        // Public SMU command entry point
        // --------------------------------------------------------------------

        /// <summary>
        /// Sends a command to the SMU using the specified mailbox.
        /// On success, the SMU may overwrite values in the args array.
        /// </summary>
        public Status SendSmuCommand(Mailbox mailbox, uint message, ref uint[] args)
        {
            ThrowIfDisposed();

            if (mailbox == null || !mailbox.IsValid || message == 0u)
            {
                RememberDebug(mailbox, message, Status.UNKNOWN_CMD);
                return Status.UNKNOWN_CMD;
            }

            // Lazily open mutexes if the caller forgot
            if (_pciMutex == null)
                Open();

            if (_pciMutex == null || !WaitForMutex(_pciMutex, 10))
            {
                RememberDebug(mailbox, message, Status.FAILED);
                return Status.FAILED;
            }

            try
            {
                var status = ExecuteMailboxFlow(mailbox, message, ref args);
                RememberDebug(mailbox, message, status);
                return status;
            }
            catch
            {
                RememberDebug(mailbox, message, Status.FAILED);
                return Status.FAILED;
            }
            finally
            {
                SafeReleaseMutex(_pciMutex);
            }
        }

        // --------------------------------------------------------------------
        // Core mailbox protocol
        // --------------------------------------------------------------------

        private Status ExecuteMailboxFlow(Mailbox mb, uint msg, ref uint[] args)
        {
            // Ensure the mailbox is idle before starting
            if (!WaitForResponse(mb.SMU_ADDR_RSP, out _))
                return Status.FAILED;

            // Clear response register
            if (!Write32(mb.SMU_ADDR_RSP, 0u))
                return Status.FAILED;

            // Write input arguments
            if (!WriteArguments(mb, args))
                return Status.FAILED;

            // Send command
            if (!Write32(mb.SMU_ADDR_MSG, msg))
                return Status.FAILED;

            // Wait for completion
            if (!WaitForResponse(mb.SMU_ADDR_RSP, out uint rsp))
                return Status.FAILED;

            // The response register should contain a byte-sized status code
            if (rsp > byte.MaxValue)
                return Status.FAILED;

            Status status = unchecked((Status)rsp);

            // Read back arguments only on success
            if (status == Status.OK && args != null && args.Length > 0)
            {
                if (!ReadArguments(mb, ref args))
                    return Status.FAILED;
            }

            return status;
        }

        private bool WaitForResponse(uint rspReg, out uint value)
        {
            value = 0u;

            for (ushort i = 0; i < POLL_LIMIT; i++)
            {
                if (Read32(rspReg, out value) && value != 0u)
                    return true;
            }

            return false;
        }

        private bool WriteArguments(Mailbox mb, uint[] args)
        {
            uint[] payload = PrepareArguments(args, mb.MAX_ARGS);

            // Guard against overflow when calculating base + (index * 4)
            uint maxSafe = uint.MaxValue - (mb.MAX_ARGS * 4u);

            for (int i = 0; i < payload.Length; i++)
            {
                uint reg = mb.SMU_ADDR_ARG + (uint)(i * 4);
                if (reg > maxSafe)
                    continue;

                if (!Write32(reg, payload[i]))
                    return false;
            }

            return true;
        }

        private bool ReadArguments(Mailbox mb, ref uint[] args)
        {
            int count = Math.Min(args.Length, (int)mb.MAX_ARGS);

            // Same overflow guard as above
            uint maxSafe = uint.MaxValue - (mb.MAX_ARGS * 4u);

            for (int i = 0; i < count; i++)
            {
                uint reg = mb.SMU_ADDR_ARG + (uint)(i * 4);
                if (reg > maxSafe)
                    continue;

                if (!Read32(reg, out uint value))
                    return false;

                args[i] = value;
            }

            return true;
        }

        // --------------------------------------------------------------------
        // Low-level register access
        // --------------------------------------------------------------------

        private bool Read32(uint reg, out uint value)
        {
            value = 0u;

            long[] inBuf = { unchecked((long)reg) };
            long[] outBuf = new long[1];

            int hr = _pawnIo.ExecuteHr(IOCTL_READ_SMU_REGISTER, inBuf, 1u, outBuf, 1u, out _);
            if (hr != 0)
                return false;

            value = unchecked((uint)outBuf[0]);
            return true;
        }

        private bool Write32(uint reg, uint value)
        {
            long[] inBuf = { unchecked((long)reg), unchecked((long)value) };

            int hr = _pawnIo.ExecuteHr(
                IOCTL_WRITE_SMU_REGISTER,
                inBuf,
                2u,
                Array.Empty<long>(),
                0u,
                out _);

            return hr == 0;
        }

        private static uint[] PrepareArguments(uint[] args, uint maxArgs)
        {
            uint[] result = new uint[maxArgs];

            if (args == null || args.Length == 0)
                return result;

            int copy = Math.Min(args.Length, (int)maxArgs);
            for (int i = 0; i < copy; i++)
                result[i] = args[i];

            return result;
        }

        // --------------------------------------------------------------------
        // Mutex helpers
        // --------------------------------------------------------------------

        private static Mutex? CreateOrOpenMutex(string name)
        {
            try
            {
                var security = BuildWorldWritableSecurity();
#if NETFRAMEWORK
                return new Mutex(false, name, out _, security);
#else
                return MutexAcl.Create(false, name, out _, security);
#endif
            }
            catch (UnauthorizedAccessException)
            {
                try { return Mutex.OpenExisting(name); }
                catch { return null; }
            }
            catch
            {
                return null;
            }
        }

        private static MutexSecurity BuildWorldWritableSecurity()
        {
            var sid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var rule = new MutexAccessRule(
                sid,
                MutexRights.FullControl,
                AccessControlType.Allow);

            var security = new MutexSecurity();
            security.AddAccessRule(rule);
            return security;
        }

        private static bool WaitForMutex(Mutex mutex, int timeoutMs)
        {
            try { return mutex.WaitOne(timeoutMs, false); }
            catch (AbandonedMutexException) { return true; }
            catch { return false; }
        }

        private static void SafeReleaseMutex(Mutex mutex)
        {
            try { mutex.ReleaseMutex(); }
            catch { /* Mutex release failed; already released or owned by another thread */ }
        }

        private static void DisposeMutex(ref Mutex? mutex)
        {
            try { mutex?.Close(); }
            catch { /* Mutex dispose failed; already closed */ }
            finally { mutex = null; }
        }

        private void RememberDebug(Mailbox? mb, uint cmd, Status status)
        {
            LastMailboxName = mb?.Name;
            LastCommand = cmd;
            LastStatus = status;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RyzenSMU));
        }
    }
}
