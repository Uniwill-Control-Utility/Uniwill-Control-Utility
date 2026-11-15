using UCU.Contracts;

namespace UCU.Hardware.Ec
{
    /// <summary>
    /// The GPU power-control slice of <see cref="EcEngine"/> that the ROM-override store
    /// composes with, behind an interface so the store's apply/restore ordering runs in a
    /// test without an EC driver.
    /// </summary>
    public interface IEcGpuPowerControl
    {
        /// <summary>The board's capability and its DefaultTGP, the base 0x0744 is an offset from.</summary>
        GpuPowerInfoDto GetGpuPowerInfo();

        /// <summary>Writes the cTGP/DB register set; <c>dbOffset</c> 0 is Dynamic Boost off.</summary>
        void ActivateGpuPowerControl(int ctgpOffset, int dbOffset);

        /// <summary>
        /// Clears the enable bits and puts 0x0744 back to the byte captured before our first
        /// activation (a constant restores the wrong TGP on another board).
        /// </summary>
        void DeactivateGpuPowerControl();
    }
}
