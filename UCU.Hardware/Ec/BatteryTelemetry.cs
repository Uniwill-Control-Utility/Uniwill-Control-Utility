namespace UCU.Hardware.Ec
{
    /// <summary>
    /// Battery telemetry read from the EC battery registers. Field comments describe
    /// the "unknown" sentinel each read uses when the register carries no data, which
    /// is what the dashboard renders as a dash.
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
}
