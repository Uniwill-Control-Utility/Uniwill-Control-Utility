namespace Uniwill_Control_Utility.Views.Controls;

internal enum DashboardBatteryHealthState
{
    Unavailable,
    Healthy,
    Degraded
}

internal static class DashboardBatteryHealthClassifier
{
    public static DashboardBatteryHealthState Classify(int percent)
        => percent switch
        {
            >= 80 => DashboardBatteryHealthState.Healthy,
            > 0 => DashboardBatteryHealthState.Degraded,
            _ => DashboardBatteryHealthState.Unavailable
        };
}
