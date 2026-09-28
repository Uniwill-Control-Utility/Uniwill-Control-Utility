namespace Uniwill_Control_Utility.Views.Controls;

internal static class DashboardMemoryInfoFormatter
{
    public static string FormatModel(string manufacturer, string model)
    {
        string cleanManufacturer = Normalize(manufacturer);
        string cleanModel = Normalize(model);

        if (cleanModel.Length == 0)
            return cleanManufacturer.Length == 0 ? "Not available" : cleanManufacturer;

        if (cleanManufacturer.Length == 0 ||
            cleanModel.Contains(cleanManufacturer, System.StringComparison.OrdinalIgnoreCase))
        {
            return cleanModel;
        }

        return $"{cleanManufacturer} · {cleanModel}";
    }

    public static string FormatSpeed(int speed)
        => speed > 0 ? $"{speed} MT/s" : "Not available";

    private static string Normalize(string value)
        => string.Join(" ", value.Split(
            (char[]?)null,
            System.StringSplitOptions.RemoveEmptyEntries));
}
