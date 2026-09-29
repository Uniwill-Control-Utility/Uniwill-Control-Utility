using System.Collections.Generic;
using System.Linq;

namespace Uniwill_Control_Utility.Services
{
    internal static class TrayRefreshRateOptions
    {
        public static int[] Build(IEnumerable<int> enumeratedRates, int currentRefreshRate)
        {
            return enumeratedRates
                .Append(currentRefreshRate)
                .Where(rate => rate > 0)
                .Distinct()
                .OrderByDescending(rate => rate)
                .ToArray();
        }
    }
}
