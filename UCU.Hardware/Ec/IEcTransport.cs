namespace UCU.Hardware.Ec
{
    /// <summary>
    /// The byte-level transport the EC engine drives: one register address in, one
    /// byte out. Keeping the seam this small is what makes the engine testable — a
    /// fake transport can record every write and the tests can assert the exact
    /// sequence the firmware expects, without a laptop.
    ///
    /// Implementations are not required to be thread-safe: <see cref="EcEngine"/>
    /// serialises all access under its own lock, which is also the ordering the EC
    /// needs (register writes are stateful sequences, not independent writes).
    /// </summary>
    public interface IEcTransport : IDisposable
    {
        /// <summary>True when the transport has a usable handle to the device.</summary>
        bool IsOpen { get; }

        /// <summary>
        /// Opens the device if it is not open. Throws <see cref="System.ComponentModel.Win32Exception"/>
        /// when the driver is missing or not running.
        /// </summary>
        void Open();

        /// <summary>
        /// Drops the current handle and opens a fresh one, used by the engine after a
        /// failed transfer (the driver handle goes stale after the ACPI device is
        /// re-enumerated, e.g. on resume from sleep).
        /// </summary>
        void Reconnect();

        /// <summary>Reads one register. Throws when the transfer fails.</summary>
        byte Read(ushort address);

        /// <summary>Writes one register. Throws when the transfer fails.</summary>
        void Write(ushort address, byte value);
    }
}
