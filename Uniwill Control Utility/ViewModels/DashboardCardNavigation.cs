using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.ViewModels
{
    public enum DashboardCard
    {
        Cpu,
        Gpu,
        Memory,
        Battery,
        HydroUi,
        Flydigi
    }

    public readonly record struct DashboardCardDestination(
        string PageTag,
        PresetsTuningSection? TuningSection = null);

    public static class DashboardCardNavigation
    {
        public static bool TryResolve(
            DashboardCard card,
            out DashboardCardDestination destination)
        {
            switch (card)
            {
                case DashboardCard.Cpu:
                    destination = new("presets", PresetsTuningSection.Cpu);
                    return true;
                case DashboardCard.Gpu:
                    destination = new("presets", PresetsTuningSection.Gpu);
                    return true;
                case DashboardCard.Battery:
                    destination = new("battery");
                    return true;
                case DashboardCard.HydroUi:
                    destination = new("watercooler");
                    return true;
                case DashboardCard.Flydigi:
                    destination = new("flydigicooler");
                    return true;
                default:
                    destination = default;
                    return false;
            }
        }
    }
}
