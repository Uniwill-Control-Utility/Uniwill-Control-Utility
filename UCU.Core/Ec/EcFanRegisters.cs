namespace UCU.Core.Ec
{
    /// <summary>
    /// EC fan-table register addresses that the app's write planners target directly.
    /// The engine service owns the full register map; these are the handful the UI
    /// layer composes raw block writes for (the smart-fan duty plan), so they live in
    /// the shared domain assembly rather than leaking the hardware assembly upward.
    /// The values match <c>EcEngine.REG_*</c>; the hardware tests pin the engine side.
    /// </summary>
    public static class EcFanRegisters
    {
        /// <summary>CPU fan duty table, 16 bytes, duty on the EC 0-200 scale.</summary>
        public const ushort CpuFanSpeedTable = 0x0F20;

        /// <summary>GPU fan duty table, 16 bytes, duty on the EC 0-200 scale.</summary>
        public const ushort GpuFanSpeedTable = 0x0F50;
    }
}
