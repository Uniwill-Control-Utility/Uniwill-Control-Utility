using System;
using System.Threading.Tasks;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Drives the EC turbo/full-fan state idempotently and verifies that the
    /// hardware-facing state changed after a command.
    /// </summary>
    internal sealed class FullFanController
    {
        private readonly Func<bool> _isActive;
        private readonly Func<Task> _toggleAsync;

        public FullFanController(Func<bool> isActive, Func<Task> toggleAsync)
        {
            _isActive = isActive;
            _toggleAsync = toggleAsync;
        }

        public bool IsActive => _isActive();

        public async Task SetAsync(bool enabled)
        {
            if (_isActive() == enabled)
                return;

            await _toggleAsync();

            if (_isActive() != enabled)
            {
                string requested = enabled ? "enter" : "leave";
                throw new InvalidOperationException($"Full fan mode did not {requested} the requested state.");
            }
        }
    }
}
