namespace Uniwill_Control_Utility.Views.Controls;

internal static class DashboardMemoryInfoFormatter
{
    public static string FormatModel(string manufacturer, string model)
    {
        string cleanManufacturer = Normalize(manufacturer);
        string cleanModel = Normalize(model);

        string[] manufacturers = SplitAggregates(cleanManufacturer);
        string[] models = SplitAggregates(cleanModel);

        if (models.Length == 0)
            return manufacturers.Length == 0 ? "Not available" : string.Join(" / ", manufacturers);

        if (manufacturers.Length == 0)
            return string.Join(" / ", models);

        if (manufacturers.Length == 1)
            return ContainsManufacturer(models, manufacturers[0])
                ? string.Join(" / ", models)
                : $"{manufacturers[0]} · {string.Join(" / ", models)}";

        string[] identities = new string[models.Length];
        for (int index = 0; index < models.Length; index++)
        {
            string modelIdentity = models[index];
            if (ContainsManufacturer(new[] { modelIdentity }, manufacturers))
            {
                identities[index] = modelIdentity;
                continue;
            }

            identities[index] = index < manufacturers.Length
                ? $"{manufacturers[index]} {modelIdentity}"
                : modelIdentity;
        }

        return string.Join(" / ", identities);
    }

    public static string FormatSpeed(int speed)
        => speed > 0 ? $"{speed} MT/s" : "Not available";

    private static string Normalize(string value)
        => string.Join(" ", value.Split(
            (char[]?)null,
            System.StringSplitOptions.RemoveEmptyEntries));

    private static string[] SplitAggregates(string value)
        => value.Split(
            '/',
            System.StringSplitOptions.TrimEntries | System.StringSplitOptions.RemoveEmptyEntries);

    private static bool ContainsManufacturer(string[] models, params string[] manufacturers)
    {
        foreach (string model in models)
        {
            foreach (string manufacturer in manufacturers)
            {
                if (model.Contains(manufacturer, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
