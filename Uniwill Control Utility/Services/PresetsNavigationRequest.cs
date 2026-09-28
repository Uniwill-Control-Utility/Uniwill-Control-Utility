namespace Uniwill_Control_Utility.Services
{
    public enum PresetsTuningSection
    {
        Cpu,
        Gpu
    }

    /// <summary>
    /// Carries a one-shot request from the dashboard to the Presets page.
    /// </summary>
    public sealed class PresetsNavigationRequest
    {
        private readonly object _sync = new();
        private PresetsTuningSection? _pendingSection;

        public void Request(PresetsTuningSection section)
        {
            lock (_sync)
            {
                _pendingSection = section;
            }
        }

        public bool TryConsume(out PresetsTuningSection section)
        {
            lock (_sync)
            {
                if (_pendingSection is not PresetsTuningSection pendingSection)
                {
                    section = default;
                    return false;
                }

                section = pendingSection;
                _pendingSection = null;
                return true;
            }
        }
    }
}
