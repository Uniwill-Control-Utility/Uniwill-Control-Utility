using System.Collections.Generic;

namespace Uniwill_Control_Utility.Services
{
    internal static class DisplayTargetSelector
    {
        public static string? Choose(
            int internalDisplayIndex,
            IReadOnlyList<string> screenDeviceNames,
            string? primaryDeviceName)
        {
            if (internalDisplayIndex >= 0 && internalDisplayIndex < screenDeviceNames.Count)
                return screenDeviceNames[internalDisplayIndex];

            return primaryDeviceName;
        }
    }
}
