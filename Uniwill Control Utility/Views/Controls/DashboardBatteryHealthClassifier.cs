namespace Uniwill_Control_Utility.Views.Controls;

internal enum DashboardBatteryHealthState
{
    Unavailable,
    Healthy,
    Reduced
}

internal static class DashboardBatteryHealthClassifier
{
    public static DashboardBatteryHealthState Classify(int percent)
        => percent switch
        {
            >= 80 => DashboardBatteryHealthState.Healthy,
            > 0 => DashboardBatteryHealthState.Reduced,
            _ => DashboardBatteryHealthState.Unavailable
        };
}
