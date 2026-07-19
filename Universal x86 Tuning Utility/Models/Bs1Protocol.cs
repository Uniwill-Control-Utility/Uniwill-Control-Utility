using System;

namespace Universal_x86_Tuning_Utility.Models
{
    /// <summary>
    /// Frame magic bytes for the Flydigi BS1 protocol.
    /// Identical to BS2+ (5A A5) with the same checksum algorithm.
    /// </summary>
    public static class Bs1Magic
    {
        public const byte Start0 = 0x5A;
        public const byte Start1 = 0xA5;
    }

    /// <summary>
    /// Command byte identifiers for the Flydigi BS1 protocol.
    /// The BS1 supports a reduced command set compared to BS2+: no RGB, no device settings,
    /// no sub-gear levels.
    ///
    /// Frame format: 5A A5 &lt;CMD&gt; &lt;LEN&gt; &lt;PAYLOAD...&gt; &lt;CHECKSUM&gt;
    /// LEN = 2 + payload_length (counting CMD and LEN bytes).
    /// CHECKSUM = sum(CMD, LEN, PAYLOAD...) &amp; 0xFF  (same as BS2+).
    ///
    /// Unlike BS2+ which uses HID, the BS1 transports these frames over BLE GATT
    /// with no Report ID prefix.
    /// </summary>
    public static class Bs1Command
    {
        public const byte QueryDeviceInfo     = 0x01;
        public const byte GearMode            = 0x08;

        public const byte RealtimeRpm         = 0x21;
        public const byte EnterRealtimeMode   = 0x23;
        public const byte ExitRealtimeMode    = 0x24;

        // Device-to-host notification
        public const byte StatusNotify        = 0xEF;

        // Heartbeat keepalive (required every ~3 seconds or device disconnects)
        public const byte Heartbeat           = 0x04;
    }

    /// <summary>
    /// Fixed gear mode values for the GearMode (0x08) command.
    /// Identical to BS2+.
    /// </summary>
    public static class Bs1GearMode
    {
        public const byte Quiet     = 0x01;
        public const byte Standard  = 0x02;
        public const byte Strong    = 0x03;
        public const byte Overclock = 0x04;
    }

    /// <summary>
    /// Work mode values reported in the 0xEF status notification.
    /// </summary>
    public static class Bs1WorkMode
    {
        /// <summary>Manual / fixed gear mode (even value, e.g. 0x04).</summary>
        public const byte Manual = 0x04;

        /// <summary>Realtime / auto RPM mode (odd value, e.g. 0x05).</summary>
        public const byte Realtime = 0x05;
    }

    /// <summary>
    /// Default factory RPM values for each gear level on the BS1.
    /// The BS1 has a maximum RPM of 3000 (vs 4000 on BS2+).
    /// No sub-levels — each gear maps to a single fixed RPM.
    /// </summary>
    public static class Bs1DefaultGearRpm
    {
        public const ushort Gear0_Quiet     = 1300;
        public const ushort Gear1_Standard  = 2000;
        public const ushort Gear2_Strong    = 2500;
        public const ushort Gear3_Overclock = 3000;

        /// <summary>Minimum RPM the device accepts.</summary>
        public const ushort MinRpm = 1300;

        /// <summary>Maximum RPM the BS1 accepts (lower than BS2+ which is 4000).</summary>
        public const ushort MaxRpm = 3000;
    }

    /// <summary>
    /// Frame builder and parser for the Flydigi BS1 5A A5 protocol.
    /// Differs from BS2+ in one way:
    ///   1. No Report ID prefix (BLE transport, not HID)
    /// The checksum algorithm is identical to BS2+: sum &amp; 0xFF.
    /// </summary>
    public static class Bs1Frame
    {
        /// <summary>
        /// Builds a protocol frame: 5A A5 &lt;CMD&gt; &lt;LEN&gt; &lt;PAYLOAD...&gt; &lt;CHECKSUM&gt;.
        /// LEN = 2 + payload.Length (counting CMD + LEN themselves).
        /// CHECKSUM = sum(CMD, LEN, PAYLOAD...) &amp; 0xFF.
        /// </summary>
        public static byte[] Build(byte cmd, params byte[] payload)
        {
            int length = 2 + payload.Length;
            int frameSize = 2 + 1 + 1 + payload.Length + 1; // magic(2) + cmd + len + payload + checksum

            var frame = new byte[frameSize];
            frame[0] = Bs1Magic.Start0;
            frame[1] = Bs1Magic.Start1;
            frame[2] = cmd;
            frame[3] = (byte)length;

            Array.Copy(payload, 0, frame, 4, payload.Length);

            frame[frameSize - 1] = ComputeChecksum(cmd, (byte)length, payload);

            return frame;
        }

        /// <summary>
        /// Builds a heartbeat frame for connection keepalive.
        /// The BS1 requires a heartbeat every ~3 seconds or it will disconnect.
        /// </summary>
        public static byte[] BuildHeartbeat()
        {
            return Build(Bs1Command.Heartbeat);
        }

        /// <summary>
        /// Parses a raw BLE notification or protocol frame, extracting the command and payload.
        /// Returns null if the frame is invalid (bad magic bytes, checksum mismatch, or too short).
        /// </summary>
        public static ParsedFrame? Parse(byte[] data)
        {
            if (data == null || data.Length < 5)
                return null;

            // BS1 uses BLE transport — no Report ID prefix.
            // Look for magic bytes at offset 0.
            if (data[0] != Bs1Magic.Start0 || data[1] != Bs1Magic.Start1)
                return null;

            byte cmd = data[2];
            byte length = data[3];

            if (length < 2)
                return null;

            int frameLen = 2 + length + 1; // magic(2) + length field content + checksum(1)
            if (data.Length < frameLen)
                return null;

            int payloadLen = length - 2;
            var payload = new byte[payloadLen];
            if (payloadLen > 0)
                Array.Copy(data, 4, payload, 0, payloadLen);

            byte expectedChecksum = data[2 + length];
            byte actualChecksum = ComputeChecksum(cmd, length, payload);

            return new ParsedFrame
            {
                Command = cmd,
                Payload = payload,
                ChecksumValid = expectedChecksum == actualChecksum
            };
        }

        /// <summary>
        /// Computes the BS1 checksum: sum of all data bytes &amp; 0xFF.
        /// Identical to BS2+ checksum algorithm.
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
            public byte Command { get; init; }
            public byte[] Payload { get; init; }
            public bool ChecksumValid { get; init; }
        }
    }

    /// <summary>
    /// Parsed device status notification (0xEF command) from the BS1.
    /// </summary>
    public readonly struct Bs1StatusNotification
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
    public static class Bs1StatusParser
    {
        /// <summary>
        /// Parses a status notification payload into a structured result.
        /// Returns null if the payload is too short or malformed.
        /// </summary>
        public static Bs1StatusNotification? Parse(byte[] payload)
        {
            if (payload == null || payload.Length < 7)
                return null;

            return new Bs1StatusNotification
            {
                GearSettings = payload[0],
                WorkMode = payload[1],
                CurrentRpm = (ushort)(payload[3] | (payload[4] << 8)),
                TargetRpm = (ushort)(payload[5] | (payload[6] << 8))
            };
        }
    }
}
