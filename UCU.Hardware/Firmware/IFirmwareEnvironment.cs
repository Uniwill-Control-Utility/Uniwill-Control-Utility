namespace UCU.Hardware.Firmware
{
    /// <summary>
    /// The seam over the Win32 firmware-variable calls (the W-variants of
    /// Get/SetFirmwareEnvironmentVariable). The production implementation
    /// (<see cref="Win32FirmwareEnvironment"/>) enables
    /// SeSystemEnvironmentPrivilege and calls through; the tests inject a
    /// scripted variable store, because no test may write a real machine's
    /// firmware (the OEM entry is factory data on the SPI flash).
    ///
    /// The calls report their Win32 result verbatim: the service layer's
    /// questions ("is the variable absent, or did the call fail?") need the
    /// error code, not a collapsed null. ERROR_ENVVAR_NOT_FOUND (203) means
    /// the variable genuinely does not exist; anything else means the call
    /// failed (privilege, buffer, firmware), which is policy-relevant at the
    /// OemMagicDoor handshake.
    /// </summary>
    public interface IFirmwareEnvironment
    {
        /// <summary>The variable's bytes, or null when the read returned
        /// nothing; <paramref name="error"/> carries the Win32 code (0 = the
        /// bytes came back).</summary>
        byte[]? Read(string name, string guid, out uint error);

        /// <summary>True when the write landed; <paramref name="error"/>
        /// carries the Win32 code on failure (0 on success).</summary>
        bool Write(string name, string guid, byte[] data, out uint error);
    }
}
