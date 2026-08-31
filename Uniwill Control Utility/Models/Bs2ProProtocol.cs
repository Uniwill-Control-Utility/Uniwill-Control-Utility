using System;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Frame magic bytes that delimit every BS2 Pro protocol packet.
    /// </summary>
    public static class Bs2ProMagic
    {
        public const byte Start0 = 0x5A;
        public const byte Start1 = 0xA5;
    }

    /// <summary>
    /// HID report IDs used by the Flydigi BS2 Pro protocol.
    /// Input reports (device → host) use 0x01, output reports (host → device) use 0x02.
    /// </summary>
    public static class Bs2ProReportId
    {
        public const byte Input  = 0x01;
        public const byte Output = 0x02;
    }

    /// <summary>
    /// HID report lengths used by the Flydigi BS2 Pro protocol.
    /// </summary>
    public static class Bs2ProReportLength
    {
        /// <summary>Standard control report length (25 bytes: 1 report ID + 24 payload).</summary>
        public const int Control = 25;

        /// <summary>RGB light report length (65 bytes: 1 report ID + 64 payload).</summary>
        public const int Light = 65;
    }

    /// <summary>
    /// Command byte identifiers for the Flydigi BS2 Pro cooling pad protocol.
    /// Every command is wrapped in a frame: 5A A5 &lt;CMD&gt; &lt;LEN&gt; &lt;PAYLOAD...&gt; &lt;CHECKSUM&gt;
    /// LEN = 2 + payload_length (counting CMD and LEN themselves).
    /// CHECKSUM = sum(CMD, LEN, PAYLOAD...) &amp; 0xFF.
    /// </summary>
    public static class Bs2ProCommand
    {
        public const byte QueryDeviceInfo     = 0x01;
        public const byte QueryConfigFlag     = 0x02;
        public const byte QueryConfigSnapshot = 0x04;
        public const byte GearMode            = 0x08;
        public const byte PowerOnStart        = 0x0C;
        public const byte SmartStartStop      = 0x0D;
        public const byte RealtimeRpm         = 0x21;
        public const byte EnterRealtimeMode   = 0x23;
        public const byte ExitRealtimeMode    = 0x24;
        public const byte QueryWorkMode       = 0x25;
        public const byte SetGearRpm          = 0x26;
        public const byte QueryGearTable      = 0x27;

        // RGB / light strip commands
        public const byte RgbUploadInit  = 0x41;
        public const byte RgbChunkWrite  = 0x42;
        public const byte RgbCommit      = 0x43;
        public const byte RgbDynamicMode = 0x44;
        public const byte RgbStatus      = 0x45;
        public const byte RgbEnable      = 0x46;
        public const byte RgbFrameWrite  = 0x47;
        public const byte GearLight      = 0x48;

        // Device-to-host notification
        public const byte StatusNotify   = 0xEF;
    }

    /// <summary>
    /// Fixed gear mode values for the GearMode (0x08) command.
    /// </summary>
    public static class Bs2ProGearMode
    {
        public const byte Quiet     = 0x01;
        public const byte Standard  = 0x02;
        public const byte Strong    = 0x03;
        public const byte Overclock = 0x04;
    }

    /// <summary>
    /// Power-On Start (0x0C) command values.
    /// </summary>
    public static class Bs2ProPowerOnStart
    {
        public const byte Enabled  = 0x01;
        public const byte Disabled = 0x02;
    }

    /// <summary>
    /// Smart Start/Stop (0x0D) command values.
    /// </summary>
    public static class Bs2ProSmartStartStop
    {
        public const byte Off       = 0x00;
        public const byte Immediate = 0x01;
        public const byte Delayed   = 0x02;
    }

    /// <summary>
    /// Gear indicator light (0x48) command values.
    /// </summary>
    public static class Bs2ProGearLight
    {
        public const byte Off  = 0x00;
        public const byte On   = 0x01;
    }

    /// <summary>
    /// RGB enable (0x46) command values.
    /// </summary>
    public static class Bs2ProRgbEnable
    {
        public const byte Off = 0x00;
        public const byte On  = 0x01;
    }

    /// <summary>
    /// RGB animation speed values used in frame 0 of the light strip upload sequence.
    /// </summary>
    public static class Bs2ProRgbSpeed
    {
        public const byte Fast   = 0x05;
        public const byte Medium = 0x0A;
        public const byte Slow   = 0x0F;
    }

    /// <summary>
    /// Work mode values reported in the 0xEF status notification.
    /// </summary>
    public static class Bs2ProWorkMode
    {
        /// <summary>Manual / fixed gear mode (even value, e.g. 0x04).</summary>
        public const byte Manual = 0x04;

        /// <summary>Realtime / auto RPM mode (odd value, e.g. 0x05).</summary>
        public const byte Realtime = 0x05;
    }

    /// <summary>
    /// Default factory RPM values for each gear level.
    /// Gear indices 0-3 map to Quiet / Standard / Strong / Overclock.
    /// Sub-levels 0-2 map to Low / Medium / High.
    /// </summary>
    public static class Bs2ProDefaultGearRpm
    {
        // Gear 0 — Quiet
        public const ushort Gear0Low     = 1300;
        public const ushort Gear0Medium  = 1700;
        public const ushort Gear0High    = 1900;

        // Gear 1 — Standard
        public const ushort Gear1Low     = 2100;
        public const ushort Gear1Medium  = 2400;
        public const ushort Gear1High    = 2700;

        // Gear 2 — Strong
        public const ushort Gear2Low     = 2800;
        public const ushort Gear2Medium  = 3000;
        public const ushort Gear2High    = 3300;

        // Gear 3 — Overclock
        public const ushort Gear3Low     = 3500;
        public const ushort Gear3Medium  = 3700;
        public const ushort Gear3High    = 4000;

        /// <summary>Minimum RPM the device accepts.</summary>
        public const ushort MinRpm = 1300;

        /// <summary>Maximum RPM the device accepts.</summary>
        public const ushort MaxRpm = 4000;
    }

    /// <summary>
    /// Flydigi HID device product IDs.
    /// Vendor ID is 0x37D7 for all devices.
    /// </summary>
    public static class Bs2ProProductId
    {
        public const ushort B2      = 0x1001;
        public const ushort B2Pro   = 0x1002;
        public const ushort B3      = 0x1003;
        public const ushort B3Pro   = 0x1004;

        public const ushort VendorId = 0x37D7;

        /// <summary>All known Flydigi BS series product IDs for enumeration.</summary>
        public static readonly int[] AllProductIds = { B2, B2Pro, B3, B3Pro };
    }

    /// <summary>
    /// Frame builder and parser for the Flydigi BS2 Pro 5A A5 protocol.
    /// </summary>
    public static class Bs2ProFrame
    {
        /// <summary>
        /// Builds a protocol frame: 5A A5 &lt;CMD&gt; &lt;LEN&gt; &lt;PAYLOAD...&gt; &lt;CHECKSUM&gt;.
        /// LEN = 2 + payload.Length (counting CMD and LEN bytes).
        /// CHECKSUM = sum(CMD, LEN, PAYLOAD...) &amp; 0xFF.
        /// </summary>
        public static byte[] Build(byte cmd, params byte[] payload)
        {
            int length = 2 + payload.Length; // CMD + LEN themselves count in LEN field
            int frameSize = 2 + 1 + 1 + payload.Length + 1; // magic(2) + cmd + len + payload + checksum

            var frame = new byte[frameSize];
            frame[0] = Bs2ProMagic.Start0;
            frame[1] = Bs2ProMagic.Start1;
            frame[2] = cmd;
            frame[3] = (byte)length;

            Array.Copy(payload, 0, frame, 4, payload.Length);

            frame[frameSize - 1] = ComputeChecksum(cmd, (byte)length, payload);

            return frame;
        }

        /// <summary>
        /// Wraps a protocol frame into a HID output report by prepending the report ID
        /// and padding to the specified report length.
        /// </summary>
        public static byte[] BuildReport(byte[] frame, int reportLength = Bs2ProReportLength.Control)
        {
            if (reportLength <= 0 || reportLength < frame.Length + 1)
                reportLength = frame.Length + 1;

            var report = new byte[reportLength];
            report[0] = Bs2ProReportId.Output;
            Array.Copy(frame, 0, report, 1, frame.Length);

            return report;
        }

        /// <summary>
        /// Parses a raw HID input report or protocol frame, extracting the command and payload.
        /// Automatically detects whether the data starts with a Report ID byte.
        /// Returns null if the frame is invalid (bad magic bytes, checksum mismatch, or too short).
        /// </summary>
        public static ParsedFrame? Parse(byte[] data)
        {
            if (data == null || data.Length < 5)
                return null;

            // Determine offset: check for Report ID prefix or direct magic bytes.
            // Input reports (device → host) use report ID 0x01, output reports use 0x02.
            int offset;
            byte reportId = 0;

            if (data.Length >= 3 &&
                (data[0] == Bs2ProReportId.Input || data[0] == Bs2ProReportId.Output) &&
                data[1] == Bs2ProMagic.Start0 &&
                data[2] == Bs2ProMagic.Start1)
            {
                offset = 1;
                reportId = data[0];
            }
            else if (data.Length >= 2 && data[0] == Bs2ProMagic.Start0
                     && data[1] == Bs2ProMagic.Start1)
            {
                offset = 0;
            }
            else
            {
                return null;
            }

            // Minimum frame: magic(2) + cmd + len + checksum = 5 bytes
            if (data.Length < offset + 5)
                return null;

            byte cmd = data[offset + 2];
            byte length = data[offset + 3];

            if (length < 2)
                return null;

            // Total frame: magic(2) + length field content + checksum(1)
            int frameLen = 2 + length + 1;
            if (data.Length < offset + frameLen)
                return null;

            int payloadLen = length - 2;
            var payload = new byte[payloadLen];
            Array.Copy(data, offset + 4, payload, 0, payloadLen);

            byte expectedChecksum = data[offset + 2 + length];
            byte actualChecksum = ComputeChecksum(cmd, length, payload);

            return new ParsedFrame
            {
                ReportId = reportId,
                Command = cmd,
                Payload = payload,
                ChecksumValid = expectedChecksum == actualChecksum
            };
        }

        /// <summary>
        /// Computes the checksum: sum of all data bytes (CMD through PAYLOAD) &amp; 0xFF.
        /// </summary>
        private static byte ComputeChecksum(byte cmd, byte length, byte[]? payload)
        {
            byte sum = (byte)(cmd + length);
            if (payload != null)
            {
                for (int i = 0; i < payload.Length; i++)
                    sum = (byte)(sum + payload[i]);
            }
            return sum;
        }

        /// <summary>
        /// Parsed result from a protocol frame.
        /// </summary>
        public readonly struct ParsedFrame
        {
            public byte ReportId { get; init; }
            public byte Command { get; init; }
            public byte[] Payload { get; init; }
            public bool ChecksumValid { get; init; }
        }
    }

    /// <summary>
    /// Parsed device status notification (0xEF command) from the cooling pad.
    /// </summary>
    public readonly struct Bs2ProStatusNotification
    {
        /// <summary>Raw gear settings byte (high nibble = max gear, low nibble = selected gear).</summary>
        public byte GearSettings { get; init; }

        /// <summary>Work mode byte (e.g. 0x04 = manual, 0x05 = realtime).</summary>
        public byte WorkMode { get; init; }

        /// <summary>Current fan RPM reported by the device (little-endian from payload).</summary>
        public ushort CurrentRpm { get; init; }

        /// <summary>Target fan RPM set by the host (little-endian from payload).</summary>
        public ushort TargetRpm { get; init; }

        /// <summary>Whether the device is in realtime/auto RPM mode.</summary>
        public bool IsRealtimeMode => (WorkMode & 0x01) == 1;

        /// <summary>Maximum gear level code (high nibble of GearSettings).</summary>
        public byte MaxGearCode => (byte)((GearSettings >> 4) & 0x0F);

        /// <summary>Selected gear level code (low nibble of GearSettings).</summary>
        public byte SelectedGearCode => (byte)(GearSettings & 0x0F);
    }

    /// <summary>
    /// Parser for the 0xEF status notification payload.
    /// Payload layout (7+ bytes):
    ///   [0] gear_settings
    ///   [1] work_mode
    ///   [2] reserved
    ///   [3..4] current RPM (little-endian)
    ///   [5..6] target RPM (little-endian)
    /// </summary>
    public static class Bs2ProStatusParser
    {
        /// <summary>
        /// Parses a status notification payload into a structured result.
        /// Returns null if the payload is too short or malformed.
        /// </summary>
        public static Bs2ProStatusNotification? Parse(byte[] payload)
        {
            if (payload == null || payload.Length < 7)
                return null;

            return new Bs2ProStatusNotification
            {
                GearSettings = payload[0],
                WorkMode = payload[1],
                CurrentRpm = (ushort)(payload[3] | (payload[4] << 8)),
                TargetRpm = (ushort)(payload[5] | (payload[6] << 8))
            };
        }

        /// <summary>
        /// Decodes the max gear code (high nibble of gear_settings byte) to a human-readable name.
        /// </summary>
        public static string MaxGearName(byte code) => code switch
        {
            0x2 => "Standard",
            0x4 => "Performance",
            0x6 => "Extreme",
            _ => $"Unknown(0x{code:X})"
        };

        /// <summary>
        /// Decodes the selected gear code (low nibble of gear_settings byte) to a human-readable name.
        /// </summary>
        public static string SelectedGearName(byte code) => code switch
        {
            0x8 => "Quiet",
            0xA => "Standard",
            0xC => "Performance",
            0xE => "Extreme",
            _ => $"Unknown(0x{code:X})"
        };

        /// <summary>
        /// Decodes the smart start/stop mode from the work_mode byte bits.
        /// Bits 1-3 encode: 0x02=off, 0x04=immediate, 0x08=delayed.
        /// </summary>
        public static string SmartStartStopName(byte workMode) => (workMode & 0x0E) switch
        {
            0x02 => "Off",
            0x04 => "Immediate",
            0x08 => "Delayed",
            _ => ""
        };
    }

    /// <summary>
    /// Query response parser for the gear RPM table (0x27 command response).
    /// Payload contains 4 gear entries, each 2 bytes little-endian RPM.
    /// </summary>
    public static class Bs2ProGearTableParser
    {
        /// <summary>
        /// Parses a gear RPM table response payload.
        /// Returns array of 4 RPM values (gear 0-3), or null if payload is too short.
        /// </summary>
        public static ushort[]? Parse(byte[] payload)
        {
            if (payload == null || payload.Length < 8)
                return null;

            var table = new ushort[4];
            for (int i = 0; i < 4; i++)
            {
                table[i] = (ushort)(payload[i * 2] | (payload[i * 2 + 1] << 8));
            }
            return table;
        }
    }

    /// <summary>
    /// RGB handshake sub-commands used during the 0x45 (RgbStatus) phase of the upload sequence.
    /// </summary>
    public static class Bs2ProRgbHandshake
    {
        /// <summary>Heartbeat query (0x45 02).</summary>
        public const byte Heartbeat = 0x02;

        /// <summary>Heartbeat acknowledge (0x45 03 01).</summary>
        public const byte HeartbeatAck = 0x03;

        /// <summary>Acknowledge parameter.</summary>
        public const byte AckParam = 0x01;
    }

    /// <summary>
    /// RGB upload initialization sub-commands used during the 0x41 (RgbUploadInit) phase.
    /// </summary>
    public static class Bs2ProRgbUpload
    {
        /// <summary>Init transfer (0x41 02).</summary>
        public const byte Init = 0x02;

        /// <summary>Init confirm (0x41 03 01).</summary>
        public const byte Confirm = 0x03;

        /// <summary>Confirm parameter.</summary>
        public const byte ConfirmParam = 0x01;
    }

    /// <summary>
    /// RGB commit sub-commands used during the 0x43 (RgbCommit) phase.
    /// </summary>
    public static class Bs2ProRgbCommit
    {
        /// <summary>Apply/commit animation (0x43 01).</summary>
        public const byte Apply = 0x01;
    }

    /// <summary>
    /// RGB smart-temp mode sub-commands used during the 0x44 (RgbDynamicMode) phase.
    /// </summary>
    public static class Bs2ProRgbDynamicMode
    {
        /// <summary>Activate smart-temp mode (0x44 01).</summary>
        public const byte SmartTemp = 0x01;
    }

    /// <summary>
    /// RGB frame write sub-commands used during the 0x47 (RgbFrameWrite) phase.
    /// </summary>
    public static class Bs2ProRgbFrameIndex
    {
        /// <summary>Header frame index (frame 0).</summary>
        public const byte Header = 0x00;
    }
}
